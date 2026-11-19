# DecklinkSwitcher — Operator Manual

## 1. Overview
**DecklinkSwitcher** is a live video & audio production switcher for Windows supporting Blackmagic DeckLink SDI/HDMI I/O, desktop/webcam capture, local media, YouTube Live, HTML/web pages, NDI, SRT, multi-box picture-in-picture scenes, overlays, and live streaming/recording.

---

## 2. Quick Start
1. **Launch** the latest `DecklinkSwitcher_*.exe` from `bin\Release\net10.0-windows\win-x64\`.
2. **Select Output Device & Format** (top/output panel):
   - Choose a **DeckLink Output** device (or run in software-only mode if no hardware output is selected).
   - Select your **Video Format** (e.g., `1080i50`, `1080p25`, `720p50`, `PAL`).
3. Click **Start Switcher** (if auto-start is not enabled) to activate the inputs and source panel.
4. Click any **Source Tile** (or its preview thumbnail) to switch it to **Program (PGM)**.

---

## 3. Program (PGM) & Transitions
- **Transition Selector** (below PGM preview, beside the duration box `0.1s` – `10.0s`):
  - **`Cut`**: Instant switch to the selected source.
  - **`Mix`**: Smooth cross-dissolve video transition + synchronized **AFV audio crossfade**.
  - **`Wipe L→R` / `Wipe R→L` / `Wipe T→B` / `Wipe B→T` / `Box Out`**: Directional and center-expanding broadcast wipes (rendered in both the in-app PGM preview and 1080p DeckLink, Fullscreen, Stream, Record, NDI, and SRT outputs).
  - **`Stinger`**: Plays an alpha-keyed PNG/image sequence over the transition. When **Stinger** is selected, click **`📁`** to choose a folder of image frames (natural-sorted, up to 300 frames). The outgoing source stays underneath for the first 50% and cuts cleanly to the incoming source at the midpoint while the Stinger graphic finishes playing out.
- **Fullscreen Preview**:
  - Click **`⛶ Fullscreen`**, **double-click** the PGM preview box, or press **`F11`** to send a crisp `1920×1080` program feed (including all overlays) to a secondary monitor (or the current display if only one monitor is connected).
  - Press **`ESC`**, **`F11`**, **double-click** the fullscreen window, or click **`✕ Exit Fullscreen`** to exit.

---

## 4. Built-In Sources
| Source | Controls & Notes |
|---|---|
| **DeckLink Inputs** | Auto-detected SDI/HDMI inputs with live preview, L/R audio meters, gain slider, and `AFV / ON / OFF`. |
| **Screen Capture** | Captures connected monitors at 25 fps. Works both on PGM and inside Multi-Box scenes. |
| **Webcam** | USB / UVC cameras with live preview and audio routing. |
| **Local Video (Media)** | `Select File` (video or image), `Cue`, unified `Pause / Resume` toggle, `Stop`, `Loop`, seek slider, L/R meters, gain slider + `%` label, `AFV / ON / OFF`. |
| **YouTube Live / Video** | Paste YouTube URL, click `Connect` / `Stop`, gain slider + `%` label, `AFV / ON / OFF`. Automatic PTS-based A/V lip-sync (`libvlc_clock`), and auto-connects on startup if a URL is saved. |
| **SRT Input** | Full **SRT Settings** (**Mode** `Caller`/`Listener`, **Port**, **Host / IP**, **Latency (ms)**, **Stream ID**, **Passphrase**, auto-built **SRT URL**), `Connect` / `Stop`, L/R meters, gain slider + `%` label, `AFV / ON / OFF`. |
| **HTML / Web Source** | Enter URL or local `.html` file (runs at 60 fps by default), `Load` / `Stop`, `AFV / ON / OFF` mute control. |
| **NDI Source** | Scan network NDI sources (including local **NDI Out**), select & `Connect` / `Stop`, gain slider + `%` label, `AFV / ON / OFF`. |
| **Color Bars & Matte** | Built-in `1080p` SMPTE Color Bars (with `-20 dBFS` 1 kHz tone when audio is active) and **Matte Color** with a 2×5 **Color Swatch Grid** (9 presets + **`...`** custom RGB color picker). |

---

## 5. Dynamic Sources (`+ Add Source ▾`)
Click **`+ Add Source ▾`** at the top-right of the Source Panel to add extra independent sources at any time:
- **`🎨 Matte Color`**: Pick any custom solid color via the color swatch or **Pick Color** button.
- **`🌐 HTML / Web Page`**: Independent offscreen browser source (60 fps) with URL bar, `Load / Stop`, and `AFV / ON / OFF` audio control.
- **`▶ YouTube Live / Video`**: Independent YouTube player with L/R meters, gain slider, `Connect / Stop`, automatic PTS-based A/V sync, `AFV / ON / OFF`, and automatic startup reconnect when a URL is saved.
- **`📡 NDI Source`**: Independent NDI receiver with `🔍` network scan, `Connect / Stop`, gain slider + `%` label, and `AFV / ON / OFF`.
- **`📶 SRT Stream`**: Independent SRT receiver with full **SRT Settings** (Caller/Listener, Port, Host/IP, Latency, Stream ID, Passphrase, auto-built URL), `Connect / Stop`, L/R meters, gain slider + `%` label, `AFV / ON / OFF`, and auto-connect on startup.
- **`🎬 Media / Video File`**: Independent VLC media player with `Select File`, `Cue`, unified `Pause / Resume` toggle, `Stop`, `Loop`, seek slider, L/R meters, gain slider + `%` label, and `AFV / ON / OFF`.
- **`▦ Multi-Box Scene`**: Creates an additional Multi-Box scene tile with its own independent layout and **Edit Layout...** window.

### Managing Dynamic Tiles
- **Rename**: Click **`✎`** (or double-click the title on extra source tiles). Renaming a source automatically updates any Multi-Box layer referencing it.
- **Remove**: Click **`✕`** on the tile header (or bottom bar for Multi-Box scenes).
- **Persistence**: All dynamic sources, URLs, colors, audio states, and Multi-Box layouts are saved automatically to `%AppData%\DecklinkSwitcher\decklink_switcher_settings.json`.

---

## 6. Multi-Box / Composite Layout Editor
Click **Edit Layout...** on any Multi-Box tile to open the visual layout editor. Editing a Multi-Box scene that is *not* currently on PGM will not disturb the live PGM output.

### Canvas Tools
- **Move / Resize**: Drag any box to reposition it; drag the bottom-right corner handle to resize.
- **Crop Edges**: Switch to **Crop Edges** and drag the yellow `L / T / R / B` handles (up to `98%` per edge). Cropping masks the frame **in-place without stretching** and leaves cropped-out areas blank. Click **Reset** on a row (or **Clear All Crops**) to reset crops to `0%`.

### Layer Controls
- **On**: Enable/disable the box.
- **Z (`▲ / ▼`)**: Reorder layer stacking (front/back).
- **Source**: Choose any DeckLink input, Screen, Webcam, Media, YouTube, HTML, NDI, Color Bars, or dynamically added source.
- **Title / Name (use `$$` for loop)**: Enter a participant name/title (or multiple captions separated by `$$`, e.g. `Alex Rivera$$Host & Producer`) to display a lower-third badge inside the bottom-left of that box. The background pill stays visible while only the text wipes in/out left-to-right every **5 seconds** in a continuous loop. Leave blank for no badge.
- **Border & Crop Reset**: Adjust box border thickness (`0–12 px`) or click **Reset** to clear crop edges; all positioning, resizing, and cropping are done visually on the canvas.
- **`+ Add Box` / `✕`**: Dynamically add or remove boxes.
- **Background**: Set the canvas background color (Black, Navy, Broadcast Blue, Slate, Chroma Green, or Custom).

### Quick Layout Presets
- **Standard Presets**: `2-Up Side-by-Side`, `PiP (Main + Corner)`, `3-Box (1 + 2 Side)`, `2×2 Quad`.
- **Crop Presets**:
  - `2-Split (L/R)` / `2-Split (Top/Bot)`: 50/50 split without stretching.
  - `2-Mid (Center Crop)` / `3-Mid (Center Crop)`: Takes the **middle portion** of each camera/source and places them side-by-side with zero distortion.
  - `3-Col Split` / `4-Col Split`: 3 or 4 equal vertical strips across the frame.
  - `Center Box`: Displays only the center portion of a single source.
  - `Clear All Crops`: Resets all crop values to `0%`.

---

## 7. Overlays (Downstream Keyers)
All active overlays composite onto PGM, Fullscreen Preview, DeckLink SDI, Stream, Record, NDI, and SRT:
- **Logo Overlay**: Supports static images (`.png`, `.jpg`, etc.) and looping video files, with X/Y position, scale, and opacity controls.
- **HTML Overlay**: Transparent browser layer (`CefSharp` OSR, `60 fps` by default) for scoreboards, tickers, or alerts (e.g., StreamElements/OBS browser URLs) with opacity control.
- **NDI Overlay**: Alpha-keyed NDI source overlay (also discovers local **NDI Out**) with opacity control.

---

## 8. Audio Mixer
Each audio-capable source has an audio state selector:
- **`AFV` (Audio-Follow-Video)**: Automatically unmutes when that source is on PGM (and crossfades during Mix transitions), and mutes when off-air.
- **`ON`**: Always mixed into Program audio regardless of which video source is on air.
- **`OFF`**: Always muted.

Use each channel's vertical gain slider (`0.0` – `1.5`) and the **Master PGM** slider to balance levels while monitoring the L/R peak meters.

---

## 9. Outputs (Stream, Record, NDI, SRT)
- **Local Recording (`START RECORDING`)**: Records the 1080p composited PGM video + mixed audio to disk (supports MP4, TS, ProRes, XDCAM HD422, and **XDCAM Sony Compatible** `.mxf` with 2 discrete 24-bit 48 kHz mono `pcm_s24le` audio tracks, in Infinite or Interval mode).
- **YouTube Streaming 1 & 2 (`START STREAM` / `START STREAM 2`)**: Two independent RTMP stream outputs, each with its own **Server URL** (default `rtmp://a.rtmp.youtube.com/live2/`), **Stream Key**, elapsed timer, status badge (`● OFFLINE` / `● CONNECTING` / `● LIVE` / `● ERROR`), and live FFmpeg telemetry (`fps • kbps • speed`). Uses `Stopwatch`-paced CFR delivery (`4500k` / `9000k` VBV) for smooth, buffer-free YouTube ingest.
- **Facebook Streaming (`START FB STREAM`)**: Dedicated Facebook Live RTMPS output (**Server URL** default `rtmps://live-api-s.facebook.com:443/rtmp/`, **Stream Key**), with the same live status badge, elapsed timer, and FFmpeg `fps • kbps • speed` telemetry.
- **NDI Output (`START NDI`)**: Publishes the composited PGM feed as an NDI source on your local network.
- **SRT Output (`START SRT`)**: Streams PGM over SRT (Caller/Listener, Port, Host/IP, Latency, Stream ID, Passphrase).

---

## 10. Troubleshooting
- **A YouTube / Media / NDI box is black in Multi-Box**: Ensure you clicked **Connect** / **Play** on that source's tile first; Multi-Box renders live frames once the source starts decoding.
- **Audio keeps playing when off-air**: Check that the source's dropdown is set to **`AFV`** (not `ON`).
- **Source not updating when renamed**: Use the **`✎`** button on the dynamic tile so Multi-Box references update automatically.
- **Settings Reset**: Close the app and delete or back up `%AppData%\DecklinkSwitcher\decklink_switcher_settings.json`.

