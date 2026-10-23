// The extension's behaviour, with every browser API injected: `new Controller({ chrome, link, recorder, userAgent })`.
// background.js wires the real ones; test/controller.test.mjs drives it with fakes.
//
// Hand-over of a browser download follows IDM's sequence (report §13.8): pause the browser's own download, ask the
// app, then cancel+erase it if the app took it over, or resume it if not. A failure of any kind therefore never loses
// the user's download.

import {
  basename, buildCapture, cookiesToHeader, evaluate, headerValue, isCapturableUrl, pickHeaders,
} from './capture.js';
import { MediaTracker, looksLikePlaylist, pairStreams } from './media.js';
import { TracksTracker, cleanTitle, describeTracks, extensionFor, heightOf, isVideoPage } from './youtube.js';
import { browserTimers } from './native.js';
import { acceptPageAddress, embedWatchUrl, isDecoyVideo, isPageSite, looksLikeOneVideo } from './sites.js';

export const DEFAULT_SETTINGS = { enabled: true, excludedHosts: [], minSizeBytes: 0, pageSignIn: true };

const CONNECT_TIMEOUT_MS = 10000;   // the app may have to be started
const ANSWER_TIMEOUT_MS = 8000;
const MENU_LINK = 'shadenet-link';

let nextRuleId = 9000;

/**
 * Reads a playlist with the browser's own cookies; gives up after a few seconds.
 *
 * A player served from another site (an iframe) often answers only requests that look like the player's own: with its
 * `Referer` and `Origin`. A request from the extension carries neither, so for this one request those two headers are set
 * to what the player sent (a short-lived rule of the browser's declarativeNetRequest, removed right after).
 */
export function defaultFetchText(url, recorded, chromeApi = globalThis.chrome, fetchImpl = (...a) => fetch(...a)) {
  return fetchAs(url, recorded, chromeApi, fetchImpl, {}, async (response) => (response.ok ? response.text() : ''));
}

/** The first `max` bytes of a piece (a range request; a server that ignores the range is cut off after `max` bytes). */
export function defaultFetchHead(url, recorded, chromeApi = globalThis.chrome, fetchImpl = (...a) => fetch(...a), max = 64 * 1024) {
  return fetchAs(url, recorded, chromeApi, fetchImpl, { headers: { Range: `bytes=0-${max - 1}` } }, async (response) => {
    if (!response.ok) return new Uint8Array(0);
    const reader = response.body?.getReader?.();
    if (!reader) return new Uint8Array(await response.arrayBuffer()).subarray(0, max);

    const chunks = [];
    let total = 0;
    while (total < max) {
      const { done, value } = await reader.read();
      if (done) break;
      chunks.push(value);
      total += value.length;
    }
    reader.cancel().catch(() => {});
    const all = new Uint8Array(Math.min(total, max));
    let at = 0;
    for (const chunk of chunks) {
      const part = chunk.subarray(0, Math.max(0, all.length - at));
      all.set(part, at);
      at += part.length;
    }
    return all;
  });
}

async function fetchAs(url, recorded, chromeApi, fetchImpl, init, read) {
  const referer = headerValue(recorded, 'referer');
  const origin = headerValue(recorded, 'origin');
  const rules = chromeApi?.declarativeNetRequest?.updateSessionRules;
  const ruleId = (referer || origin) && rules ? nextRuleId++ : 0;

  if (ruleId) {
    const requestHeaders = [];
    if (referer) requestHeaders.push({ header: 'referer', operation: 'set', value: referer });
    if (origin) requestHeaders.push({ header: 'origin', operation: 'set', value: origin });
    try {
      await rules.call(chromeApi.declarativeNetRequest, {
        addRules: [{
          id: ruleId,
          priority: 1,
          action: { type: 'modifyHeaders', requestHeaders },
          condition: {
            regexFilter: '^' + url.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$',
            resourceTypes: ['xmlhttprequest'],
            initiatorDomains: [chromeApi.runtime.id],
          },
        }],
      });
    } catch {
      // without the rule the plain request is still worth a try
    }
  }

  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), 6000);
  try {
    return await read(await fetchImpl(url, { credentials: 'include', signal: abort.signal, ...init }));
  } finally {
    clearTimeout(timer);
    if (ruleId) await rules.call(chromeApi.declarativeNetRequest, { removeRuleIds: [ruleId] }).catch(() => {});
  }
}

