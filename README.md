<div align="center">

# ShadeNet Downloader

**A fast, tidy download manager for Windows.** Many connections per file, pause and resume that survives a crash,
videos from YouTube and hundreds of other sites, live-stream recording, and a browser extension that takes downloads over
with your sign-in. Everything is in the box: no .NET, no extra tools to install.

[![Latest release](https://img.shields.io/github/v/release/shade-sdev/ShadeNetDownloader?label=latest%20release)](https://github.com/shade-sdev/ShadeNetDownloader/releases/latest)
&nbsp;
**[⬇ Download the installer](https://github.com/shade-sdev/ShadeNetDownloader/releases/latest)**

<img src="screenshots/downloads.png" alt="ShadeNet Downloader's main window with downloads running and finished" width="860">

</div>

---

## Contents

1. [What it does](#what-it-does)
2. [Install](#install)
3. [A first look](#a-first-look)
4. [Downloading files](#downloading-files)
5. [Videos, streams and live recordings](#videos-streams-and-live-recordings)
6. [The browser extension](#the-browser-extension)
7. [Queue, schedule and "when finished"](#queue-schedule-and-when-finished)
8. [Settings, page by page](#settings-page-by-page)
9. [Keyboard shortcuts](#keyboard-shortcuts)
10. [Updates](#updates)
11. [Troubleshooting](#troubleshooting)
12. [Privacy and limits](#privacy-and-limits)
13. [Building from source](#building-from-source)
14. [Credits](#credits)

---

## What it does

| | |
|---|---|
| **Fast** | A file is fetched over several connections at once. ShadeNet starts with one and adds more only while they really speed things up, so you get the speed without hammering the server. Up to 32 connections per file; a per-site limit protects sites that ban for it. |
| **Safe to interrupt** | Pause, resume, close the program, lose power: unfinished downloads continue where they stopped. Every download is kept in a small local database. |
| **Videos** | Paste the address of a YouTube (or Facebook, Instagram, X, TikTok, Vimeo, Reddit …) video page, choose a quality, and ShadeNet downloads it. HLS and DASH streams are fetched in parallel and joined without re-encoding. Subtitles can be saved or put inside the file. |
| **Live recordings** | A live stream is recorded until you press **Stop and save** (or until a length limit you set). |
| **Browser extension** | For Chrome, Edge, Brave and other Chromium browsers. It hands your downloads to ShadeNet **with the cookies and headers the site needs**, and puts a **Download video** button on videos, named after the video. |
| **Tidy** | Downloads are sorted into category folders (Video, Music, Documents, Programs …). A queue with a schedule, a speed limit, search, sorting, and a details view with a live speed graph and a map of the file. |
| **Yours to style** | Light, dark or Windows' own; ready-made themes, gradient themes, and a choice of app icons. |
| **Self-updating** | It looks for a newer version about once a day and **asks** before installing it. |

---

## Install

1. Open the **[latest release](https://github.com/shade-sdev/ShadeNetDownloader/releases/latest)** and download
   `ShadeNetDownloader-Setup-<version>.exe` (about 300 MB: the whole program, yt-dlp and ffmpeg are inside).
2. Run it. **No administrator rights are needed**; ShadeNet installs for your user only.

<div align="center">
<img src="screenshots/installer.png" alt="The setup window" width="420">
</div>

3. Choose the options, press **Install**:
   * *A shortcut on the desktop* (a Start menu entry is always made);
   * *Start ShadeNet with Windows* (it starts hidden in the notification area);
   * *Set up the browser connection* (Chrome, Edge, Brave); see [The browser extension](#the-browser-extension).

> **Windows SmartScreen** may say the app is from an unknown publisher: the installer is not code-signed yet. Choose
> **More info → Run anyway**. The release page lists SHA-256 checksums (`SHA256SUMS.txt`) if you want to verify the download.

**Where things are.** The program is installed in `%LOCALAPPDATA%\Programs\ShadeNet Downloader`; your settings and the list of
downloads are in `%LOCALAPPDATA%\ShadeNet` (Settings → About → *Data folder → Open*). Your downloaded files go to your
`Downloads` folder by default.

**Install from a script** (for IT or your own setup):

```text
ShadeNetDownloader-Setup-<version>.exe /S /D="C:\Tools\ShadeNet"
```

Add `--no-desktop-shortcut`, `--no-start-menu-shortcut`, `--no-browser-integration`, `--startup` (start with Windows) or
`--launch` as needed.

**Uninstall** from *Settings → Apps → Installed apps*. Your downloads and settings stay (so a reinstall finds them); the
uninstaller asks whether to delete them as well. Silent: `Uninstall.exe --uninstall --silent` (add `--remove-data` to also
delete the data folder).

**Upgrading** by running a newer setup over an installed ShadeNet works too (it asks a running ShadeNet to quit first), but
normally the program updates itself: see [Updates](#updates).

---

## A first look

* **Left:** *Add download*, the lists (*All downloads, Downloading, Queued, Paused, Completed, Failed*) with live counts, and your
  **categories**. *Settings* is at the bottom. The menu button at the top folds the bar to icons.
* **Centre:** the downloads. Each card shows the name, a progress bar, size, speed, time left and the number of connections.
  Buttons on a card: pause/resume, details, open the folder, remove. **Right-click** a card for everything else.
* **Top:** search (type to filter the list), the current total speed, and a theme toggle.
* **Bottom bar:** how many are active/queued/completed, free disk space, **When finished** (what to do when everything is done),
  and the **speed limit**.

Closing the window **keeps ShadeNet running in the notification area** (so downloads and the browser connection carry on).
Use **Exit** in the tray menu to quit for real. Both behaviours can be changed in Settings → General.

<div align="center">
<img src="screenshots/downloads-light.png" alt="The same window in the light theme" width="700">
</div>

---

## Downloading files

### Add a download

Press **Add download** (or **Ctrl+N**), or just press **Ctrl+V** anywhere in the window with a link on the clipboard.

<div align="center">
<img src="screenshots/add-download.png" alt="The Add download dialog" width="760">
</div>

1. **Paste the address.** ShadeNet looks at it at once: the file name, the size, whether the server supports pause and resume.
   You can **paste several links** (one per line) to add them as a batch.
2. **Name and folder.** The name can be changed; *Save to* follows the file's category (here *Video*), or choose another folder with
   **Browse…**.
3. **Connections.** The slider sets the most connections ShadeNet may use for this file (default 8). *The engine starts with one and
   adds more only while they actually speed things up.*
4. **Advanced** holds a sign-in cookie and a referring page, for links that only answer a signed-in browser. (The
   [browser extension](#the-browser-extension) fills these in for you.)
5. Choose **Download now** to start, or **Add to queue** to let it wait its turn.

### Managing downloads

* **Pause / resume** with the button on the card, **Space**, or a double-click. **Pause all / Resume all** are at the top of the list.
* **Select several** cards (Ctrl/Shift-click, or *Select all*) and act on them together.
* **Right-click** a card: Open, Open with…, Details, Pause/Resume, Add to queue, Move to top/up/down/bottom, Refresh address,
  Open the page it came from, Show in folder, Copy link, **Remove from list**, **Remove and delete file**.
* **Remove** (the **Delete** key) takes a download off the list and keeps the file; an **Undo** appears for a few seconds.
* **Refresh address** gets a new link for a download whose link expired (it asks the page the download came from).
* **Sort** by name, type, size or date, ascending or descending; **filter** with the lists and categories on the left, or search.

### Details, connections and speed

Open a card's **Details** (the *i* button, **Alt+Enter**, or right-click → Details) to see what the engine is doing.

<div align="center">
<img src="screenshots/details-overview.png" alt="The details drawer: speed, graph and file map" width="760">
</div>

* **Overview:** current speed, time left, a graph of the last two minutes, and the **file map**: every square is a slice of the file,
  and a coloured ring shows where a connection is writing right now.
* **Connections:** one row per connection with its own speed and the range of the file it is fetching.
* **Properties:** the file's details, plus a **speed limit for this download only** and its own number of connections.

<div align="center">
<img src="screenshots/details-connections.png" alt="The Connections tab" width="760">
</div>

**Speed limit for everything:** the *Limit* button in the bottom bar. Choose a preset or type a speed; a download can also have
its own limit in its details.

<div align="center">
<img src="screenshots/speed-limit.png" alt="The speed limit flyout" width="600">
</div>

### Categories

A finished download is sorted by its file type into **Video, Music, Documents, Programs, Compressed, Images** or **General**,
each with its own folder under your download folder. The file types and folders are yours to edit, and you can add categories
(Settings → Categories). Turn the sorting off in Settings → Downloads to keep everything in one folder.

---

## Videos, streams and live recordings

You don't need to do anything special: **paste the address of the video's page** (or use the
[extension's button](#the-browser-extension)) and the Add dialog asks yt-dlp what is there.

<div align="center">
<img src="screenshots/add-video-stream.png" alt="Add dialog for a video stream with a quality picker" width="760">
</div>

* **Quality:** pick one from the list. Pictures and sound that are kept apart are downloaded in parallel and **joined without
  re-encoding** (so no quality is lost).
* **Where it works:** YouTube and the many sites yt-dlp reads (Facebook, Instagram, X, TikTok, Vimeo, Reddit …), plain **HLS**
  (`.m3u8`) and **DASH** (`.mpd`) streams, and ordinary video files. ShadeNet does the downloading itself (many connections,
  pause and resume); yt-dlp only works out where the video is.
* **Subtitles** are offered when the video has them: as separate files or inside the MP4/MKV.
* **Protected (DRM) videos are refused**, with the reason. ShadeNet does not circumvent copy protection.

### Recording a live stream

When the address is a live stream the dialog says so: *"Recording starts now and goes on until the broadcast ends or you press
Stop and save on its card."*

<div align="center">
<img src="screenshots/add-live-stream.png" alt="Add dialog for a live stream" width="620">
</div>

The card shows **Recording**, how long has been recorded and the size. The square **stop button** on the card (or right-click →
**Stop and save**) ends the recording and saves what was recorded as a normal video file. A recording starts a couple of minutes
behind the live edge, and stopping is quick even for streams with hours of history.

<div align="center">
<img src="screenshots/recording.png" alt="A recording in progress" width="760">
</div>

To limit a recording automatically, set **Stop live recordings after (minutes)** in Settings → Downloads (0 = no limit).
*Stop and save* finishes a recording and keeps the file. (*Pause* is different: on a recording made from a stream's pieces it keeps the
recording open so it can be continued later; a recording made by ffmpeg is saved when paused.)

### The helper programs

**yt-dlp** and **ffmpeg** come with ShadeNet (in `tools\` next to the program) and are used automatically. Settings → Downloads →
*Helper programs* shows which copy is used and its version, lets you point to your own copy, and has a **Check** button. About
once a day ShadeNet also looks for newer versions and **asks** before installing any.

---

## The browser extension

The extension lets ShadeNet take over your browser's downloads and add a **Download video** button to videos. It works in
**Chrome, Edge, Brave** and other Chromium-based browsers. (Firefox is not supported yet.)

### Set it up (once)

If you left *Set up the browser connection* ticked in the installer, its last page walks you through this. Otherwise:

1. In ShadeNet open **Settings → Browser** and press **Set up** (this registers ShadeNet with your browsers; no administrator
   rights needed).
2. In your browser open `chrome://extensions` (or `edge://extensions`) and turn on **Developer mode**.
3. Press **Load unpacked** and choose the folder
   `%LOCALAPPDATA%\Programs\ShadeNet Downloader\browser\chromium` (Settings → Browser shows the exact path and has buttons that open
   the page and the folder).

<div align="center">
<img src="screenshots/settings-browser.png" alt="Settings, Browser page" width="760">
</div>

Browsers don't let a program add an extension for you, so that last click is yours. The extension can talk only to ShadeNet (its
ID is fixed and ShadeNet accepts nothing else), and it **updates with the program**: after an update it reloads itself.

### What you get

* **Downloads are taken over.** When the browser starts a download, the extension pauses it, asks ShadeNet, and if ShadeNet takes
  it, the browser's own copy is cancelled. If ShadeNet can't be reached the browser's download just continues: **a failure never
  loses a download.** Sign-in cookies, the referring page and the browser's identity travel with it, so links that only answer a
  signed-in browser work.
* **Right-click → Download with ShadeNet** on a link, video or audio.
* **A Download video button on videos.** When a page plays something ShadeNet can download, a small button appears on the video
  with its **name, length and quality**. One click sends it; the file is saved under the video's name. It also works for videos
  inside embedded players and for sites that hide the video's address.

<div align="center">
<img src="screenshots/extension-video-button.png" alt="The Download video button on a playing video" width="520">
</div>

* **Several videos on one page** are listed by name in a menu on the button, and the toolbar badge counts them.

<div align="center">
<img src="screenshots/extension-several-videos.png" alt="A menu listing three videos by name" width="440">
</div>

* **The toolbar popup** says whether ShadeNet is running (it looks **without starting it**) and has two switches: *Send downloads
  to ShadeNet* and *Show a Download button on videos*. Nothing to click to connect: it finds the app by itself.

<div align="center">
<img src="screenshots/extension-popup.png" alt="The extension's popup" width="300">
</div>

* If ShadeNet isn't running when a download starts, the extension starts it (minimised).

### Choose how it behaves

In **Settings → Browser** you can switch the takeover off, choose **Ask first** (the Add dialog opens with the sign-in already
filled in) or **Start right away**, list **sites to leave to the browser**, and decide whether your browser's sign-in may be used
for video pages that need it (live streams and private videos). Changes reach the extension immediately.

---

## Queue, schedule and "when finished"

* **Queued** is the list of downloads waiting their turn. ShadeNet runs **3 downloads at the same time** by default (Settings →
  Queue); the rest wait and start as others finish. Reorder them with right-click → *Move to top / up / down / bottom*, and start
  or stop the whole queue.
* **Schedule:** *Run the queue on a schedule* limits the queue to a time window on chosen days, for example at night when
  bandwidth is free.
* **When finished** (bottom bar, or Settings → Queue): *Do nothing, Close ShadeNet, Sleep, Hibernate* or *Shut down the computer*
  when every download is done. It applies to this session only and is never saved; a 60-second countdown with a **Cancel** button
  comes first.

<div align="center">
<img src="screenshots/settings-queue.png" alt="Settings, Queue page" width="760">
</div>

---

## Settings, page by page

Open **Settings** (bottom left, or **Ctrl+,**).

<details>
<summary><b>General</b>: tray icon, closing behaviour, start with Windows, notifications</summary>

<img src="screenshots/settings-general.png" alt="Settings, General page" width="760">

* **Show an icon in the notification area:** left-click opens ShadeNet; right-click has pause, resume, the speed limit and Exit.
* **Closing the window keeps ShadeNet running** (downloads carry on in the background).
* **Start with Windows:** starts hidden in the notification area when you sign in.
* **Notifications** when a download finishes or fails, optionally only when ShadeNet isn't the window in front, with or without
  the Windows notification sound. Clicking a notification shows the file in its folder (ShadeNet never runs a file for you).
</details>

<details>
<summary><b>Downloads</b>: folder, connections, resume on start, recording limit, helper programs</summary>

<img src="screenshots/settings-downloads.png" alt="Settings, Downloads page" width="760">

* **Default download folder** and **Sort downloads into category folders**.
* **Connections per download:** the starting point for new downloads (1 to 32). A site's own limit, or the Add dialog, can lower it.
* **Resume unfinished downloads when ShadeNet starts:** off means they wait, paused, until you resume them.
* **Stop live recordings after (minutes).**
* **Helper programs:** yt-dlp and ffmpeg (see above).
</details>

<details>
<summary><b>Categories</b>: which file types go where</summary>

<img src="screenshots/settings-categories.png" alt="Settings, Categories page" width="760">

A download goes to the category whose list of file types names its extension. Leave a folder empty to use the default under the
download folder; **Browse…** picks another; **Reset** goes back. You can add your own categories.
</details>

<details>
<summary><b>Queue</b>: how many at once, schedule, when everything is finished</summary>

See [Queue, schedule and "when finished"](#queue-schedule-and-when-finished).
</details>

<details>
<summary><b>Network</b>: proxy, time-outs, retries, browser identity, connections per site</summary>

<img src="screenshots/settings-network.png" alt="Settings, Network page" width="760">

* **How to reach the internet:** *Windows* (the proxy set in Windows), *None* (direct) or *Manual* (`host:port`).
* **Connect time-out** (3 to 120 s) and **Retries** (0 to 50) before a broken download fails.
* **Browser identity (User-Agent)** sent to sites unless the extension supplies the browser's own.
* **Connections per site:** some sites ban you for opening many connections; a limit here caps the total open to that site (and
  its sub-domains), however many downloads are running there.
</details>

<details>
<summary><b>Browser</b>: the extension and how it takes over downloads</summary>

See [The browser extension](#the-browser-extension).
</details>

<details>
<summary><b>Appearance</b>: themes, gradient themes, app icons, your own look</summary>

<img src="screenshots/settings-appearance.png" alt="Settings, Appearance page" width="760">

* **Themes:** Windows, Midnight, Ocean, Forest, Sunset, Rose, Paper, Graphite, Nord, Dracula. Pick one, then change anything below;
  your changes are kept as *Custom*.
* **Gradient themes:** a gradient over the whole window with an accent to go with it.
* **App theme:** *System* follows Windows and changes with it live; or *Light* / *Dark*. The sun/moon button at the top of the
  window cycles through them.
* **Accent colour, layout** and more below.
* **Import a theme…** and **Share this look…** let you exchange themes as files.

<img src="screenshots/settings-app-icons.png" alt="The app icon picker" width="760">

**App icon:** choose the icon ShadeNet shows in its window, on the taskbar, in Alt+Tab and in the notification area. Shortcuts
keep the usual icon.
</details>

<details>
<summary><b>About</b>: version, updates, data folder</summary>

* The installed **version**, a **Check for updates** button, and the switch *Look for a newer ShadeNet automatically* (about once a
  day; it always asks before installing).
* The **data folder**, with a button that opens it.
</details>

---

## Keyboard shortcuts

| Keys | What it does |
|---|---|
| **Ctrl+N** | Add download |
| **Ctrl+V** | With a link on the clipboard (and not typing in a text box): open the Add dialog with it |
| **Ctrl+F** | Search the list |
| **Ctrl+,** | Open Settings |
| **Space** | Pause / resume the selected downloads |
| **Enter** | Open the selected finished download (**Alt+Enter** opens its details) |
| **Delete** | Remove the selected downloads from the list (file kept; Undo is offered) |
| **Esc** | Close the details drawer; with it closed, clear the selection |
| **Double-click** a card | Open it when finished, otherwise pause / resume |

---

## Updates

* **ShadeNet itself:** about once a day it looks at this repository's latest release. When there is a newer version it tells you
  (a notification with an **Update** button, and *Settings → About*). Nothing is installed without your OK. The update is a
  small package that is **verified (size and SHA-256)** before use; ShadeNet then closes, replaces its own files, and starts again.
  Your downloads, settings, shortcuts and helper programs are untouched, and a failed update puts the old version back.
  Running downloads are paused and saved, and continue afterwards.
* **yt-dlp and ffmpeg:** checked about once a day too, with the same "ask first" rule.
* A copy of ShadeNet that was not installed with the setup program (for example one you built yourself) says that a new version
  exists but cannot update itself; it links to the release page instead.

Turn the automatic look off in *Settings → About*; **Check for updates** still works whenever you press it.

---

## Troubleshooting

| Problem | What to try |
|---|---|
| **A video page says "nothing downloadable"** | The site may need you to be signed in: in the browser, sign in, then use the extension's button (with *Use my browser's sign-in for video pages* on). Protected (DRM) videos can't be downloaded. |
| **A download fails with a web page instead of the file** | The link needs your browser's session or has expired. Use the extension, or **Advanced** in the Add dialog, or right-click → **Refresh address**. Some sites allow each link only once. |
| **The extension says *ShadeNet is not running*** | That's not a fault: start ShadeNet (or press **Start ShadeNet** in the popup). |
| **The extension says *Not connected*** | Its second line gives the reason. Open *Settings → Browser* and press **Set up** again, which rewrites the registration. |
| **"Specified native messaging host not found"** | Press **Set up** in *Settings → Browser*, then restart the browser. |
| **Downloads are slow** | Check the speed limit in the bottom bar, the *Connections per site* list in Settings → Network, and your proxy setting. |
| **I can't find the window** | ShadeNet is probably in the notification area (closing the window keeps it running): click its icon. |
| **The taskbar or desktop icon looks old after an update** | Windows keeps the picture of an icon it has seen. From version 1.0.5 an update refreshes the shortcuts' pictures, and from 1.0.6 the taskbar button follows the icon you choose in Settings → Appearance. On older versions, update ShadeNet first. |

Log files: the browser connection writes `%LOCALAPPDATA%\ShadeNet\host.log`; a crash writes `crash.log` in the data folder.

---

## Privacy and limits

* ShadeNet runs entirely on your computer. It has **no account and no telemetry**. The only things it contacts are the sites you
  download from, GitHub (the daily look for updates), and the publishers of yt-dlp and ffmpeg (their own daily look).
* The extension sends a download's sign-in cookies, referring page and browser identity **to ShadeNet on your computer**, only for
  that download. Your browser sign-in for a video page goes to yt-dlp in a temporary file that is deleted right after.
* **DRM-protected video is not supported** and never will be.
* Please download only what you have the right to.
* The installer and program are **not code-signed** yet (SmartScreen will warn).
* Browser extension: Chromium browsers only; it is loaded unpacked (Chrome shows a *developer mode extensions* notice at start-up).
* Windows only (64-bit). It is built and tested on Windows 11.

---

## Building from source

You need the **.NET 10 SDK** (pinned in `global.json`) and Windows. Node.js 22 is only needed for the extension's tests.

```powershell
git clone https://github.com/shade-sdev/ShadeNetDownloader.git
cd ShadeNetDownloader
dotnet build                                   # the whole solution
dotnet run --project src/ShadeNetDownloader    # start the app
pwsh tools/ci/test.ps1                         # every test suite, as the release pipeline runs them
pwsh tools/get-tools.ps1 -Tool all             # fetch yt-dlp and ffmpeg (verified) into tools/
pwsh tools/build-release.ps1                   # the installer and the update package in dist/
```

Set `SHADENET_DATA_DIR` to run against a throw-away data folder.

| Folder | What is in it |
|---|---|
| `src/ShadeNet.Core` | The download engine: probing, segmenting, resume, speed limiter, HLS/DASH, recording, updates |
| `src/ShadeNetDownloader` | The WPF app ([WPF-UI](https://github.com/lepoco/wpfui), .NET 10) |
| `src/ShadeNet.Bridge`, `src/ShadeNet.NativeHost` | The pipe and native-messaging host between the browser and the app |
| `src/ShadeNet.Setup` | The installer, the uninstaller and the updater (one project) |
| `extension/chromium` | The Manifest V3 browser extension |
| `tests/` | Engine, program, bridge, installer and extension tests |
| `tools/` | Build, release and end-to-end scripts, the command-line test tool |

**Releases.** Every merge to `master` is a release: the workflow in `.github/workflows/release.yml` picks the next version
(`v<MAJOR.MINOR>.N`), runs all tests, builds the installer and the update package, checks them, and publishes them with
`update.json` and checksums. Add `[skip ci]` to a commit message to skip a release (for a documentation-only change, say).
Installed programs read `releases/latest/download/update.json` to learn about the newest version.

---

## Credits

* [yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) finds the videos behind web pages.
* [ffmpeg](https://ffmpeg.org/) (LGPL build) joins pictures, sound and subtitles.
* [WPF-UI](https://github.com/lepoco/wpfui) for the Windows 11 look.
* Licences of the bundled programs are in `licenses\THIRD-PARTY-NOTICES.txt` inside the installation.
