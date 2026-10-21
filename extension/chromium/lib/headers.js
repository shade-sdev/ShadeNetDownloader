// Remembers the request headers the browser sent for recent requests, so that when a download starts we can replay
// the exact Cookie / Authorization / Origin the site saw (IDM does the same with webRequest, report §13.8).
// Nothing is persisted: it is a short-lived cache in the service worker's memory.

const MAX_ENTRIES = 200;
const MAX_AGE_MS = 2 * 60 * 1000;

export class HeaderRecorder {
  constructor(chromeApi, now = () => Date.now()) {
    this.byUrl = new Map();
    this.now = now;

    const events = chromeApi?.webRequest?.onBeforeSendHeaders;
    if (events?.addListener) {
      events.addListener(
        (details) => this.record(details.url, details.requestHeaders),
        { urls: ['<all_urls>'] },
        ['requestHeaders', 'extraHeaders'],
      );
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
