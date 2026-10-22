// Service worker entry point: wires the real browser APIs into the Controller (lib/controller.js).
import { Controller } from './lib/controller.js';
import { HeaderRecorder } from './lib/headers.js';
import { NativeLink } from './lib/native.js';

let controller;
const link = new NativeLink(chrome, {
  onSettings: (message) => controller.applyAppSettings(message),
  onStatus: () => {},
});

controller = new Controller({
  chrome,
  link,
  recorder: new HeaderRecorder(chrome),
  userAgent: navigator.userAgent,
});

controller.registerListeners();

// For diagnosis: chrome://extensions → ShadeNet → "service worker" → console: `shadenet.controller.events`.
globalThis.shadenet = { controller, link };

// Looking whether the app is running (never starting it) lets the toolbar button show the right state without a click, and
// keeps the connection ready when the app is there. A closed app stays closed: only a download or a click on "Start" opens it.
const look = () => controller.probe().catch(() => {});
look();
chrome.runtime.onStartup?.addListener(look);
