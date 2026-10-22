# ShadeNet Downloader: browser extension (Chrome, Edge, Brave, other Chromium browsers)

Sends the downloads your browser starts to ShadeNet Downloader, **with the cookies, referrer and headers the site needs**.
That is what makes links work that only answer a signed-in browser (a share link that redirects to a web page unless
the session cookie is sent, for example).

## Install (once)

1. In ShadeNet: **Settings → Browser integration → Set up**. This registers the native messaging host for Chrome, Edge,
   Brave and Chromium under *HKEY_CURRENT_USER* (no administrator rights).
2. In your browser open `chrome://extensions` (or `edge://extensions`), turn on **Developer mode**.
3. Choose **Load unpacked** and select this folder. (**Settings → Browser integration** shows the exact path and has
   buttons that open the page and the folder.)

The extension has a fixed ID (`bcegodlanfeghhakadmalbkbhbhbejop`, derived from the `key` in `manifest.json`); the native
host's manifest allows exactly that ID and nothing else, so no other extension can talk to ShadeNet.

To remove everything: **Settings → Browser integration → Remove**, then remove the extension in the browser.

## What it does

* When the browser starts a download, the extension **pauses** it, asks ShadeNet, and if ShadeNet takes it over
  **cancels and erases** the browser's copy; if ShadeNet cannot be reached or declines, the browser's download simply
  **resumes**. A failure never loses a download. (The same order IDM uses.) The browser's file-name step (where its "Save as"
  dialog would open) is held until that is decided, so a download ShadeNet takes over never shows the browser's dialog.
* It sends the address, the referring page, the cookies the browser sent (HttpOnly ones included), the user agent and a
  short allow-list of headers (`Authorization`, `Origin`, `Accept-Language`).
* Right-click a link, video or audio → **Download with ShadeNet**.
* The toolbar button shows whether ShadeNet is connected and has a switch to stop capturing. It finds the app by itself:
  opening the popup (or starting the browser) looks whether ShadeNet is running **without starting it**; the popup says
  "ShadeNet is not running" (with a Start button) or "Connected", and follows the app while it is open. Nothing to click.
* **Videos announce themselves.** When a page plays something ShadeNet can download (a video file, an HLS/DASH stream, a
  YouTube video), a small "Download video" button appears on the video with its **name**, length and quality; with
  several videos a menu lists each by name. The toolbar badge counts them and its tooltip names them, and the popup
  lists them too. One click hands the video over; the file is saved under the video's name. The switch "Show a Download
  button on videos" in the popup turns the button off. (`content.js` runs in every page and frame; it only reports the
  `<video>` elements to the extension and draws the answer.) Pages that were open before the extension was reloaded need a refresh.
* **Videos in iframes** (an embedded player from another site) get their own button, and an embedded YouTube player is
  offered as its watch page.
* **Videos with no address** (Facebook, Instagram, X, TikTok, Vimeo, Reddit ... play through a `blob:` buffer, so no file
  address exists anywhere): the button hands the video's **page** to ShadeNet, whose Add dialog asks yt-dlp and lists the
  qualities, as for YouTube. The page address is the post's own link when the video sits in a feed (the link in the card
  around it), else the page you are on. Only sites yt-dlp is known to read get this button (`lib/sites.js`), so it never
  promises what cannot happen. Your browser sign-in is **not** passed on, so videos that need one (friends-only posts)
  cannot be read; **DRM-protected players are left alone on purpose**.
* **Playlists under neutral names.** A player may serve its HLS/DASH playlist as `/p/9f3a` with type
  `application/octet-stream`, or as a `.jpg`. Small answers of a neutral type are read once and recognised by what they
  contain (`#EXTM3U`, `<MPD`); video pieces and scripts are never read, and a tab is limited to 40 such looks. The look is
  made with the `Referer`/`Origin` the player itself sent (a short-lived `declarativeNetRequest` session rule, removed right
  after; permission `declarativeNetRequestWithHostAccess`), because players in iframes often answer only their own page. The
  pieces are later downloaded with the player's page as referrer.
* **Picture and sound as two streams.** Some players ask for the picture's playlist and the sound's playlist one after the
  other, with no master playlist above them. They used to be two entries in the list. The first bytes of each stream's first
  piece say what it holds (`lib/tracks.js`: the MP4 initialisation part names its `vide`/`soun` tracks, an MPEG-TS stream
  lists its elementary streams, a bare AAC/MP3 file is sound), and a picture-only stream and a sound-only stream of the same
  length become **one entry** ("720p · picture + sound"): the badge counts one, and one click downloads both and joins them
  without re-encoding. Several qualities of picture each pair with the sound. A stream that holds both, or cannot be
  inspected, is listed as before; a stream that is only sound is called "Sound only". Not covered: picture and sound as two
  plain files (not playlists) that a player fetches by byte ranges, and DASH manifests that list both (those are one entry
  already).
* **A try.** A hidden-address video on a site yt-dlp is not known for, with nothing found on the network, gets a button
  "Try to download this video": ShadeNet asks yt-dlp about the frame's own page, and the dialog says why if it cannot.
* ShadeNet can be started by the extension: if it is not running, the host launches it minimized.

Settings that apply are set in the app (Settings → Browser integration): capturing on/off, ask first or start right
away, and sites to leave to the browser. The app pushes them to the extension live.

## Limits worth knowing

* **One-time links.** The browser has already made the first request when the extension hears about the download. A
  site that allows each link only once will answer ShadeNet's request with a web page; ShadeNet then says so on the card.
  IDM works around this with a hot key that hands the download over *before* the browser's request, which a Manifest V3
  extension cannot do. *Refresh address* (right-click a failed card) covers expired links.
* Chromium browsers only. Firefox needs a different manifest and host registration; not done yet.
* Loaded unpacked: Chrome shows a "developer mode extensions" notice at start-up. Publishing to the Web Store is a
  separate decision.

## Troubleshooting

* Toolbar popup says **ShadeNet is not running**: that is not a fault; start the app (or press Start ShadeNet).
* Toolbar popup says **Not connected**: its second line shows the reason (`The app did not answer in time`, the host's
  error, …). In ShadeNet open Settings → Browser integration and check the status line.
* The host keeps a short log: `%LOCALAPPDATA%\ShadeNet\host.log`.
* The extension keeps the last 40 steps it took: open `chrome://extensions`, click **service worker** under ShadeNet,
  and in the console type `shadenet.controller.events`.
* "Specified native messaging host not found": run **Set up** again (it rewrites the registry entry and manifest).

## Development

* Code: `lib/capture.js` (pure decisions), `lib/controller.js` (behaviour, with injected browser APIs),
  `lib/native.js` (host connection), `lib/headers.js` (recent request headers), `background.js` (wiring), `popup.*`.
* Unit tests need only node: `node --test "test/*.test.mjs"` in this folder. `dotnet test` runs them too.
* Real-browser check: `tools/e2e/run-chrome-e2e.ps1` starts ShadeNet, a test server and a Chrome with a throw-away
  profile, loads this extension (`Extensions.loadUnpacked` over the DevTools pipe: Chrome 137+ ignores
  `--load-extension`), downloads a file that needs a session cookie, and checks that ShadeNet got it byte-exact.
  `-AppNotRunning` makes the host start the app. It registers the host under HKCU for the run and removes it afterwards.
