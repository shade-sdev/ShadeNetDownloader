// "Download links with ShadeNet…": the links of a page (or of the part of it the user selected) go to ShadeNet's link grabber, with
// the browser's sign-in for each site they point to. The user picks the files there.

export const MAX_LINKS = 5000;
const MAX_HOSTS = 30;

/**
 * Runs IN THE PAGE (through scripting.executeScript, so it must not use anything from outside itself): the page's links, or only the
 * ones in the selection, with the words they are shown with.
 */
export function collectLinks(selectionOnly) {
  const max = 5000;
  const selection = window.getSelection();
  const range = selectionOnly && selection && !selection.isCollapsed && selection.rangeCount > 0 ? selection.getRangeAt(0) : null;
  const nodes = [...document.querySelectorAll('a[href], area[href], img[src], video[src], audio[src], source[src]')]
    .filter((n) => !range || range.intersectsNode(n));
  const links = [];
  for (const n of nodes) {
    const url = n.href || n.currentSrc || n.src;
    if (!url || typeof url !== 'string') continue;
    const text = (n.innerText || n.getAttribute?.('aria-label') || n.alt || n.title || '').replace(/\s+/g, ' ').trim().slice(0, 300);
    links.push({ url, text: text || undefined });
    if (links.length >= max) break;
  }
  return { title: document.title || '', links };
}

/** Addresses ShadeNet can download (web, FTP, magnet), each once. */
export function cleanLinks(links) {
  const seen = new Set();
  const out = [];
  for (const link of links ?? []) {
    const url = typeof link?.url === 'string' ? link.url.trim() : '';
    if (!/^(https?|ftp):\/\//i.test(url) && !/^magnet:\?/i.test(url)) continue;
    if (url.length > 8192 || seen.has(url)) continue;
    seen.add(url);
    out.push({ url, ...(typeof link.text === 'string' && link.text ? { text: link.text.slice(0, 300) } : {}) });
    if (out.length >= MAX_LINKS) break;
  }
  return out;
}

/** The browser's cookies for each site the links point to (at most 30 sites), as Cookie header values by host. */
export async function cookiesByHost(chromeApi, links, toHeader) {
  const firstOfHost = new Map();
  for (const { url } of links) {
    let host;
    try {
      const u = new URL(url);
      if (!/^https?:$/.test(u.protocol)) continue;
      host = u.hostname;
    } catch {
      continue;
    }
    if (!firstOfHost.has(host)) firstOfHost.set(host, url);
    if (firstOfHost.size >= MAX_HOSTS) break;
  }

  const cookies = {};
  for (const [host, url] of firstOfHost) {
    try {
      const header = toHeader(await chromeApi.cookies.getAll({ url }));
      if (header) cookies[host] = header;
    } catch {
      // no cookies for that site
    }
  }
  return cookies;
}
