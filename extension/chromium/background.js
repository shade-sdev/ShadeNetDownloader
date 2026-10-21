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
