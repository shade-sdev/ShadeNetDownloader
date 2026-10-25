// The connection to the native messaging host (and through it, the app). Opening it for real work (a download to hand
// over) starts the app if it is not running. Looking whether the app is there (`probe`) never does: the hello then says
// `launch: false`, so a user who closed ShadeNet does not get it back because the browser started or a popup was opened.
// The app answering our `hello` is what proves that the whole chain works.

export const HOST_NAME = 'com.shadenet.downloader';

/**
 * After a connection to the app drops, how long to wait before each look for it again (never starting it). An update closes the
 * app and opens the new one moments later: looking again soon is what lets the extension notice the new version (and reload
 * its own files) without waiting for the next download. A closed app costs five quick looks over a few minutes, then none.
 */
export const RETURN_LOOKS_MS = [3000, 10000, 30000, 60000, 120000];

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
    this.portLaunch = true;      // whether the open port may start the app
    this.lastCode = '';          // the host's last error code ('app-not-running': nothing wrong, the app is simply closed)
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
    if (this.port && !this.portLaunch) this.abandon();   // a look-only connection cannot start the app
    if (!this.port) this.open(true);
    return this.waitForHello(timeoutMs);
  }

  /** Resolves true when the app is running and answers; false (quickly) when it is not. Never starts the app. */
  async probe(timeoutMs = 4000) {
    if (this.connected && this.port) return true;
    if (!this.port) this.open(false);
    return this.waitForHello(timeoutMs);
  }

  /** Gives up the port that is open, without waiting for it. */
  abandon() {
    const port = this.port;
    this.port = null;
    this.connected = false;
    try {
      port?.disconnect?.();
    } catch {
      // already gone
    }
    this.resolveWaiters(false);
  }

  resolveWaiters(value) {
    const waiters = this.helloWaiters;
    this.helloWaiters = [];
    for (const w of waiters) {
      this.timers.clearTimeout(w.timer);
      w.resolve(value);
    }
  }

  waitForHello(timeoutMs) {
    if (!this.port) return Promise.resolve(false);
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

  open(launch = true) {
    try {
      this.lastError = '';
      this.lastCode = '';
      this.portLaunch = launch;
      const port = this.chrome.runtime.connectNative(HOST_NAME);
      this.port = port;
      port.onMessage.addListener((message) => this.onMessage(message));
      port.onDisconnect.addListener(() => this.onDisconnect(port));
      port.postMessage({
        type: 'hello',
        browser: 'chromium',
        extensionVersion: this.chrome.runtime.getManifest?.().version ?? '',
        launch,
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
          this.lastCode = '';
          this.resolveWaiters(true);
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
        this.lastCode = message.code || '';
        // a closed app is not an error: the popup says so in its own words
        this.lastError = message.code === 'app-not-running' ? '' : message.message || message.code || 'The native host reported an error.';
        this.handlers.onStatus?.();
        break;

      default:
        break;
    }
  }

  onDisconnect(port) {
    if (port !== this.port) return;
    const wasConnected = this.connected;
    const reason = this.chrome.runtime.lastError?.message;
    if (reason && !this.lastError && this.lastCode !== 'app-not-running') this.lastError = reason;
    this.port = null;
    this.connected = false;

    for (const [, entry] of this.pending) {
      this.timers.clearTimeout(entry.timer);
      entry.reject(new Error('The connection to ShadeNet Downloader was closed.'));
    }
    this.pending.clear();

    this.resolveWaiters(false);
    this.handlers.onStatus?.();
    if (wasConnected) this.lookForReturn(0);
  }

  /** Looks for the app again on the {@link RETURN_LOOKS_MS} schedule until it answers (an update brings it back moments later). */
  lookForReturn(attempt) {
    this.timers.clearTimeout(this.returnTimer);
    this.returnTimer = null;
    if (attempt >= RETURN_LOOKS_MS.length) return;
    this.returnTimer = this.timers.setTimeout(async () => {
      this.returnTimer = null;
      if (this.connected) return;
      if (!(await this.probe())) this.lookForReturn(attempt + 1);
    }, RETURN_LOOKS_MS[attempt]);
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

  /** 'connected', 'not-running' (the app is closed: nothing is wrong), 'error' (see lastError) or 'unknown' (not looked yet). */
  status() {
    const state = this.connected ? 'connected' : this.lastCode === 'app-not-running' ? 'not-running' : this.lastError ? 'error' : 'unknown';
    return { connected: this.connected, state, lastError: this.lastError, appVersion: this.appVersion };
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
