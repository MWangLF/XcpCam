# XcpCam - Visual Camera Path Editor for FFXIV GPose

<p>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Release" src="https://img.shields.io/github/v/release/MWangLF/XcpCam?include_prereleases&style=flat-square&label=Release&color=blue"></a>
  <a href="https://github.com/MWangLF/XcpCam/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/MWangLF/XcpCam/total?style=flat-square&label=Downloads&color=blue"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/github/license/MWangLF/XcpCam?style=flat-square&label=License&color=blue"></a>
</p>

<p>
  <a href="README.md"><img alt="中文" src="https://img.shields.io/badge/%E4%B8%AD%E6%96%87-lightgrey?style=flat-square"></a>
  <a href="README.en.md"><img alt="English" src="https://img.shields.io/badge/English-blue?style=flat-square"></a>
  <a href="README.ja.md"><img alt="日本語" src="https://img.shields.io/badge/%E6%97%A5%E6%9C%AC%E8%AA%9E-lightgrey?style=flat-square"></a>
  <a href="README.ko.md"><img alt="한국어" src="https://img.shields.io/badge/%ED%95%9C%EA%B5%AD%EC%96%B4-lightgrey?style=flat-square"></a>
</p>

> **Current Version: 0.0.6.4 (Pre-release)**. XcpCam is under active development. Feedback and bug reports are welcome via [GitHub Issues](https://github.com/MWangLF/XcpCam/issues).

**XcpCam** is a Dalamud plugin for creating and editing cinematic camera paths directly inside FFXIV GPose. Place camera nodes, shape Blender-style 3D curves, and attach bone-tracking constraints on a frame-based multi-track timeline—then export `.xcp` camera files for XAT / Brio or sync live with **Brio**'s Vivacity timeline and character animations.

## Features

- **In-Game Visual Editing**
  - Real-time 3D overlay showing camera trajectories (with blue-to-red speed coloring), camera frustums, hard-cut markers, and tracking aim lines.
  - Pick camera nodes, curve control points, and Bézier handles directly in the viewport. Transform gizmos automatically follow your selection with translate / rotate / scale, local space, snapping, and multi-selection group translate & rotate.
  - Right-click context menus on viewport nodes, automatic window opening on GPose entry (toggleable), and fully configurable shortcuts.
- **Multi-Track Camera Timeline & Hard Cuts**
  - Frame-accurate multi-track timeline with track priority layering, mute / solo, drag retiming, hold frames, box selection, and multi-step undo / redo.
  - Supports both **in-track hard cuts** and **multi-track cuts**, with instantaneous cuts in live preview and Brio baking.
- **Blender-Style 3D Curves & Constraints**
  - **Curve Modeling**: Create Bézier curves, Bézier circles, and NURBS paths; extrude, subdivide, toggle cyclic / direction, and adjust handles directly in-game.
  - **Follow Path**: Move cameras along 3D curves with curve points shown on the timeline, fractional-frame curve-to-timeline anchoring, and two-way speed synchronization.
  - **Bone Tracking**: Damped Track, Locked Track, and Track To constraints targeting any character bone, with screen-space framing offset, Keep Framing (distance-adaptive FoV), and bone trajectory recording for offline export.
- **Dual-Mode Graph Editor**
  - **Advanced Mode**: Edit path progress, world-space position offsets, unwrapped rotations (multi-turn support), and FoV Bézier handles directly.
  - **Simple Mode**: Adjust pacing along your spatial path without altering its shape, with linked or independent Position / Rotation / FoV timing.
- **Deep Brio Integration**
  - Bind to either Brio's **Vivacity Timeline** or a character's **Animation Scrubber**, with automatic FPS conversion and two-way length / playhead sync.
  - Supports **Live Camera Preview**, **Synchronized XAT Playback**, and **One-Click Baking** to Brio's Free Camera timeline track (undoable).
- **`.xcp` Import & Editable Project Export**
  - **Editable `.xcp` Export**: Embeds the full XcpCam project alongside standard playback frames (enabled by default), so re-importing restores tracks, curves, anchors, and live bone-tracking constraints.
  - **Smart Plain `.xcp` Import**: Reconstructs external `.xcp` files into editable camera nodes and curves with error reporting and cut confirmation—no per-frame keyframe spam.

## Installation

Requires **Dalamud API 15 / .NET 10**. Live preview, synced playback, and baking require [Brio](https://github.com/Etheirys/Brio).

### 📦 Custom Dalamud Repository (Recommended)

1. Open `/xlsettings` in-game and go to the **Experimental** tab.
2. Add the following URL under **Custom Plugin Repositories**, enable it, and click **Save**:
   ```text
   https://raw.githubusercontent.com/MWangLF/XcpCam/main/repo.json
   ```
3. Open `/xlplugins`, search for **XcpCam**, and install it.
4. The window opens automatically when entering GPose, or toggle it anytime with `/xcpcam`.

### 🛠️ Manual Installation (Dev Plugin)

1. Download and extract the latest archive from [Releases](https://github.com/MWangLF/XcpCam/releases).
2. Add the full path to `XcpCam.dll` under `/xlsettings` → **Experimental** → `Dev Plugin Locations`.
3. Enable XcpCam in `/xlplugins` → **Dev Tools**.

## Support

Found a bug or have a feature request? Please open an issue on [GitHub Issues](https://github.com/MWangLF/XcpCam/issues).

## Building from Source

Requires the .NET 10 SDK and Dalamud (API 15):

```bash
dotnet build -c Release
```

## Acknowledgements

- [Brio](https://github.com/Etheirys/Brio) (GPL-3.0) — XAT camera playback and Vivacity timeline
- [XAT](https://github.com/Etheirys/XAT) — `.xcp` camera file format
- [Blender](https://github.com/blender/blender) (GPL-2.0-or-later) — Curve objects, Bézier handles, NURBS, curve editing, and Follow Path / Tracking constraint concepts
- [psenxiv/vista](https://github.com/psenxiv/vista) — Arc-length lookup and monotone PCHIP timing ideas
- [ReplayMod / ReplayStudio](https://github.com/ReplayMod/ReplayStudio) — Keyframe-timed spline camera editing inspiration
- Shoemake (1985/1987) — SQUAD quaternion interpolation

## License

Licensed under the [GPL-3.0](LICENSE) license.
