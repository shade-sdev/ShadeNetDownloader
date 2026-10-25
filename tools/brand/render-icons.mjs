// Renders ShadeNet's icon from its SVG sources into every file that carries it:
//   src/ShadeNetDownloader/Assets/app.ico      the program, its window and tray icon, and the setup program
//   src/ShadeNetDownloader/Assets/logo.png     the title bar's mark (64 px)
//   extension/chromium/icons/icon{16,32,48,128}.png
// shadenet-icon.svg is the full design; shadenet-icon-small.svg (the gem without its dot globe) is used at 32 px and
// below, where the dots would only blur. Needs Playwright with its Chromium, and Python with Pillow.
//
//   node tools/brand/render-icons.mjs        (PLAYWRIGHT_FROM=<a folder with node_modules/playwright> if it is elsewhere)
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const require = createRequire(path.join(process.env.PLAYWRIGHT_FROM ?? root, 'package.json'));
const { chromium } = require('playwright');

const full = fs.readFileSync(path.join(here, 'shadenet-icon.svg'), 'utf8');
const small = fs.readFileSync(path.join(here, 'shadenet-icon-small.svg'), 'utf8');
const art = (size) => (size <= 32 ? small : full);
const uri = (svg) => 'data:image/svg+xml;base64,' + Buffer.from(svg).toString('base64');

const icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const out = fs.mkdtempSync(path.join(os.tmpdir(), 'shadenet-icon-'));
const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });
for (const size of icoSizes) {
  await page.setViewportSize({ width: size, height: size });
  await page.setContent(`<html><body style="margin:0;background:transparent"><img src="${uri(art(size))}" width=${size} height=${size} style="display:block"></body></html>`);
  await page.waitForFunction(() => document.images[0].complete);
  await page.screenshot({ path: path.join(out, `${size}.png`), omitBackground: true });
}
await browser.close();

// the .ico holds every size as its own picture: Windows picks the one it needs instead of scaling one
execFileSync('python3', ['-c', `
import sys
from PIL import Image
folder, target, sizes = sys.argv[1], sys.argv[2], [int(s) for s in sys.argv[3:]]
frames = [Image.open(f"{folder}/{s}.png").convert("RGBA") for s in sizes]
frames[-1].save(target, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
`, out, path.join(root, 'src/ShadeNetDownloader/Assets/app.ico'), ...icoSizes], { stdio: 'inherit' });

fs.copyFileSync(path.join(out, '64.png'), path.join(root, 'src/ShadeNetDownloader/Assets/logo.png'));
for (const size of [16, 32, 48, 128]) fs.copyFileSync(path.join(out, `${size}.png`), path.join(root, `extension/chromium/icons/icon${size}.png`));
fs.rmSync(out, { recursive: true, force: true });
console.log('icons written');
