// The connection to the native messaging host (and through it, the app). It is opened lazily, when there is something
// to hand over, because opening it can start the app: a user who closed ShadeNet must not have it relaunched by every
// browser start. The app answering our `hello` is what proves that the whole chain works.

export const HOST_NAME = 'com.shadenet.downloader';

export class NativeLink {
  /**
   * @param chromeApi  the `chrome` object (injected so tests can fake it)
   * @param handlers   { onSettings(settings), onStatus() } called as the state changes
   */
  constructor(chromeApi, handlers = {}, timers = browserTimers) {
    this.chrome = chromeApi;
    this.handlers = handlers;
    this.timers = timers;
    this.port = null;
    this.connected = false;      // true once the app has answered our hello
    this.lastError = '';
    this.appVersion = '';
    this.pending = new Map();    // capture id -> { resolve, reject, timer }
    this.helloWaiters = [];
    this.nextId = 1;
  }

  newId() {
    return `${Date.now().toString(36)}-${this.nextId++}`;
  }

  /** Resolves true when the app is reachable (starting it if needed, within `timeoutMs`), false otherwise. */
  async ensureConnected(timeoutMs = 10000) {
    if (this.connected && this.port) return true;
    if (!this.port) this.open();
    if (!this.port) return false;

    return new Promise((resolve) => {
      const waiter = { resolve };
      waiter.timer = this.timers.setTimeout(() => {
        this.helloWaiters = this.helloWaiters.filter((w) => w !== waiter);
        if (!this.connected) this.lastError ||= 'The app did not answer in time.';
        resolve(false);
      }, timeoutMs);
      this.helloWaiters.push(waiter);
    });
  }

  open() {
    try {
      this.lastError = '';
      const port = this.chrome.runtime.connectNative(HOST_NAME);
      this.port = port;
      port.onMessage.addListener((message) => this.onMessage(message));
      port.onDisconnect.addListener(() => this.onDisconnect(port));
      port.postMessage({
        type: 'hello',
        browser: 'chromium',
        extensionVersion: this.chrome.runtime.getManifest?.().version ?? '',
      });
    } catch (error) {
      this.port = null;
      this.lastError = String(error?.message ?? error);
    }
    this.handlers.onStatus?.();
  }

  onMessage(message) {
    if (!message || typeof message.type !== 'string') return;

    switch (message.type) {
      case 'hello-ack':
      case 'settings':
        this.appVersion = message.appVersion || this.appVersion;
        this.handlers.onSettings?.(message);
        if (message.type === 'hello-ack') {
          this.connected = true;
          this.lastError = '';
          const waiters = this.helloWaiters;
          this.helloWaiters = [];
          for (const w of waiters) {
            this.timers.clearTimeout(w.timer);
            w.resolve(true);
          }
        }
        this.handlers.onStatus?.();
        break;

      case 'capture-result': {
        const entry = this.pending.get(message.id);
        if (entry) {
          this.pending.delete(message.id);
          this.timers.clearTimeout(entry.timer);
          entry.resolve(message);
        }
        break;
      }

      case 'host-error':
        this.lastError = message.message || message.code || 'The native host reported an error.';
        this.handlers.onStatus?.();
        break;

      default:
        break;
    }
  }

  onDisconnect(port) {
    if (port !== this.port) return;
    const reason = this.chrome.runtime.lastError?.message;
    if (reason && !this.lastError) this.lastError = reason;
    this.port = null;
    this.connected = false;

    for (const [, entry] of this.pending) {
      this.timers.clearTimeout(entry.timer);
      entry.reject(new Error('The connection to ShadeNet Downloader was closed.'));
    }
    this.pending.clear();

    const waiters = this.helloWaiters;
    this.helloWaiters = [];
    for (const w of waiters) {
      this.timers.clearTimeout(w.timer);
      w.resolve(false);
    }
    this.handlers.onStatus?.();
  }

  /** Sends a capture and resolves with the app's `capture-result`; rejects on timeout or disconnect. */
  request(message, timeoutMs = 8000) {
    if (!this.port) return Promise.reject(new Error('Not connected to ShadeNet Downloader.'));
    return new Promise((resolve, reject) => {
      const timer = this.timers.setTimeout(() => {
        this.pending.delete(message.id);
        reject(new Error('ShadeNet Downloader did not answer in time.'));
      }, timeoutMs);
      this.pending.set(message.id, { resolve, reject, timer });
      try {
        this.port.postMessage(message);
      } catch (error) {
        this.pending.delete(message.id);
        this.timers.clearTimeout(timer);
        reject(error);
      }
    });
  }

  status() {
    return { connected: this.connected, lastError: this.lastError, appVersion: this.appVersion };
  }
}

/**
 * `setTimeout` must be called on the global object in a browser ("Illegal invocation" otherwise), so it is wrapped
 * rather than stored as-is: `timers.setTimeout(...)` would call it with `timers` as `this`.
 */
export const browserTimers = {
  setTimeout: (...args) => globalThis.setTimeout(...args),
  clearTimeout: (...args) => globalThis.clearTimeout(...args),
};
