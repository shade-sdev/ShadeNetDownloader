// A stand-in for a common kind of embedded player, for testing the extension in a real browser:
//   * the page plays through a MediaSource, so its <video> has a blob: address (nothing to download in the page itself);
//   * the playlist is served under a neutral address and type (/p/9f3a, application/octet-stream), the pieces as .jpg;
//   * every one of those requests is refused unless it carries the PLAYER's own Referer (what many real players do).
//
//   node disguised-player-server.mjs <folder with ffmpeg's fmp4 HLS: index.m3u8, init.mp4, *.m4s>
// Prints "READY <port>". GET /__log answers with what was asked and what was answered.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const dir = path.resolve(process.argv[2]);
const playlist = fs.readFileSync(path.join(dir, 'index.m3u8'), 'utf8');
const files = new Map();   // /s/<n>.jpg -> file
let n = 0;
const disguised = playlist.split('\n').map((line) => {
  const map = /^(#EXT-X-MAP:URI=")([^"]+)(".*)$/.exec(line);
  if (map) {
    const name = `/s/${n++}.jpg`;
    files.set(name, path.join(dir, map[2]));
    return `${map[1]}${name}${map[3]}`;
  }
  if (line && !line.startsWith('#')) {
    const name = `/s/${n++}.jpg`;
    files.set(name, path.join(dir, line.trim()));
    return name;
  }
  return line;
}).join('\n');

const log = [];

const page = `<!doctype html><html><head><meta charset="utf-8"><title>Open film trailer</title></head><body style="margin:0;background:#111">
<video id="v" width="640" height="360" controls muted autoplay></video>
<script>
(async () => {
  const text = await (await fetch('/p/9f3a')).text();
  const init = /URI="([^"]+)"/.exec(text)[1];
  const pieces = text.split('\\n').filter((l) => l && !l.startsWith('#'));
  const media = new MediaSource();
  const video = document.getElementById('v');
  video.src = URL.createObjectURL(media);
  await new Promise((r) => media.addEventListener('sourceopen', r, { once: true }));
  const buffer = media.addSourceBuffer('video/mp4; codecs="avc1.42E01E,mp4a.40.2"');
  const append = async (url) => {
    const bytes = await (await fetch(url)).arrayBuffer();
    buffer.appendBuffer(bytes);
    await new Promise((r) => buffer.addEventListener('updateend', r, { once: true }));
  };
  await append(init);
  for (const piece of pieces) await append(piece);
  media.endOfStream();
})();
</script></body></html>`;

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/__log') {
    res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(log));
    return;
  }
  if (url.pathname === '/player.html') {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' }).end(page);
    return;
  }

  const playerPage = `http://${req.headers.host}/`;
  const allowed = String(req.headers.referer ?? '').startsWith(playerPage);
  const entry = { path: url.pathname, referer: req.headers.referer ?? null, origin: req.headers.origin ?? null, ua: String(req.headers['user-agent'] ?? '').slice(0, 30) };
  const answer = (status, type, body = '') => {
    log.push({ ...entry, status });
    res.writeHead(status, type ? { 'Content-Type': type, 'Content-Length': Buffer.byteLength(body), 'Access-Control-Allow-Origin': '*' } : {}).end(body);
  };

  if (url.pathname === '/p/9f3a') {
    if (!allowed) return answer(403);
    return answer(200, 'application/octet-stream', disguised);
  }
  if (files.has(url.pathname)) {
    if (!allowed) return answer(403);
    const bytes = fs.readFileSync(files.get(url.pathname));
    log.push({ ...entry, status: 200 });
    res.writeHead(200, { 'Content-Type': 'image/jpeg', 'Content-Length': bytes.length }).end(bytes);
    return;
  }
  answer(404);
});

server.listen(0, '127.0.0.1', () => console.log(`READY ${server.address().port}`));