/** What the toolbar button and the page's button count: a YouTube page already includes the picture and sound it is playing. */
export function shownItems(items) {
  const visible = items.filter((i) => !i.protected);
  return visible.some((i) => i.kind === 'page') ? visible.filter((i) => i.kind !== 'tracks') : visible;
}

/** The toolbar button's tooltip: how many videos, and their names. */
export function tooltipFor(items) {
  if (items.length === 0) return 'ShadeNet Downloader';
  const names = items.slice(0, 5).map((i) => `• ${i.title.length > 70 ? i.title.slice(0, 69) + '…' : i.title}`);
  if (items.length > 5) names.push(`• and ${items.length - 5} more`);
  return `ShadeNet Downloader\n${items.length === 1 ? '1 video can be downloaded' : `${items.length} videos can be downloaded`}:\n${names.join('\n')}`;
}

export class Controller {
  constructor({ chrome, link, recorder, userAgent, timers = browserTimers, fetchText = defaultFetchText, fetchHead = defaultFetchHead }) {
    this.chrome = chrome;
    this.link = link;
    this.recorder = recorder;
    this.userAgent = userAgent;
    this.timers = timers;
    this.appSettings = { ...DEFAULT_SETTINGS };
    this.localEnabled = true;
    this.badgeTimer = null;
    this.media = new MediaTracker({
      fetchText: (url) => fetchText(url, this.recorder?.lookup(url), this.chrome),
      // a piece is asked for the way its PLAYLIST was (the player's own Referer and Origin)
      fetchHead: (url, via) => fetchHead(url, this.recorder?.lookup(via), this.chrome),
      onChange: (tabId) => this.updateMediaBadge(tabId),
    });
    this.tracks = new TracksTracker({ onChange: (tabId) => this.updateMediaBadge(tabId) });
    this.handling = new Map();   // download id -> the hand-over in progress (or done), so each download is handled once
    this.overlayEnabled = true;  // the "Download" button on top of videos (the popup has the switch)
    this.pageVideos = new Map(); // tab id -> Map(frame id -> { url, videos }): what each frame (the page, or an iframe in it) reports
    this.extraSites = [];        // more sites whose pages yt-dlp may be asked about (the built-in list is in lib/sites.js)
    this.events = [];   // the last things that happened, for diagnosis ("why was my download not taken over?")
    this.ready = this.loadLocal();
  }

  /** Keeps the last 40 steps, newest last. Never throws. */
  trace(text) {
    this.events.push(`${new Date().toISOString().slice(11, 23)} ${text}`);
    if (this.events.length > 40) this.events.shift();
  }

  async loadLocal() {
    try {
      const stored = await this.chrome.storage?.local?.get?.({ enabled: true, overlay: true });
      this.localEnabled = stored?.enabled !== false;
      this.overlayEnabled = stored?.overlay !== false;
    } catch {
      this.localEnabled = true;
      this.overlayEnabled = true;
    }
  }

  /** Called when the app sends `hello-ack` or `settings`. */
  applyAppSettings(message) {
    this.appSettings = {
      enabled: message.enabled !== false,
      excludedHosts: Array.isArray(message.excludedHosts) ? message.excludedHosts : [],
      minSizeBytes: Number.isFinite(message.minSizeBytes) ? message.minSizeBytes : 0,
      pageSignIn: message.pageSignIn !== false,
    };
  }

  effectiveSettings() {
    return { ...this.appSettings, enabled: this.appSettings.enabled && this.localEnabled };
  }

  /** Listeners must be registered synchronously when the service worker starts, so this does no awaiting. */
  registerListeners() {
    const c = this.chrome;
    c.downloads.onCreated.addListener((item) => this.onDownloadCreated(item));
    // The browser's "Save as" dialog opens when the file name has been settled. Holding that step until the app has
    // answered is what keeps the dialog from ever showing for a download ShadeNet takes over.
    c.downloads.onDeterminingFilename?.addListener((item, suggest) => this.onDeterminingFilename(item, suggest));
    // video streams the page plays, for the popup's "Videos on this page" list
    c.webRequest?.onHeadersReceived?.addListener(
      (details) => {
        this.tracks.consider(details);
        return this.media.consider(details);
      },
      { urls: ['<all_urls>'], types: ['xmlhttprequest', 'media', 'other'] },
      ['responseHeaders'],
    );
    c.tabs?.onUpdated?.addListener((tabId, change) => {
      if (change.status === 'loading') {
        this.media.clear(tabId);
        this.tracks.clear(tabId);
        this.pageVideos.delete(tabId);
      }
    });
    c.tabs?.onRemoved?.addListener((tabId) => {
      this.media.clear(tabId);
      this.tracks.clear(tabId);
      this.pageVideos.delete(tabId);
    });
    c.contextMenus?.onClicked?.addListener((info, tab) => this.onContextMenu(info, tab));
    c.runtime.onInstalled?.addListener(() => this.createMenus());
    c.runtime.onMessage?.addListener((message, sender, sendResponse) => this.onPopupMessage(message, sendResponse, sender));
  }

