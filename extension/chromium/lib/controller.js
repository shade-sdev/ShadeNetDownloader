// The extension's behaviour, with every browser API injected: `new Controller({ chrome, link, recorder, userAgent })`.
// background.js wires the real ones; test/controller.test.mjs drives it with fakes.
//
// Hand-over of a browser download follows IDM's sequence (report §13.8): pause the browser's own download, ask the
// app, then cancel+erase it if the app took it over, or resume it if not. A failure of any kind therefore never loses
// the user's download.

import {
  basename, buildCapture, cookiesToHeader, evaluate, headerValue, isCapturableUrl, pickHeaders,
} from './capture.js';
import { MediaTracker } from './media.js';
import { TracksTracker, cleanTitle, describeTracks, extensionFor, heightOf, isVideoPage } from './youtube.js';
import { browserTimers } from './native.js';

export const DEFAULT_SETTINGS = { enabled: true, excludedHosts: [], minSizeBytes: 0 };

const CONNECT_TIMEOUT_MS = 10000;   // the app may have to be started
const ANSWER_TIMEOUT_MS = 8000;
const MENU_LINK = 'shadenet-link';

/** Reads a playlist with the browser's own cookies; gives up after a few seconds. */
async function defaultFetchText(url) {
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), 6000);
  try {
    const response = await fetch(url, { credentials: 'include', signal: abort.signal });
    return response.ok ? await response.text() : '';
  } finally {
    clearTimeout(timer);
  }
}

export class Controller {
  constructor({ chrome, link, recorder, userAgent, timers = browserTimers, fetchText = defaultFetchText }) {
    this.chrome = chrome;
    this.link = link;
    this.recorder = recorder;
    this.userAgent = userAgent;
    this.timers = timers;
    this.appSettings = { ...DEFAULT_SETTINGS };
    this.localEnabled = true;
    this.badgeTimer = null;
    this.media = new MediaTracker({ fetchText, onChange: (tabId) => this.updateMediaBadge(tabId) });
    this.tracks = new TracksTracker({ onChange: (tabId) => this.updateMediaBadge(tabId) });
    this.handling = new Map();   // download id -> the hand-over in progress (or done), so each download is handled once
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
      const stored = await this.chrome.storage?.local?.get?.({ enabled: true });
      this.localEnabled = stored?.enabled !== false;
    } catch {
      this.localEnabled = true;
    }
  }

  /** Called when the app sends `hello-ack` or `settings`. */
  applyAppSettings(message) {
    this.appSettings = {
      enabled: message.enabled !== false,
      excludedHosts: Array.isArray(message.excludedHosts) ? message.excludedHosts : [],
      minSizeBytes: Number.isFinite(message.minSizeBytes) ? message.minSizeBytes : 0,
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
      }
    });
    c.tabs?.onRemoved?.addListener((tabId) => {
      this.media.clear(tabId);
      this.tracks.clear(tabId);
    });
    c.contextMenus?.onClicked?.addListener((info, tab) => this.onContextMenu(info, tab));
    c.runtime.onInstalled?.addListener(() => this.createMenus());
    c.runtime.onMessage?.addListener((message, _sender, sendResponse) => this.onPopupMessage(message, sendResponse));
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
  async describe({ url, lookupUrl, originalUrl, referrer, fileName, mimeType, size, source, audioUrl, audioSize, duration }) {
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
      referrer: referrer || headerValue(recorded, 'referer'),
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

  updateMediaBadge(tabId) {
    const action = this.chrome.action;
    if (!action?.setBadgeText) return;
    const n = this.media.list(tabId).filter((m) => !m.protected).length + (this.tracks.current(tabId) ? 1 : 0);
    action.setBadgeBackgroundColor?.({ tabId, color: '#2563eb' });
    action.setBadgeText({ tabId, text: n > 0 ? String(n) : '' });
  }

  async activeTab() {
    const [tab] = (await this.chrome.tabs?.query?.({ active: true, currentWindow: true })) ?? [];
    return tab;
  }

  /** The streams found on the tab the popup was opened on. */
  async listMedia() {
    const tab = await this.activeTab();
    if (!tab) return { items: [] };
    const items = this.media.list(tab.id).map((m) => ({
      url: m.url, kind: m.kind, live: m.live, protected: !!m.protected, duration: m.duration, maxHeight: m.maxHeight, title: tab.title || '',
    }));

    // a YouTube page: ShadeNet asks yt-dlp what is behind it (the player's own requests cannot be used any more)
    if (isVideoPage(tab.url)) {
      items.unshift({
        url: tab.url, kind: 'page', live: false, protected: false, duration: 0, maxHeight: 0, title: cleanTitle(tab.title),
        quality: 'qualities chosen in ShadeNet',
      });
    }

    // picture and sound the page's player is fetching as files (some other sites)
    const playing = this.tracks.current(tab.id);
    if (playing) {
      items.unshift({
        url: playing.video.url, kind: 'tracks', live: playing.video.live, protected: playing.video.drm,
        duration: playing.video.dur, maxHeight: heightOf(playing.video), title: cleanTitle(tab.title),
        quality: describeTracks(playing),
      });
    }

    return { tabId: tab.id, items };
  }

  /** "Download" next to a stream in the popup: the same hand-over as a captured download, without a browser download to cancel. */
  async sendMedia(url) {
    const tab = await this.activeTab();
    if (tab && isVideoPage(tab.url) && tab.url === url) return this.sendPage(tab);
    const playing = tab ? this.tracks.current(tab.id) : null;
    if (playing && playing.video.url === url) return this.sendTracks(tab, playing);
    if (!tab || !this.media.get(tab.id, url)) return { accepted: false, reason: 'unknown-stream' };

    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const message = await this.describe({ url, referrer: tab.url, fileName: tab.title, source: 'media' });
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
   * The address of a video page goes to the app, which shows its Add dialog and asks yt-dlp. No cookies travel with it:
   * the page's address is all the app needs, and your sign-in stays in the browser.
   */
  async sendPage(tab) {
    try {
      if (!(await this.link.ensureConnected(CONNECT_TIMEOUT_MS))) throw new Error(this.link.lastError || 'app unavailable');
      const message = buildCapture({
        id: this.link.newId(), url: tab.url, referrer: tab.url, userAgent: this.userAgent, fileName: cleanTitle(tab.title), source: 'media',
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

  onPopupMessage(message, sendResponse) {
    const reply = async () => {
      await this.ready;
      if (message?.type === 'set-enabled') {
        this.localEnabled = !!message.value;
        await this.safely(() => this.chrome.storage.local.set({ enabled: this.localEnabled }));
      } else if (message?.type === 'connect') {
        await this.link.ensureConnected(CONNECT_TIMEOUT_MS);
      } else if (message?.type === 'list-media') {
        return { ...(await this.listMedia()) };
      } else if (message?.type === 'download-media') {
        return { ...(await this.sendMedia(message.url)) };
      }
      return { ...this.link.status(), localEnabled: this.localEnabled, appEnabled: this.appSettings.enabled };
    };
    reply().then(sendResponse);
    return true;   // the answer is asynchronous
  }
}
