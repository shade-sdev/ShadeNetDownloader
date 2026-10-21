// Pure functions: deciding what to hand over and shaping the message. No browser APIs in here, so they are
// unit-tested directly (test/capture.test.mjs).

/** Request headers worth replaying. Cookie, Referer and User-Agent travel as their own fields. */
export const HEADER_ALLOWLIST = new Set(['authorization', 'accept-language', 'origin']);

const MAX_HEADER_VALUE = 8192;

/** Only web addresses are handed over; blob:, data:, file: and extension pages stay with the browser. */
export function isCapturableUrl(url) {
  if (typeof url !== 'string' || url.length === 0 || url.length > 8192) return false;
  try {
    const u = new URL(url);
    return u.protocol === 'http:' || u.protocol === 'https:';
  } catch {
    return false;
  }
}

/** True when `host` is one of the patterns or a sub-domain of one ("*.example.com" and ".example.com" also work). */
export function hostMatches(host, patterns) {
  if (!host || !Array.isArray(patterns)) return false;
  const h = host.toLowerCase();
  return patterns.some((raw) => {
    if (typeof raw !== 'string') return false;
    const p = raw.trim().toLowerCase().replace(/^\*?\./, '');
    return p.length > 0 && (h === p || h.endsWith('.' + p));
  });
}

/** The last path segment of a Windows or POSIX path, '' when there is none. */
export function basename(path) {
  if (typeof path !== 'string') return '';
  const parts = path.split(/[\\/]/);
  return parts[parts.length - 1] || '';
}

export function cookiesToHeader(cookies) {
  if (!Array.isArray(cookies)) return '';
  return cookies
    .filter((c) => c && typeof c.name === 'string' && c.name.length > 0)
    .map((c) => `${c.name}=${c.value ?? ''}`)
    .join('; ');
}

/** From a webRequest header list ([{name, value}]) keep only the allow-listed ones, as a plain object. */
export function pickHeaders(headerList) {
  const picked = {};
  if (!Array.isArray(headerList)) return picked;
  for (const h of headerList) {
    if (!h || typeof h.name !== 'string' || typeof h.value !== 'string') continue;
    if (!HEADER_ALLOWLIST.has(h.name.toLowerCase())) continue;
    if (h.value.length === 0 || h.value.length > MAX_HEADER_VALUE || /[\r\n]/.test(h.value)) continue;
    picked[h.name] = h.value;
  }
  return picked;
}

/** The value of one header (case-insensitive) from a webRequest header list. */
export function headerValue(headerList, name) {
  if (!Array.isArray(headerList)) return undefined;
  const wanted = name.toLowerCase();
  return headerList.find((h) => h && typeof h.name === 'string' && h.name.toLowerCase() === wanted)?.value;
}

/**
 * Should this browser download be handed to the app? `settings` is what the app last told us
 * ({ enabled, excludedHosts, minSizeBytes }) plus the user's local switch in the popup.
 */
export function evaluate(item, settings) {
  if (!settings.enabled) return { capture: false, reason: 'disabled' };
  const url = item.finalUrl || item.url;
  if (!isCapturableUrl(url)) return { capture: false, reason: 'not-http' };
  if (item.state && item.state !== 'in_progress') return { capture: false, reason: 'not-in-progress' };

  let host = '';
  try {
    host = new URL(url).hostname;
  } catch {
    // isCapturableUrl already proved it parses
  }
  if (hostMatches(host, settings.excludedHosts)) return { capture: false, reason: 'excluded' };
  if (item.totalBytes > 0 && settings.minSizeBytes > 0 && item.totalBytes < settings.minSizeBytes) {
    return { capture: false, reason: 'too-small' };
  }
  return { capture: true, reason: 'ok' };
}

/** The `capture` message. Unset members are omitted so the frame stays small. */
export function buildCapture({
  id, url, originalUrl, referrer, cookies, userAgent, headers, fileName, mimeType, size, source, audioUrl, audioSize, duration,
}) {
  const message = { type: 'capture', id, url, source: source || 'download' };
  if (originalUrl && originalUrl !== url) message.originalUrl = originalUrl;
  if (referrer) message.referrer = referrer;
  if (cookies) message.cookies = cookies;
  if (userAgent) message.userAgent = userAgent;
  if (headers && Object.keys(headers).length > 0) message.headers = headers;
  if (fileName) message.fileName = fileName;
  if (mimeType) message.mimeType = mimeType;
  if (Number.isFinite(size) && size > 0) message.size = size;
  if (audioUrl) message.audioUrl = audioUrl;
  if (Number.isFinite(audioSize) && audioSize > 0) message.audioSize = audioSize;
  if (Number.isFinite(duration) && duration > 0) message.duration = duration;
  return message;
}
