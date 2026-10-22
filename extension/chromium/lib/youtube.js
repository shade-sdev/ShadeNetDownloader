// Finds the picture and sound a video site's player is fetching as separate files (YouTube: `googlevideo.com/videoplayback`),
// so the popup can offer to download what is playing.
//
// Nothing here works out how the addresses are made: the browser's own player already did that (the address a request
// carries is complete, signed and valid for this machine). We only notice the requests, drop the parameters that make a
// request a small slice of the file (`range`, `rn`, `rbuf`) and remember what is playing.

/** What the common itags are: label for the popup, height for sorting. Unknown ones are shown by their type only. */
const ITAGS = {
  18: { height: 360, muxed: true }, 22: { height: 720, muxed: true }, 37: { height: 1080, muxed: true }, 43: { height: 360, muxed: true },
  59: { height: 480, muxed: true }, 78: { height: 480, muxed: true },
  133: { height: 240 }, 134: { height: 360 }, 135: { height: 480 }, 136: { height: 720 }, 137: { height: 1080 }, 160: { height: 144 },
  264: { height: 1440 }, 266: { height: 2160 }, 298: { height: 720 }, 299: { height: 1080 },
  242: { height: 240 }, 243: { height: 360 }, 244: { height: 480 }, 247: { height: 720 }, 248: { height: 1080 }, 271: { height: 1440 },
  313: { height: 2160 }, 278: { height: 144 }, 302: { height: 720 }, 303: { height: 1080 }, 308: { height: 1440 }, 315: { height: 2160 },
  394: { height: 144 }, 395: { height: 240 }, 396: { height: 360 }, 397: { height: 480 }, 398: { height: 720 }, 399: { height: 1080 },
  400: { height: 1440 }, 401: { height: 2160 },
  139: { kbps: 48 }, 140: { kbps: 128 }, 141: { kbps: 256 }, 249: { kbps: 50 }, 250: { kbps: 70 }, 251: { kbps: 160 },
};

const SLICE_PARAMS = ['range', 'rn', 'rbuf'];

/**
 * One request of the player, understood. Returns null for anything that is not a playback request.
 * @returns {null | { kind: 'video'|'audio'|'muxed', itag: number, mime: string, clen: number, dur: number, live: boolean, drm: boolean, url: string, source: string }}
 */
export function parsePlayback(rawUrl) {
  let u;
  try {
    u = new URL(rawUrl);
  } catch {
    return null;
  }
  if (!/(^|\.)googlevideo\.com$/i.test(u.hostname) || u.pathname !== '/videoplayback') return null;

  const itag = Number(u.searchParams.get('itag'));
  const mime = (u.searchParams.get('mime') || '').toLowerCase();
  if (!Number.isFinite(itag) || !/^(video|audio)\//.test(mime)) return null;

  const info = ITAGS[itag] ?? {};
  const kind = info.muxed ? 'muxed' : mime.startsWith('audio/') ? 'audio' : 'video';

  const stripped = new URL(u.href);
  for (const name of SLICE_PARAMS) stripped.searchParams.delete(name);

  return {
    kind,
    itag,
    mime,
    clen: Number(u.searchParams.get('clen')) || 0,
    dur: Number(u.searchParams.get('dur')) || 0,
    // a live stream is an endless run of numbered pieces (`sq`), not a file
    live: u.searchParams.get('live') === '1' || u.searchParams.has('sq'),
    drm: [...u.searchParams.keys()].some((k) => k.startsWith('drm_')),
    url: stripped.href,
    source: rawUrl,
  };
}

/** "1080p + 128 kbit/s" for the popup. */
export function describeTracks({ video, audio }) {
  const parts = [];
  const v = video && ITAGS[video.itag];
  if (v?.height) parts.push(`${v.height}p`);
  const a = audio && ITAGS[audio.itag];
  if (a?.kbps) parts.push(`${a.kbps} kbit/s sound`);
  return parts.join(' + ');
}

export function heightOf(track) {
  return (track && ITAGS[track.itag]?.height) || 0;
}

export class TracksTracker {
  constructor({ onChange = () => {} } = {}) {
    this.onChange = onChange;
    this.byTab = new Map();   // tabId -> { video?, audio? }   (muxed counts as video: it has both)
  }

  /** A response arrived: `details` is what webRequest.onHeadersReceived gives. */
  consider(details) {
    const { tabId, url } = details || {};
    if (!Number.isInteger(tabId) || tabId < 0 || typeof url !== 'string') return;
    if (details.statusCode && details.statusCode !== 200 && details.statusCode !== 206) return;

    const track = parsePlayback(url);
    if (!track) return;

    const slot = this.byTab.get(tabId) ?? {};
    const key = track.kind === 'audio' ? 'audio' : 'video';
    const before = slot[key];
    slot[key] = track;
    this.byTab.set(tabId, slot);
    if (!before || before.itag !== track.itag) this.onChange(tabId);
  }

  /** What is playing on the tab, or null when no picture was seen (sound alone is not offered). */
  current(tabId) {
    const slot = this.byTab.get(tabId);
    if (!slot?.video) return null;
    return { video: slot.video, audio: slot.video.kind === 'muxed' ? undefined : slot.audio };
  }

  clear(tabId) {
    if (this.byTab.delete(tabId)) this.onChange(tabId);
  }
}

/** A YouTube video page (watch, shorts, live, youtu.be): ShadeNet asks yt-dlp what is behind it. */
export function isVideoPage(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== 'https:') return false;
    if (u.hostname === 'youtu.be') return u.pathname.length > 1;
    if (!/^(www\.|m\.|music\.)?youtube\.com$/.test(u.hostname)) return false;
    return (u.pathname === '/watch' && u.searchParams.has('v')) || /^\/(shorts|live)\//.test(u.pathname);
  } catch {
    return false;
  }
}

/** The page title without the site's suffix, made safe as a file name. */
export function cleanTitle(title) {
  return String(title || 'video')
    .replace(/^\(\d+\)\s*/, '')                // "(3) " unread counter
    .replace(/\s+[|–—-]\s+(YouTube|Facebook|Instagram|TikTok|Vimeo|Reddit|Dailymotion|Twitch)$/i, '')   // the site's name after the title
    .replace(/[\\/:*?"<>|]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim() || 'video';
}

/** MKV holds VP9/Opus; MP4 is used when everything is MP4 family. */
export function extensionFor({ video, audio }) {
  return video?.mime.includes('webm') || audio?.mime.includes('webm') ? '.mkv' : '.mp4';
}
