// Sites whose player hands the browser a buffer (a `blob:` address) instead of a file: nothing on the network says what
// the video is, so the PAGE is the only address there is. ShadeNet gives it to yt-dlp, which knows these sites.
//
// A button is offered only for sites yt-dlp is known to read, so it never promises a download that cannot happen. Anything
// else with a hidden address stays without a button (the right-click menu still works for links).

const PAGE_SITES = [
  'facebook.com', 'fb.watch', 'instagram.com', 'threads.net', 'twitter.com', 'x.com', 'tiktok.com', 'vimeo.com',
  'reddit.com', 'dailymotion.com', 'twitch.tv', 'streamable.com', 'ok.ru', 'vk.com', 'bilibili.com', 'rumble.com',
  'odysee.com', 'bitchute.com', 'kick.com', 'linkedin.com', 'pinterest.com', 'tumblr.com', 'imgur.com', 'ted.com',
];

/** Is this address on a site whose pages ShadeNet can have yt-dlp read? `extra` adds hosts (tests, and your own list). */
export function isPageSite(url, extra = []) {
  let host;
  try {
    const u = new URL(url);
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return false;
    host = u.hostname.toLowerCase();
  } catch {
    return false;
  }
  return [...PAGE_SITES, ...extra].some((site) => host === site || host.endsWith(`.${site}`));
}

/**
 * Is this the address of ONE video (what yt-dlp can read), and not a feed or the front page? An address that has an id
 * after /videos/, /reel/, /status/ ... or a `v=` parameter is one.
 */
export function looksLikeOneVideo(url) {
  try {
    const u = new URL(url);
    return (/\/(videos?|reels?|shorts?|status|clips?|p|posts|tv|watch)\/[^/?#]+/i.test(u.pathname) && !/^\/watch\/?$/i.test(u.pathname))
      || u.searchParams.has('v')
      || /permalink\.php|\/video\.php/i.test(u.pathname);
  } catch {
    return false;
  }
}

/**
 * A <video> the page keeps out of sight: nearly see-through and tiny. Players load one to unlock autoplay or to measure things
 * (TikTok's 50 px `playback1.mp4`); it is not a video anybody wants, and listing it would hide the real one.
 * `box` is its size on the screen and `opacity` its see-through-ness, as the page's script reports them.
 */
export function isDecoyVideo(video) {
  const [width, height] = Array.isArray(video?.box) ? video.box : [0, 0];
  const opacity = Number.isFinite(video?.opacity) ? video.opacity : 1;
  return opacity < 0.05 && Math.max(width, height) < 100;
}

/** A YouTube player embedded in another site's page (an iframe): its watch page, which is what yt-dlp reads. */
export function embedWatchUrl(frameUrl) {
  try {
    const u = new URL(frameUrl);
    if (!/^(www\.)?youtube(-nocookie)?\.com$/i.test(u.hostname)) return null;
    const match = /^\/embed\/([\w-]{6,})/.exec(u.pathname);
    return match ? `https://www.youtube.com/watch?v=${match[1]}` : null;
  } catch {
    return null;
  }
}

/** The address a page's script reports for a video, if it is something ShadeNet may hand to yt-dlp: same site list, no fragment. */
export function acceptPageAddress(address, extra = []) {
  try {
    const u = new URL(address);
    if (!isPageSite(u.href, extra)) return '';
    u.hash = '';
    return u.href;
  } catch {
    return '';
  }
}
