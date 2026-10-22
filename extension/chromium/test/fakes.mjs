// A fake `chrome` and a fake native app, so the real extension code can be exercised without a browser.

export class FakeEvent {
  constructor() { this.listeners = []; }
  addListener(fn) { this.listeners.push(fn); }
  async emit(...args) { for (const fn of [...this.listeners]) await fn(...args); }
}

export class FakePort {
  constructor(app) {
    this.app = app;
    this.onMessage = new FakeEvent();
    this.onDisconnect = new FakeEvent();
    this.sent = [];
    this.disconnected = false;
  }

  disconnect() { this.disconnected = true; }

  postMessage(message) {
    this.sent.push(message);
    queueMicrotask(() => this.app.receive(this, message));
  }

  deliver(message) { return this.onMessage.emit(message); }
  async close() { await this.onDisconnect.emit(); }
}

/** Behaves like the app behind the native host: answers hello, and captures according to `capture`. */
export class FakeApp {
  constructor() {
    this.settings = { enabled: true, excludedHosts: [], minSizeBytes: 0, appVersion: '9.9' };
    this.captures = [];
    this.available = true;               // false: connectNative works but the host reports an error and closes
    this.running = true;                 // false: the app is closed; a hello that may launch it starts it, one that may not is told so
    this.launches = 0;
    this.answerHello = true;
    this.capture = (message) => ({ accepted: true, reason: 'added' });   // overridable per test
    this.ports = [];
  }

  connectNative = () => {
    const port = new FakePort(this);
    this.ports.push(port);
    if (!this.available) {
      queueMicrotask(async () => {
        await port.deliver({ type: 'host-error', code: 'app-unavailable', message: 'ShadeNet Downloader is not running.' });
        await port.close();
      });
    }
    return port;
  };

  async receive(port, message) {
    if (!this.available) return;
    if (message.type === 'hello' && !this.running) {
      if (message.launch === false) {
        await port.deliver({ type: 'host-error', code: 'app-not-running', message: 'ShadeNet Downloader is not running.' });
        await port.close();
        return;
      }
      this.running = true;
      this.launches++;
    }
    if (message.type === 'hello' && this.answerHello) {
      await port.deliver({ type: 'hello-ack', ...this.settings });
    } else if (message.type === 'capture') {
      this.captures.push(message);
      const reply = await this.capture(message);
      if (reply) await port.deliver({ type: 'capture-result', id: message.id, ...reply });
    }
  }
}

export function fakeChrome(app, { storage = {}, cookies = {}, downloads = {} } = {}) {
  const calls = { pause: [], resume: [], cancel: [], erase: [], badge: [], titles: [], nudges: [] };
  const store = { ...storage };

  const chrome = {
    runtime: {
      connectNative: app.connectNative,
      getManifest: () => ({ version: '1.0.0' }),
      lastError: undefined,
      onInstalled: new FakeEvent(),
      onMessage: new FakeEvent(),
    },
    storage: {
      local: {
        get: async (defaults) => ({ ...defaults, ...store }),
        set: async (values) => { Object.assign(store, values); },
      },
    },
    downloads: {
      onCreated: new FakeEvent(),
      onDeterminingFilename: new FakeEvent(),
      pause: async (id) => { calls.pause.push(id); },
      resume: async (id) => { calls.resume.push(id); },
      cancel: async (id) => { calls.cancel.push(id); },
      erase: async (query) => { calls.erase.push(query.id); },
      search: async ({ id }) => [{ id, state: downloads.state ?? 'in_progress' }],
    },
    cookies: {
      getAll: async ({ url }) => cookies[url] ?? [],
    },
    contextMenus: {
      onClicked: new FakeEvent(),
      created: [],
      removeAll(callback) { callback?.(); },
      create(properties) { this.created.push(properties); },
    },
    action: {
      setBadgeText: ({ text }) => { calls.badge.push(text); },
      setBadgeBackgroundColor: () => {},
      setTitle: ({ title }) => { calls.titles.push(title); },
    },
    webRequest: { onBeforeSendHeaders: new FakeEvent(), onHeadersReceived: new FakeEvent() },
    tabs: {
      onUpdated: new FakeEvent(),
      onRemoved: new FakeEvent(),
      active: { id: 7, title: 'My Holiday Video', url: 'https://site.example/watch/1' },
      query: async () => [chrome.tabs.active],
      get: async (id) => (id === chrome.tabs.active.id ? chrome.tabs.active : undefined),
      sendMessage: async (id, message) => { calls.nudges.push([id, message.type]); },
    },
  };

  return { chrome, calls, store };
}

/** Timers that never fire on their own: tests decide when time passes. */
export function manualTimers() {
  const pending = new Map();
  let next = 1;
  return {
    setTimeout: (fn, ms) => { const id = next++; pending.set(id, { fn, ms }); return id; },
    clearTimeout: (id) => { pending.delete(id); },
    fire(predicate = () => true) { for (const [id, t] of [...pending]) if (predicate(t)) { pending.delete(id); t.fn(); } },
    count: () => pending.size,
  };
}
