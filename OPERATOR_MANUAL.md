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
- **Cut Switching**: With **Mix** turned off, clicking any source cuts immediately to PGM.
- **Mix (Dissolve) Transition**:
  - Enable the **Mix** toggle and set **Mix Duration** (`0.1s` – `10.0s`).
  - Switching sources performs a smooth video dissolve (visible in the in-app PGM preview, Fullscreen Preview, DeckLink SDI, Stream, Record, NDI, and SRT) along with a synchronized **AFV audio crossfade**.
- **Fullscreen Preview**:
  - Click **`⛶ Fullscreen Preview`** to send a clean `1920×1080` program feed (including all overlays) to a secondary monitor (or primary display if only one monitor is connected).
  - Click **`✕ Exit Fullscreen`** (or close the window) to exit.

---

## 4. Built-In Sources
| Source | Controls & Notes |
|---|---|
| **DeckLink Inputs** | Auto-detected SDI/HDMI inputs with live preview, L/R audio meters, gain slider, and `AFV / ON / OFF`. |
| **Screen Capture** | Captures connected monitors at 25 fps. Works both on PGM and inside Multi-Box scenes. |
| **Webcam** | USB / UVC cameras with live preview and audio routing. |
| **Local Video (Media)** | `Select File` (video or image), `Cue`, `Pause`, `Resume`, `Stop`, `Loop`, seek slider, L/R meters, gain slider, `AFV / ON / OFF`. |
| **YouTube Live / Video** | Paste YouTube URL, click `Connect` / `Stop`, adjust `Sync` audio delay (`0–60000 ms`, default `200 ms`), `AFV / ON / OFF`. Auto-connects on startup if a URL is saved. |
| **HTML / Web Source** | Enter URL or local `.html` file, choose FPS (`25 / 30 / 50 / 60`), `Load` / `Stop`, `AFV / ON / OFF` mute control. |
| **NDI Source** | Scan network NDI sources, select & `Connect` / `Stop`, `AFV / ON / OFF`. |
| **Color Bars & Matte** | Built-in `1080p` SMPTE Color Bars (with `-20 dBFS` 1 kHz tone when audio is active) and custom Matte Color picker. |

---

## 5. Dynamic Sources (`+ Add Source ▾`)
Click **`+ Add Source ▾`** at the top-right of the Source Panel to add extra independent sources at any time:
- **`🎨 Matte Color`**: Pick any custom solid color via the color swatch or **Pick Color** button.
- **`🌐 HTML / Web Page`**: Independent offscreen browser source with URL bar, `Load / Stop`, `25p / 30p / 50p / 60p` FPS selector, and `AFV / ON / OFF` audio control.
- **`▶ YouTube Live / Video`**: Independent YouTube player with L/R meters, gain slider, `Connect / Stop`, `Sync` audio delay (`0–60000 ms`), `AFV / ON / OFF`, and automatic startup reconnect when a URL is saved.
- **`📡 NDI Source`**: Independent NDI receiver with `🔍` network scan, `Connect / Stop`, and `AFV / ON / OFF`.
- **`🎬 Media / Video File`**: Independent VLC media player with `Select File`, `Cue`, `Pause`, `Resume`, `Stop`, `Loop`, seek slider, L/R meters, gain slider, and `AFV / ON / OFF`.
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
- **Title / Name**: Enter a participant name or title to display an animated lower-third badge inside the bottom-left of that box (plays a **5-second looping left-to-right wipe reveal**). Leave blank for no badge.
- **X %, Y %, W %, H %, Crop L/T/R/B %, Border**: Fine-tune coordinates numerically (`X/Y` supports `-95%` to `100%` for center-cropped positioning).
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
- **HTML Overlay**: Transparent browser layer (`CefSharp` OSR) for scoreboards, tickers, or alerts (e.g., StreamElements/OBS browser URLs) with opacity control.
- **NDI Overlay**: Alpha-keyed NDI source overlay with opacity control.

---

## 8. Audio Mixer
Each audio-capable source has an audio state selector:
- **`AFV` (Audio-Follow-Video)**: Automatically unmutes when that source is on PGM (and crossfades during Mix transitions), and mutes when off-air.
- **`ON`**: Always mixed into Program audio regardless of which video source is on air.
- **`OFF`**: Always muted.

Use each channel's vertical gain slider (`0.0` – `1.5`) and the **Master PGM** slider to balance levels while monitoring the L/R peak meters.

---

## 9. Outputs (Stream, Record, NDI, SRT)
- **Local Recording (`START RECORDING`)**: Records the 1080p composited PGM video + mixed audio to disk.
- **Live Streaming (`START STREAM`)**: Streams PGM to your configured RTMP / YouTube Live ingest URL & stream key.
- **NDI Output (`START NDI`)**: Publishes the composited PGM feed as an NDI source on your local network.
- **SRT Output (`START SRT`)**: Streams PGM over SRT to the configured target address/port.

---

## 10. Troubleshooting
- **A YouTube / Media / NDI box is black in Multi-Box**: Ensure you clicked **Connect** / **Play** on that source's tile first; Multi-Box renders live frames once the source starts decoding.
- **Audio keeps playing when off-air**: Check that the source's dropdown is set to **`AFV`** (not `ON`).
- **Source not updating when renamed**: Use the **`✎`** button on the dynamic tile so Multi-Box references update automatically.
- **Settings Reset**: Close the app and delete or back up `%AppData%\DecklinkSwitcher\decklink_switcher_settings.json`.

