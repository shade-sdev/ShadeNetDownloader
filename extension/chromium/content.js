// Runs in every page and frame. When the page plays a video that ShadeNet can download, a small button appears on the
// video and says which video it is ("Download video - <name>"), so nobody has to open the toolbar popup to find out.
//
// What can be downloaded is decided by the extension's background page (it sees the network requests); this script only
// reports the <video> elements it finds (name, address, length, size) and draws the answer. It reads nothing else from the
// page and sends nothing anywhere but to the extension.

(() => {
  if (window.top !== window && window.innerWidth < 200) return;       // tiny frames (ads, trackers) have nothing to show
  if (globalThis.__shadenetContent) return;
  globalThis.__shadenetContent = true;

  const MIN_WIDTH = 200;
  const MIN_HEIGHT = 110;
  const POLL_MS = 1500;

  let items = [];            // what the extension says can be downloaded on this tab
  let overlay = true;        // the switch in the popup
  let dismissed = false;     // "x" on the button: hidden until the page is loaded again
  let lastReport = '';       // the last list of videos sent, so unchanged pages cost nothing
  let timer = 0;
  let host = null;
  let ui = null;
  let sentUntil = 0;

  const clean = (text) => String(text ?? '').replace(/\s+/g, ' ').trim();

  // ---- finding the videos ---------------------------------------------------------------------------------------

  // Players built as web components (Reddit's <shreddit-player>, ...) keep their <video> inside a shadow root, where the page's own
  // element list does not reach. Shadow roots are searched too, closed ones as well (an extension may look into those); the hosts are
  // looked up again at most every two seconds.
  let shadowHosts = [];
  let shadowCheckedAt = 0;

  function shadowOf(el) {
    try {
      return el.shadowRoot ?? chrome.dom?.openOrClosedShadowRoot?.(el) ?? null;
    } catch {
      return null;   // not an element that can have one
    }
  }

  function hostsOf(root, depth, out) {
    if (depth > 6) return;
    for (const el of root.querySelectorAll('*')) {
      const shadow = el.tagName.includes('-') || el.shadowRoot ? shadowOf(el) : null;   // only custom elements (and a few built-ins) have one
      if (shadow) {
        out.push(shadow);
        hostsOf(shadow, depth + 1, out);
      }
    }
  }

  function videoElements() {
    const found = Array.from(document.getElementsByTagName('video'));
    if (Date.now() - shadowCheckedAt > 2000) {
      shadowCheckedAt = Date.now();
      shadowHosts = [];
      hostsOf(document, 0, shadowHosts);
    }
    for (const root of shadowHosts) if (root.host?.isConnected) found.push(...root.querySelectorAll('video'));
    return found;
  }

  function isVisible(video) {
    const rect = video.getBoundingClientRect();
    if (rect.width < MIN_WIDTH || rect.height < MIN_HEIGHT) return false;
    if (rect.bottom < 0 || rect.right < 0 || rect.top > innerHeight || rect.left > innerWidth) return false;
    const style = getComputedStyle(video);
    if (style.visibility === 'hidden' || style.display === 'none') return false;
    // TikTok plays its video with `opacity: 0` and shows the picture another way, so a see-through <video> that has data and
    // fills the screen is still the video that plays. (A hidden player that has loaded nothing is not.)
    return Number(style.opacity) !== 0 || video.readyState >= 2;
  }

  /** The name of a video: its own label, else the heading of the card it sits in, else what the page calls itself. */
  function titleFor(video, several) {
    const own = clean(video.getAttribute('aria-label') || video.getAttribute('title') || video.dataset?.title);
    if (own.length > 2 && !/^(video|video player|media player|player|youtube video player)$/i.test(own)) return own;

    const described = describedBySite(video);
    if (described) return described;

    if (several) {
      const card = video.closest('article, figure, li, [role="article"]');
      const caption = clean(card?.querySelector('figcaption, h1, h2, h3, h4')?.textContent);
      if (caption.length > 2 && caption.length < 200) return caption;
    }

    if (/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) {
      // the watch page's heading, or the title the embedded player itself shows in its top bar
      const heading = clean(document.querySelector('ytd-watch-metadata h1, h1.ytd-watch-metadata, .ytp-title-link, .ytp-title-text')?.textContent);
      if (heading) return heading;
    }

    // the page's address bar title is always current; the social title is cleaner (no site name) when it matches it
    const pageTitle = clean(document.title);
    const social = clean(document.querySelector('meta[property="og:title"], meta[name="twitter:title"]')?.content);
    return social && pageTitle.includes(social) ? social : pageTitle || social;
  }

  // What the generic rules cannot find, a site's own page structure can: one entry per site. A site without an entry works from
  // the generic rules alone (the video's label, the links around it, the page's title). Nothing else in this file names a site.
  //   description   a selector for the text a card shows about its video (a better name than the page's title)
  //   playerId      a pattern for an id on the player's wrapper that holds the video's own id
  //   pageFromId    the address of a video's page, made from that id
  const SITE_HINTS = [
    {
      host: /(^|\.)tiktok\.com$/,
      description: '[data-e2e="video-desc"]',
      playerId: /^xgwrapper-\d+-(\d{8,})$/,
      pageFromId: (id) => `${location.origin}/@/video/${id}`,   // the author is not needed: the site and yt-dlp take /@/video/<id>
    },
  ];

  const hint = () => SITE_HINTS.find((h) => h.host.test(location.hostname));

  /** What the site's card says about this video, when the site is known for it. */
  function describedBySite(video) {
    const selector = hint()?.description;
    if (!selector) return '';
    for (let node = video.parentElement, depth = 0; node && depth < 12; node = node.parentElement, depth++) {
      const text = clean(node.querySelector(selector)?.textContent);
      if (text) return text.slice(0, 120);
    }
    return '';
  }

  /** The page of this video when the player's wrapper carries its id and the site is known for it. */
  function pageFromPlayerId(video) {
    const { playerId, pageFromId } = hint() ?? {};
    if (!playerId || !pageFromId) return '';
    for (let node = video.parentElement, depth = 0; node && depth < 6; node = node.parentElement, depth++) {
      const id = playerId.exec(node.id ?? '')?.[1];
      if (id) return pageFromId(id);
    }
    return '';
  }

  function sourceOf(video) {
    return video.currentSrc || video.src || video.querySelector('source')?.src || '';
  }

  // A player that feeds the video through a buffer (Facebook, Instagram ...) has a blob: address, which means nothing
  // outside this page. The page the video belongs to is then the only address, and the extension hands that to yt-dlp.
  const isBuffer = (video) => /^blob:/i.test(sourceOf(video)) || (!sourceOf(video) && !!video.srcObject);
  // an ending in `/videos/123`, `/reel/123`, `/status/123` or `?v=123`: a single video, not a feed
  const ONE_VIDEO = /\/(videos?|reels?|shorts?|status|clips?|p|posts|tv|watch|live|broadcasts?)\/[^/?#]+|\/live\/?$|[?&]v=|permalink\.php|\/video\.php/i;
  const permalinks = new WeakMap();

  /** The address of the post or page this video belongs to: its own address when the page is about it, else the nearest link to one. */
  function permalinkFor(video) {
    const cached = permalinks.get(video);
    if (cached && Date.now() - cached.at < 3000) return cached.value;

    let value = '';
    const here = location.href.split('#')[0];
    if (ONE_VIDEO.test(location.pathname + location.search) && !/^\/watch\/?$/i.test(location.pathname)) {
      value = here;
    } else {
      // a feed: the link to this video's own post sits in the card around it
      for (let node = video.parentElement, depth = 0; node && depth < 10 && !value; node = node.parentElement, depth++) {
        for (const a of node.querySelectorAll('a[href]')) {
          let url;
          try {
            url = new URL(a.href, location.href);
          } catch {
            continue;
          }
          if (url.origin === location.origin && ONE_VIDEO.test(url.pathname + url.search)) {
            value = url.href.split('#')[0];
            break;
          }
        }
      }
    }

    if (!value) value = pageFromPlayerId(video);

    permalinks.set(video, { at: Date.now(), value });
    return value;
  }

  function describe() {
    const videos = videoElements();
    return videos.slice(0, 12).map((video) => {
      const buffer = isBuffer(video);
      const rect = video.getBoundingClientRect();
      return {
        title: titleFor(video, videos.length > 1),
        src: sourceOf(video),
        duration: Number.isFinite(video.duration) ? Math.round(video.duration) : 0,
        live: video.duration === Infinity,                 // a live stream has no end: the player says so
        width: video.videoWidth || 0,
        height: video.videoHeight || 0,
        blob: buffer,
        drm: !!video.mediaKeys,                         // a player with a licence key: protected, left alone on purpose
        permalink: buffer ? permalinkFor(video) : '',
        box: [Math.round(rect.width), Math.round(rect.height)],      // with this, the extension can tell a hidden decoy from a video
        opacity: Number(getComputedStyle(video).opacity),
      };
    });
  }

  // ---- talking to the extension ---------------------------------------------------------------------------------

  function alive() {
    try {
      return !!chrome.runtime?.id;
    } catch {
      return false;
    }
  }

  async function ask(message) {
    try {
      return await chrome.runtime.sendMessage(message);
    } catch {
      return null;
    }
  }

  let lastError = '';   // for diagnosis: the last thing that went wrong here

  async function refresh(force = false) {
    try {
      await refreshOnce(force);
    } catch (error) {
      lastError = String(error?.stack ?? error).slice(0, 400);
    }
  }

  async function refreshOnce(force) {
    if (!alive()) return stop();
    const videos = describe();
    const report = JSON.stringify(videos);
    if (!force && report === lastReport) return render();
    if (videos.length === 0 && lastReport === '') return;   // nothing before, nothing now

    lastReport = report;
    const answer = await ask({ type: 'page-videos', videos });
    items = Array.isArray(answer?.items) ? answer.items : [];
    overlay = answer?.overlay !== false;
    render();
  }

  async function download(url) {
    setStatus('Sending…');
    const answer = await ask({ type: 'download-media', url });
    if (answer?.accepted) {
      sentUntil = Date.now() + 3000;
      setStatus('Sent to ShadeNet ✓');
    } else {
      sentUntil = Date.now() + 5000;
      setStatus(answer?.reason ? `Could not send: ${answer.reason}` : 'Could not send to ShadeNet');
    }
    setTimeout(render, 3200);
  }

  // ---- the button ------------------------------------------------------------------------------------------------

  const CSS = `
    :host { all: initial; position: fixed; z-index: 2147483647; font: 13px/1.3 system-ui, "Segoe UI", sans-serif; }
    .pill { display: flex; align-items: stretch; background: rgba(17, 22, 34, .94); color: #fff; border-radius: 10px;
            box-shadow: 0 4px 18px rgba(0,0,0,.45); overflow: hidden; max-width: min(380px, 92vw); border: 1px solid rgba(255,255,255,.14); }
    button { all: unset; box-sizing: border-box; cursor: pointer; }
    .main { display: flex; align-items: center; gap: 9px; padding: 8px 12px; min-width: 0; flex: 1; }
    .main:hover, .more:hover { background: #2563eb; }
    .main:focus-visible, .more:focus-visible, .x:focus-visible, .item:focus-visible { outline: 2px solid #93c5fd; outline-offset: -2px; }
    svg { flex: none; width: 18px; height: 18px; fill: none; stroke: #fff; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
    .txt { display: flex; flex-direction: column; min-width: 0; }
    .txt b { font-weight: 600; font-size: 12px; letter-spacing: .01em; }
    .txt span { color: #cbd5e1; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .more { padding: 0 9px; display: flex; align-items: center; border-left: 1px solid rgba(255,255,255,.18); font-size: 11px; gap: 3px; }
    .x { padding: 0 9px; display: flex; align-items: center; color: #94a3b8; border-left: 1px solid rgba(255,255,255,.18); font-size: 14px; }
    .x:hover { color: #fff; background: rgba(255,255,255,.12); }
    .menu { margin-top: 6px; background: rgba(17, 22, 34, .97); border-radius: 10px; border: 1px solid rgba(255,255,255,.14);
            box-shadow: 0 4px 18px rgba(0,0,0,.45); overflow: hidden; max-width: min(380px, 92vw); }
    .item { display: block; padding: 8px 12px; color: #fff; }
    .item:hover { background: #2563eb; }
    .item b { display: block; font-weight: 600; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .item span { display: block; color: #cbd5e1; font-size: 12px; }
    [hidden] { display: none !important; }
  `;

  function length(seconds) {
    if (!seconds) return '';
    const s = Math.round(seconds);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const rest = String(s % 60).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${rest}` : `${m}:${rest}`;
  }

  function facts(item) {
    const parts = [];
    if (item.kind === 'page') parts.push(item.guess ? 'not sure ShadeNet can read this site' : 'choose the quality in ShadeNet');
    else if (item.quality) parts.push(item.quality);
    else if (item.maxHeight) parts.push(item.kind === 'file' || item.audioUrl ? `${item.maxHeight}p` : `up to ${item.maxHeight}p`);
    if (item.audioUrl) parts.push('picture + sound');
    if (item.duration) parts.push(length(item.duration));
    if (item.live) parts.push('live');
    return parts.join(' · ');
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    Object.assign(node, props);
    node.append(...children);
    return node;
  }

  function icon() {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    const path = document.createElementNS(ns, 'path');
    path.setAttribute('d', 'M12 4v11m0 0-4.5-4.5M12 15l4.5-4.5M5 20h14');
    svg.append(path);
    return svg;
  }

  function build() {
    host = document.createElement('shadenet-download');
    const root = host.attachShadow({ mode: 'closed' });
    try {
      const sheet = new CSSStyleSheet();   // constructable: a page's Content-Security-Policy cannot block it (inline <style> can be)
      sheet.replaceSync(CSS);
      root.adoptedStyleSheets = [sheet];
    } catch {
      root.append(el('style', { textContent: CSS }));
    }

    const label = el('b');
    const name = el('span');
    const main = el('button', { type: 'button' }, icon(), el('span', { className: 'txt' }, label, name));
    main.className = 'main';
    const more = el('button', { type: 'button', title: 'More than one video here: choose' }, '▾');
    more.className = 'more';
    const close = el('button', { type: 'button', title: 'Hide this button on this page', textContent: '✕' });
    close.className = 'x';
    const pill = el('div', { className: 'pill' }, main, more, close);
    const menu = el('div', { className: 'menu', hidden: true });
    root.append(pill, menu);

    main.addEventListener('click', () => ui.primary && download(ui.primary.url));
    more.addEventListener('click', () => { menu.hidden = !menu.hidden; });
    close.addEventListener('click', () => { dismissed = true; render(); });
    ui = { root, label, name, main, more, menu, pill, primary: null, shown: '' };
    for (const type of ['click', 'mousedown', 'mouseup', 'pointerdown', 'pointerup', 'dblclick', 'keydown']) {
      host.addEventListener(type, (event) => event.stopPropagation());   // the player under it must not react to our clicks
    }
  }

  function setStatus(text) {
    if (ui) ui.name.textContent = text;
  }

  /** The largest video on the screen: the button goes on it. */
  function mainVideo() {
    let best = null;
    let bestArea = 0;
    for (const video of videoElements()) {
      if (!isVisible(video)) continue;
      const rect = video.getBoundingClientRect();
      const area = rect.width * rect.height;
      if (area > bestArea) { best = video; bestArea = area; }
    }
    return best;
  }

  function render() {
    const video = overlay && !dismissed && items.length > 0 ? mainVideo() : null;
    if (!video) {
      if (host?.isConnected) host.remove();
      return;
    }

    if (!host) build();
    const parent = document.fullscreenElement && document.fullscreenElement.tagName !== 'VIDEO' ? document.fullscreenElement : document.documentElement;
    if (host.parentNode !== parent) parent.append(host);

    // the one this very video plays when the extension knows it (a page with several videos), else the first
    const own = [sourceOf(video), isBuffer(video) ? permalinkFor(video) : ''];
    const primary = items.find((item) => own.includes(item.url)) ?? items[0];
    ui.primary = primary;
    const sending = Date.now() < sentUntil;
    if (!sending) {
      ui.label.textContent = primary.live ? 'Record this video' : primary.audioOnly ? 'Download the sound' : primary.guess ? 'Try to download this video' : primary.kind === 'page' ? 'Download video (choose quality)' : 'Download video';
      ui.name.textContent = [primary.title, facts(primary)].filter(Boolean).join(' · ');
    }
    ui.main.title = `Download “${primary.title}” with ShadeNet`;

    ui.more.hidden = items.length < 2;
    ui.more.textContent = `${items.length} ▾`;
    const signature = JSON.stringify(items.map((i) => [i.url, i.title]));
    if (ui.shown !== signature) {
      ui.shown = signature;
      ui.menu.replaceChildren(...items.map((item) => {
        const row = el('button', { type: 'button', title: item.url }, el('b', { textContent: item.title }), el('span', { textContent: [item.live ? 'Record' : 'Download', facts(item)].filter(Boolean).join(' · ') }));
        row.className = 'item';
        row.addEventListener('click', () => { ui.menu.hidden = true; download(item.url); });
        return row;
      }));
    }
    if (items.length < 2) ui.menu.hidden = true;

    const rect = video.getBoundingClientRect();
    const width = host.offsetWidth || 260;
    host.style.top = `${Math.max(rect.top, 0) + 12}px`;
    host.style.left = `${Math.max(Math.min(rect.right, innerWidth) - width - 12, 4)}px`;
  }

  // ---- upkeep ----------------------------------------------------------------------------------------------------

  function stop() {
    clearInterval(timer);
    host?.remove();
  }

  function start() {
    timer = setInterval(() => { if (document.visibilityState === 'visible') refresh(); }, POLL_MS);
    for (const type of ['loadedmetadata', 'loadeddata', 'play', 'emptied']) document.addEventListener(type, () => refresh(), true);
    addEventListener('scroll', () => render(), { passive: true, capture: true });
    addEventListener('resize', () => render(), { passive: true });
    document.addEventListener('fullscreenchange', () => render());
    document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') refresh(true); });
    chrome.runtime.onMessage.addListener((message, _sender, reply) => {
      if (message?.type === 'media-changed') refresh(true);
      // diagnose(): what this page's script sees (it answers for the page itself, not for its frames)
      if (message?.type === 'page-script-state' && window.top === window) {
        let found = -1;
        try {
          found = videoElements().length;
        } catch (error) {
          lastError = String(error?.stack ?? error).slice(0, 400);
        }
        reply({ videosFound: found, shadowRoots: shadowHosts.length, videosReported: lastReport ? JSON.parse(lastReport).length : 0, itemsAnswered: items.length, buttonShown: !!host?.isConnected, lastError });
      }
    });
    refresh();
  }

  start();
})();