  createMenus() {
    const menus = this.chrome.contextMenus;
    if (!menus?.create) return;
    menus.removeAll?.(() => {
      menus.create({ id: MENU_LINK, title: 'Download with ShadeNet', contexts: ['link', 'video', 'audio'] });
    });
  }

  // ---- a download the browser started ------------------------------------------------------------------------

  /** The browser is about to settle the file name (and maybe ask where to save): it waits until `suggest` is called. */
  onDeterminingFilename(item, suggest) {
    this.trace(`download ${item.id} waiting for a file name`);
    this.hold(item)
      .catch(() => {})
      .finally(() => {
        try {
          suggest();   // no argument: the browser's own name and folder; if the download was taken over it is gone anyway
        } catch {
          // the download is gone
        }
        this.trace(`download ${item.id} released the file name step`);
      });
    return true;   // the answer comes later
  }

  onDownloadCreated(item) {
    return this.hold(item);
  }

  /** Both events concern the same download; whichever comes first starts the hand-over, the other joins it. */
  hold(item) {
    let work = this.handling.get(item.id);
    if (!work) {
      work = this.handOver(item);
      this.handling.set(item.id, work);
      if (this.handling.size > 100) this.handling.delete(this.handling.keys().next().value);
    }
    return work;
  }

  async handOver(item) {
    await this.ready;
    const decision = evaluate(item, this.effectiveSettings());
    this.trace(`download ${item.id} created (${item.state}) ${item.finalUrl || item.url} -> ${decision.capture ? 'capture' : 'leave: ' + decision.reason}`);
    if (!decision.capture) return;

    await this.safely(() => this.chrome.downloads.pause(item.id));
    this.trace(`download ${item.id} paused`);
    let handedOver = false;
    let leaveAlone = false;
    try {
      // it may already have finished while we were deciding: then there is nothing left to take over
      const [current] = (await this.chrome.downloads.search({ id: item.id })) ?? [];
      if (current && current.state === 'complete') {
        this.trace(`download ${item.id} already complete: left alone`);
        leaveAlone = true;
        return;
      }

      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      this.trace(`connected to the app ${this.link.appVersion}`);
      if (!evaluate(item, this.effectiveSettings()).capture) {
        this.trace('the app settings say: leave it');
        return;   // the app's settings just arrived
      }

      const url = item.finalUrl || item.url;
      const message = await this.describe({
        url,
        originalUrl: item.url,
        referrer: item.referrer,
        fileName: basename(item.filename),
        mimeType: item.mime,
        size: item.totalBytes,
        source: 'download',
      });

      this.trace(`sent capture ${message.id}: cookies=${message.cookies ? message.cookies.length + ' chars' : 'none'} referrer=${message.referrer ?? 'none'}`);
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.trace(`answer: accepted=${result?.accepted} reason=${result?.reason}`);
      if (result?.accepted) {
        handedOver = true;
        await this.safely(() => this.chrome.downloads.cancel(item.id));
        await this.safely(() => this.chrome.downloads.erase({ id: item.id }));
        this.trace(`download ${item.id} cancelled and erased`);
        this.flash('✓', '#2e9d57');
      }
    } catch (error) {
      this.trace(`hand-over failed: ${error?.message ?? error}`);
      this.flash('!', '#c4452d');
    } finally {
      // anything but a successful hand-over: the browser carries on with its own download
      if (!handedOver && !leaveAlone) await this.safely(() => this.chrome.downloads.resume(item.id));
    }
  }

  // ---- "Download with ShadeNet" in the context menu ----------------------------------------------------------

