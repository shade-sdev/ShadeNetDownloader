// Remembers the request headers the browser sent for recent requests, so that when a download starts we can replay
// the exact Cookie / Authorization / Origin the site saw (IDM does the same with webRequest, report §13.8).
// Nothing is persisted: it is a short-lived cache in the service worker's memory.

const MAX_ENTRIES = 200;
const MAX_AGE_MS = 2 * 60 * 1000;

/** The origin the extension's own requests come from ("chrome-extension://<id>", "moz-extension://<uuid>"); null when unknown. */
export function ownOrigin(chromeApi) {
  try {
    const base = chromeApi?.runtime?.getURL?.('');
    if (base) return originOf(base);
  } catch {
    // below
  }
  return chromeApi?.runtime?.id ? `chrome-extension://${chromeApi.runtime.id}` : null;
}

/** Where a request came from: Chrome's `initiator`, Firefox's `originUrl` (as an origin). */
export function initiatorOf(details) {
  const from = details?.initiator ?? details?.originUrl;
  return from ? originOf(from) : undefined;
}

/** "scheme://host[:port]" of an address (URL.origin says "null" for an extension's own scheme in some engines). */
function originOf(address) {
  try {
    const u = new URL(address);
    return `${u.protocol}//${u.host}`;
  } catch {
    return address;
  }
}

export class HeaderRecorder {
  constructor(chromeApi, now = () => Date.now()) {
    this.byUrl = new Map();
    this.now = now;

    const events = chromeApi?.webRequest?.onBeforeSendHeaders;
    const own = ownOrigin(chromeApi);
    if (events?.addListener) {
      // the extension's own looks at a playlist are not what the site's player sent: they must not replace it
      const listener = (details) => { if (!(own && initiatorOf(details) === own)) this.record(details.url, details.requestHeaders); };
      try {
        // (Chrome hands over Cookie and Referer only with "extraHeaders")
        events.addListener(listener, { urls: ['<all_urls>'] }, ['requestHeaders', 'extraHeaders']);
      } catch {
        // Firefox has no "extraHeaders": it gives every header without it
        events.addListener(listener, { urls: ['<all_urls>'] }, ['requestHeaders']);
      }
    }
  }

  record(url, headers) {
    if (!url || !Array.isArray(headers)) return;
    this.byUrl.delete(url);                       // re-insert: Map order is the LRU order
    this.byUrl.set(url, { headers, at: this.now() });
    while (this.byUrl.size > MAX_ENTRIES) this.byUrl.delete(this.byUrl.keys().next().value);
  }

  /** The headers of the most recent request to exactly this address, if it is still fresh. */
  lookup(url) {
    const entry = this.byUrl.get(url);
    if (!entry) return undefined;
    if (this.now() - entry.at > MAX_AGE_MS) {
      this.byUrl.delete(url);
      return undefined;
    }
    return entry.headers;
  }
}
