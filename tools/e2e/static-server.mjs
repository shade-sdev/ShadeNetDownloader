// A tiny static file server for the end-to-end scripts: node static-server.mjs <folder> [delayMsPerSegment]
// Prints "READY <port>" when listening. Serves .m3u8 with the HLS content type and honours Range.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(process.argv[2]);
const delay = Number(process.argv[3] ?? 0);

const server = http.createServer(async (req, res) => {
  const file = path.join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
    res.writeHead(404).end();
    return;
  }
  if (delay > 0 && file.endsWith('.ts')) await new Promise((r) => setTimeout(r, delay));

  const size = fs.statSync(file).size;
  const types = { '.m3u8': 'application/vnd.apple.mpegurl', '.mpd': 'application/dash+xml', '.html': 'text/html; charset=utf-8', '.vtt': 'text/vtt', '.js': 'text/javascript' };
  const type = types[path.extname(file).toLowerCase()] ?? 'application/octet-stream';
  const range = /bytes=(\d+)-(\d*)/.exec(req.headers.range ?? '');
  if (range) {
    const from = Number(range[1]);
    const to = range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
    res.writeHead(206, { 'Content-Type': type, 'Content-Range': `bytes ${from}-${to}/${size}`, 'Content-Length': to - from + 1, 'Accept-Ranges': 'bytes' });
    fs.createReadStream(file, { start: from, end: to }).pipe(res);
  } else {
    res.writeHead(200, { 'Content-Type': type, 'Content-Length': size, 'Accept-Ranges': 'bytes' });
    fs.createReadStream(file).pipe(res);
  }
});

server.listen(0, '127.0.0.1', () => console.log(`READY ${server.address().port}`));