  async onContextMenu(info, tab) {
    await this.ready;
    if (info.menuItemId !== MENU_LINK) return;
    const url = info.linkUrl || info.srcUrl;
    if (!isCapturableUrl(url)) return;

    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const message = await this.describe({
        url,
        referrer: info.pageUrl || tab?.url,
        source: 'contextmenu',
      });
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.flash(result?.accepted ? '✓' : '!', result?.accepted ? '#2e9d57' : '#c4452d');
    } catch {
      this.flash('!', '#c4452d');
    }
  }

  // ---- shared -------------------------------------------------------------------------------------------------

  /** Everything the app needs to repeat the request: the cookies and headers the site saw, the page, the agent. */
  async describe({ url, lookupUrl, originalUrl, referrer, referrerFallback, fileName, mimeType, size, source, audioUrl, audioSize, duration }) {
    const recorded = this.recorder?.lookup(lookupUrl ?? url);
    let cookies = headerValue(recorded, 'cookie');
    if (!cookies) {
      const list = await this.safely(() => this.chrome.cookies.getAll({ url }));
      cookies = cookiesToHeader(list);
    }

    return buildCapture({
      id: this.link.newId(),
      url,
      originalUrl,
      referrer: referrer || headerValue(recorded, 'referer') || referrerFallback,
      cookies,
      userAgent: headerValue(recorded, 'user-agent') || this.userAgent,
      headers: pickHeaders(recorded),
      fileName,
      mimeType,
      size,
      source,
      audioUrl,
      audioSize,
      duration,
    });
  }

  async safely(action) {
    try {
      return await action();
    } catch {
      return undefined;   // the download may be gone already; none of these calls is worth failing the hand-over for
    }
  }

  flash(text, color) {
    const action = this.chrome.action;
    if (!action?.setBadgeText) return;
    action.setBadgeBackgroundColor?.({ color });
    action.setBadgeText({ text });
    if (this.badgeTimer) this.timers.clearTimeout(this.badgeTimer);
    this.badgeTimer = this.timers.setTimeout(() => action.setBadgeText({ text: '' }), 2500);
  }

  // ---- video streams on the page -------------------------------------------------------------------------------

  /**
   * The toolbar button says how many videos the tab has and names them (badge number and tooltip), and tells the page's
   * own "Download" button (content.js) to look again.
   */
  async updateMediaBadge(tabId) {
    const action = this.chrome.action;
    if (!action?.setBadgeText) return;
    const tab = await this.safely(() => this.chrome.tabs.get(tabId));
    const items = tab ? shownItems(await this.itemsFor(tab)) : [];
    action.setBadgeBackgroundColor?.({ tabId, color: '#2563eb' });
    action.setBadgeText({ tabId, text: items.length > 0 ? String(items.length) : '' });
    action.setTitle?.({ tabId, title: tooltipFor(items) });
    this.safely(() => this.chrome.tabs.sendMessage?.(tabId, { type: 'media-changed' })?.catch?.(() => {}));
  }

  async activeTab() {
    const [tab] = (await this.chrome.tabs?.query?.({ active: true, currentWindow: true })) ?? [];
    return tab;
  }

  /** The name of the video(s) on a tab: what the page itself calls its video, else the tab's title. */
  pageTitle(tab) {
    for (const { videos } of this.pageVideos.get(tab.id)?.values() ?? []) {
      const named = videos.find((v) => v.title);
      if (named) return cleanTitle(named.title);
    }
    return cleanTitle(tab.title);
  }

  /** Plain video files (`<video src="https://…/clip.mp4">`) that frames of the tab report. */
  fileVideos(tabId) {
    const found = [];
    for (const { videos } of this.pageVideos.get(tabId)?.values() ?? []) {
      for (const v of videos) {
        if (typeof v.src !== 'string' || !/^https?:\/\//i.test(v.src) || looksLikePlaylist(v.src, '')) continue;
        if (this.media.get(tabId, v.src) || found.some((f) => f.src === v.src)) continue;
        found.push(v);
      }
    }
    return found;
  }

  /**
   * The address of the page a player lives in. A request says only which ORIGIN it came from; the frame that reported its
   * video says the whole address, which is what a site that checks the Referer expects.
   */
  playerPage(tab, origin) {
    if (!origin) return '';
    for (const { url } of this.pageVideos.get(tab.id)?.values() ?? []) {
      try {
        if (new URL(url).origin === new URL(origin).origin) return url;
      } catch {
        // not an address
      }
    }
    return /^https?:\/\/[^/]+\/./.test(origin) ? origin : '';   // already a full address (documentUrl)
  }

  /** The videos of this tab that are downloaded by giving their PAGE to yt-dlp. */
  pageItems(tab, name, haveOthers) {
    const pages = [];
    const add = (url, title, guess = false, live = false) => {
      if (!url) return;
      const known = pages.find((p) => p.url === url);
      if (known) {
        known.live ||= live;
        return;
      }
      pages.push({ url, kind: 'page', live, protected: false, duration: 0, maxHeight: 0, title, guess, quality: guess ? 'ShadeNet will try' : 'qualities chosen in ShadeNet' });
    };

    if (isVideoPage(tab.url)) add(tab.url, name);

    for (const { url: frameUrl, videos } of this.pageVideos.get(tab.id)?.values() ?? []) {
      const embedded = embedWatchUrl(frameUrl);
      if (embedded && videos.length > 0) add(embedded, videos.find((v) => v.title)?.title ? cleanTitle(videos.find((v) => v.title).title) : name);
      if (haveOthers) continue;   // a stream or file was found: that is what this video is

      for (const v of videos) {
        if (!v.blob || v.drm) continue;   // protected players are left alone on purpose
        // A live stream has no page of its own to link to: the page it plays on (a user's /live page) is its address.
        const address = acceptPageAddress(v.permalink, this.extraSites)
          || (isPageSite(tab.url, this.extraSites) && (looksLikeOneVideo(tab.url) || v.live) ? acceptPageAddress(tab.url, this.extraSites) : '');
        const title = v.title ? cleanTitle(v.title) : name;
        if (address) {
          add(address, title, false, v.live);
        } else if (!embedded && !isPageSite(frameUrl, this.extraSites) && /^https?:\/\//i.test(frameUrl)) {
          // a site yt-dlp is not known for, and nothing on the network that names the video: the frame's own page is the one thing
          // left to try. It may not work; the button says so, and the dialog explains if yt-dlp cannot read it.
          add(frameUrl, title, true);
        }
      }
    }

    return pages;
  }

  /** Everything on the tab that can be downloaded, each with the name of its video. */
  async itemsFor(tab) {
    const name = this.pageTitle(tab);
    // (picture and sound that a player asked for separately are ONE entry here, which downloads both)
    const items = pairStreams(this.media.list(tab.id)).map((m) => ({
      url: m.url, kind: m.kind, live: m.live, protected: !!m.protected, duration: m.duration, maxHeight: m.maxHeight, title: name,
      ...(m.audioUrl ? { audioUrl: m.audioUrl } : {}), ...(m.audioOnly ? { audioOnly: true } : {}),
    }));

    for (const v of this.fileVideos(tab.id)) {
      items.push({
        url: v.src, kind: 'file', live: false, protected: false, duration: v.duration || 0, maxHeight: v.height || 0,
        title: v.title ? cleanTitle(v.title) : name,
      });
    }

    // pages ShadeNet asks yt-dlp about: a YouTube page (the player's own requests cannot be used any more), a YouTube player
    // embedded in another page, and videos whose picture comes through a buffer (Facebook, Instagram ...: no address at all)
    const pages = this.pageItems(tab, name, items.length > 0 || this.media.list(tab.id).length > 0);
    for (const page of pages.reverse()) items.unshift(page);

    // picture and sound the page's player is fetching as files (some other sites)
    const playing = this.tracks.current(tab.id);
    if (playing) {
      items.unshift({
        url: playing.video.url, kind: 'tracks', live: playing.video.live, protected: playing.video.drm,
        duration: playing.video.dur, maxHeight: heightOf(playing.video), title: name,
        quality: describeTracks(playing),
      });
    }

    return items;
  }

  /** The videos found on the tab the popup was opened on. */
  async listMedia() {
    const tab = await this.activeTab();
    if (!tab) return { items: [] };
    return { tabId: tab.id, items: await this.itemsFor(tab) };
  }

  /** A page's frame reports its <video> elements: remember them, answer with what can be downloaded (for its button). */
  async onPageVideos(sender, videos) {
    const tab = sender?.tab;
    if (!tab || !Number.isInteger(tab.id)) return { items: [], overlay: false };
    const clean = (Array.isArray(videos) ? videos : []).slice(0, 12).map((v) => ({
      title: String(v?.title ?? '').slice(0, 300),
      src: String(v?.src ?? '').slice(0, 4000),
      duration: Number.isFinite(v?.duration) ? v.duration : 0,
      width: Number.isFinite(v?.width) ? v.width : 0,
      height: Number.isFinite(v?.height) ? v.height : 0,
      blob: !!v?.blob,
      live: !!v?.live,
      drm: !!v?.drm,
      permalink: String(v?.permalink ?? '').slice(0, 2000),
      box: Array.isArray(v?.box) ? v.box.slice(0, 2).map((n) => (Number.isFinite(n) ? n : 0)) : [0, 0],
      opacity: Number.isFinite(v?.opacity) ? v.opacity : 1,
    })).filter((v) => !isDecoyVideo(v));

    const frames = this.pageVideos.get(tab.id) ?? new Map();
    const before = JSON.stringify([...frames.entries()]);
    frames.set(sender.frameId ?? 0, { url: String(sender.url ?? tab.url ?? ''), videos: clean });
    this.pageVideos.set(tab.id, frames);
    if (JSON.stringify([...frames.entries()]) !== before) this.updateMediaBadge(tab.id);

    return { items: shownItems(await this.itemsFor(tab)), overlay: this.overlayEnabled };
  }

  /** "Download" next to a stream in the popup: the same hand-over as a captured download, without a browser download to cancel. */
  async sendMedia(url, fromTab) {
    const tab = fromTab ?? await this.activeTab();
    const page = tab ? (await this.itemsFor(tab)).find((i) => i.kind === 'page' && i.url === url) : null;
    if (page) return this.sendPage(tab, page);
    const playing = tab ? this.tracks.current(tab.id) : null;
    if (playing && playing.video.url === url) return this.sendTracks(tab, playing);
    const file = tab ? this.fileVideos(tab.id).find((v) => v.src === url) : null;
    if (file) return this.sendFile(tab, file);
    if (!tab || !this.media.get(tab.id, url)) return { accepted: false, reason: 'unknown-stream' };

    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      // the player's own page (an iframe's address) is the referrer the pieces are expected with, not the outer page
      const referrerFallback = this.playerPage(tab, this.media.get(tab.id, url)?.referrer) || tab.url;
      const audioUrl = (await this.itemsFor(tab)).find((i) => i.url === url)?.audioUrl;   // the sound's own playlist, when it has one
      const recording = this.media.get(tab.id, url)?.kind === 'record';   // a live FLV pull: the app records it with ffmpeg
      const message = await this.describe({ url, referrerFallback, fileName: this.pageTitle(tab), source: recording ? 'record' : 'media', audioUrl });
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.trace(`stream ${url} sent: accepted=${result?.accepted} reason=${result?.reason}`);
      this.flash(result?.accepted ? '✓' : '!', result?.accepted ? '#2e9d57' : '#c4452d');
      return { accepted: !!result?.accepted, reason: result?.reason };
    } catch (error) {
      this.trace(`stream hand-over failed: ${error?.message ?? error}`);
      this.flash('!', '#c4452d');
      return { accepted: false, reason: String(error?.message ?? error) };
    }
  }

  /**
   * The address of a video page goes to the app, which shows its Add dialog and asks yt-dlp. Live streams and private videos can
   * only be read by a signed-in session, so the page's own site's cookies go along (the switch in the app's Settings > Browser turns
   * that off); the app gives them to yt-dlp in a temporary file and never to the file servers.
   */
  async sendPage(tab, page) {
    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const cookies = this.appSettings.pageSignIn
        ? cookiesToHeader(await this.safely(() => this.chrome.cookies.getAll({ url: page.url })))
        : '';
      const message = buildCapture({
        id: this.link.newId(), url: page.url, referrer: tab.url, cookies, userAgent: this.userAgent, fileName: page.title || cleanTitle(tab.title), source: 'page',
      });
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.trace(`page sent: accepted=${result?.accepted} reason=${result?.reason}`);
      this.flash(result?.accepted ? '✓' : '!', result?.accepted ? '#2e9d57' : '#c4452d');
      return { accepted: !!result?.accepted, reason: result?.reason };
    } catch (error) {
      this.trace(`page hand-over failed: ${error?.message ?? error}`);
      this.flash('!', '#c4452d');
      return { accepted: false, reason: String(error?.message ?? error) };
    }
  }

  /** A plain video file the page plays: the app asks or starts as set up, and names the file after the video. */
  async sendFile(tab, file) {
    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const extension = /\.(mp4|webm|mkv|mov|m4v|ogv|ogg)(?=$|[?#])/i.exec(new URL(file.src).pathname + '#')?.[1]?.toLowerCase() ?? 'mp4';
      const message = await this.describe({
        url: file.src,
        referrer: tab.url,
        fileName: `${(file.title ? cleanTitle(file.title) : this.pageTitle(tab)).replace(/[.\s]+$/, '') || 'video'}.${extension}`,   // "Title." + ".mp4" would be "Title..mp4"
        source: 'video',
      });
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.trace(`video file sent: accepted=${result?.accepted} reason=${result?.reason}`);
      this.flash(result?.accepted ? '✓' : '!', result?.accepted ? '#2e9d57' : '#c4452d');
      return { accepted: !!result?.accepted, reason: result?.reason };
    } catch (error) {
      this.trace(`video file hand-over failed: ${error?.message ?? error}`);
      this.flash('!', '#c4452d');
      return { accepted: false, reason: String(error?.message ?? error) };
    }
  }

  /** The picture and sound a player is fetching, handed over as two files for the app to download and join. */
  async sendTracks(tab, playing) {
    const { video, audio } = playing;
    if (video.live) return { accepted: false, reason: 'live-not-supported' };
    if (video.drm) return { accepted: false, reason: 'protected' };

    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const message = await this.describe({
        url: video.url,
        lookupUrl: video.source,
        referrer: tab.url,
        fileName: cleanTitle(tab.title) + extensionFor(playing),
        mimeType: video.mime,
        size: video.clen,
        source: 'tracks',
        audioUrl: audio?.url,
        audioSize: audio?.clen,
        duration: video.dur || audio?.dur,
      });
      const result = await this.link.request(message, ANSWER_TIMEOUT_MS);
      this.trace(`tracks sent: itag ${video.itag}${audio ? ' + ' + audio.itag : ''} accepted=${result?.accepted} reason=${result?.reason}`);
      this.flash(result?.accepted ? '✓' : '!', result?.accepted ? '#2e9d57' : '#c4452d');
      return { accepted: !!result?.accepted, reason: result?.reason };
    } catch (error) {
      this.trace(`tracks hand-over failed: ${error?.message ?? error}`);
      this.flash('!', '#c4452d');
      return { accepted: false, reason: String(error?.message ?? error) };
    }
  }

  // ---- popup ---------------------------------------------------------------------------------------------------

  /** Looks whether the app is running, without starting it (the popup and the browser's start-up do this). */
  async probe() {
    return this.link.probe();
  }

  /** Messages from the popup and from the pages' content script (sender.tab is set for the latter). */
  onPopupMessage(message, sendResponse, sender) {
    const reply = async () => {
      await this.ready;
      if (message?.type === 'set-enabled') {
        this.localEnabled = !!message.value;
        await this.safely(() => this.chrome.storage.local.set({ enabled: this.localEnabled }));
      } else if (message?.type === 'set-overlay') {
        this.overlayEnabled = !!message.value;
        await this.safely(() => this.chrome.storage.local.set({ overlay: this.overlayEnabled }));
        // every open page learns the new choice now, not at its next change
        const tabs = (await this.safely(() => this.chrome.tabs.query({}))) ?? [];
        for (const tab of tabs) this.safely(() => this.chrome.tabs.sendMessage?.(tab.id, { type: 'media-changed' })?.catch?.(() => {}));
      } else if (message?.type === 'connect') {
        await this.link.ensureConnected(CONNECT_TIMEOUT_MS);
      } else if (message?.type === 'probe') {
        await this.probe();
      } else if (message?.type === 'list-media') {
        return { ...(await this.listMedia()) };
      } else if (message?.type === 'page-videos') {
        return { ...(await this.onPageVideos(sender, message.videos)) };
      } else if (message?.type === 'download-media') {
        return { ...(await this.sendMedia(message.url, sender?.tab)) };
      }
      return { ...this.link.status(), localEnabled: this.localEnabled, appEnabled: this.appSettings.enabled, overlayEnabled: this.overlayEnabled };
    };
    reply().then(sendResponse);
    return true;   // the answer is asynchronous
  }
}
