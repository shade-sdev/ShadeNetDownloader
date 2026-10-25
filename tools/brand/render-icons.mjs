// Renders ShadeNet's icon from its SVG sources into every file that carries it:
//   src/ShadeNetDownloader/Assets/app.ico      the program, its window and tray icon, and the setup program
//   src/ShadeNetDownloader/Assets/logo.png     the title bar's mark (64 px)
//   extension/chromium/icons/icon{16,32,48,128}.png
//   src/ShadeNetDownloader/Assets/Icons/<id>.ico and <id>.png   the other app icons one can choose (alt/icons.json lists them)
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
const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });

/** Every size of one icon as PNG files in a fresh folder; `artFor(size)` is the SVG drawn at that size. */
async function renderSizes(artFor) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'shadenet-icon-'));
  for (const size of icoSizes) {
    await page.setViewportSize({ width: size, height: size });
    await page.setContent(`<html><body style="margin:0;background:transparent"><img src="${uri(artFor(size))}" width=${size} height=${size} style="display:block"></body></html>`);
    await page.waitForFunction(() => document.images[0].complete);
    await page.screenshot({ path: path.join(folder, `${size}.png`), omitBackground: true });
  }
  return folder;
}

/** An .ico holding every size as its own picture: Windows picks the one it needs instead of scaling one. */
function writeIco(folder, target) {
  execFileSync('python3', ['-c', `
import sys
from PIL import Image
folder, target, sizes = sys.argv[1], sys.argv[2], [int(s) for s in sys.argv[3:]]
frames = [Image.open(f"{folder}/{s}.png").convert("RGBA") for s in sizes]
frames[-1].save(target, format="ICO", sizes=[(s, s) for s in sizes], append_images=frames[:-1])
`, folder, target, ...icoSizes], { stdio: 'inherit' });
}

// ShadeNet's own icon
const out = await renderSizes(art);
writeIco(out, path.join(root, 'src/ShadeNetDownloader/Assets/app.ico'));
fs.copyFileSync(path.join(out, '64.png'), path.join(root, 'src/ShadeNetDownloader/Assets/logo.png'));
for (const size of [16, 32, 48, 128]) fs.copyFileSync(path.join(out, `${size}.png`), path.join(root, `extension/chromium/icons/icon${size}.png`));
fs.rmSync(out, { recursive: true, force: true });

// the other icons one can choose in Settings > Appearance
const alt = path.join(here, 'alt');
const target = path.join(root, 'src/ShadeNetDownloader/Assets/Icons');
fs.mkdirSync(target, { recursive: true });
for (const icon of JSON.parse(fs.readFileSync(path.join(alt, 'icons.json'), 'utf8'))) {
  const big = fs.readFileSync(path.join(alt, `${icon.id}.svg`), 'utf8');
  const little = icon.small ? fs.readFileSync(path.join(alt, `${icon.id}-small.svg`), 'utf8') : big;
  const folder = await renderSizes((size) => (size <= 32 ? little : big));
  writeIco(folder, path.join(target, `${icon.id}.ico`));
  fs.copyFileSync(path.join(folder, '64.png'), path.join(target, `${icon.id}.png`));
  fs.rmSync(folder, { recursive: true, force: true });
}

fs.copyFileSync(path.join(alt, 'icons.json'), path.join(target, 'icons.json'));   // (the app lists them with their names)

await browser.close();
console.log('icons written');
