# RetroCap

A QQ-style screenshot and annotation tool for Windows 10 and 11, written in native .NET 8 (WPF).
Press **Ctrl+Alt+A** anywhere, pick a region, mark it up, and copy it to the clipboard.
Press **Ctrl+Alt+Z** to record a region of the screen to MP4 or GIF.
The UI follows the [**Retro.Net design system**](https://github.com/owen800q/Retro.NET).



![Annotating a capture](docs/screenshot-annotate.png)

Demo video: [docs/demo.mp4](docs/demo.mp4) (recorded under Wine on Linux, so window chrome
around other apps looks like Wine rather than Windows 10).

## Features

- **Global hotkey** Ctrl+Alt+A (system tray app; single instance). If another program (QQ, WeChat)
  already owns the hotkey, the main window says so and you can capture from the window or tray icon.
- **Region selection** over every monitor: drag a rectangle, or click to take the highlighted
  window under the cursor. Eight resize handles, drag to move, arrow keys to nudge (Shift+arrow resizes).
- **Magnifier** with 4x pixel zoom, cursor position and the RGB/hex colour under the cursor.
- **Annotation tools**
  | Tool | Key | Notes |
  |---|---|---|
  | Rectangle | R | Shift = square |
  | Ellipse | E | Shift = circle |
  | Arrow | A | tapered QQ-style arrow; Shift snaps to 45° |
  | Pen | P | freehand |
  | Highlighter | H | translucent marker; Shift = straight line |
  | Text | T | supports Chinese/Japanese/Korean input |
  | Mosaic | M | pixelates whatever you paint over |
- **Three sizes and a 16-colour palette** per tool, plus **More...** for any custom colour.
- **Undo** (Ctrl+Z).
- **Output**: Copy (Enter / double-click / Ctrl+C), Save As PNG/JPG/BMP (Ctrl+S),
  **Pin to screen** (F3; drag to move, wheel to zoom, double-click to close).
  Optional auto-save of every copy to a folder.
- Esc cancels. Right-click backs out of the current selection.
- **Screen recording** with Ctrl+Alt+Z (also the Record button and the tray menu):
  pick a region the same way as for a screenshot, choose **MP4** (H.264, 30 fps; key V) or **GIF**
  (10 fps; key G) and press Enter. A red frame marks the region and a small bar shows the elapsed
  time; press Ctrl+Alt+Z again or click **Stop**. The file is saved to the save folder as
  `Recording_yyyyMMdd_HHmmss.mp4` / `.gif` and put on the clipboard as a file, ready to paste into
  a chat. The mouse cursor is included in the recording.
  - MP4 uses the Windows Media Foundation H.264 encoder. Windows "N" editions need the Media
    Feature Pack; use GIF otherwise.
  - GIFs store only the part of each frame that changed, with a 256-colour palette per frame,
    so screencasts of UI stay small. Photos and video in the region will look posterised.

- **Start automatically** when you sign in to Windows (Options → *Start automatically when I sign in
  to Windows*). By default it starts hidden in the notification area; untick *Start hidden in the
  notification area* to open the window instead. The entry lives under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` and is refreshed on every launch, so it keeps
  working if you move `RetroCap.exe`.

## Download

Grab the latest `RetroCap-win-x64.zip` or `RetroCap-win-arm64.zip` from **Releases**.

Every merge to `master` publishes a new release automatically: GitHub Actions takes the latest
`vX.Y.Z` tag, bumps the patch number (v1.0.1 → v1.0.2), builds with that version and attaches both zips.
To start a new major or minor version, push a tag yourself (for example `git tag v1.1.0 && git push origin v1.1.0`);
the next merge then continues from it (v1.1.1). Pull requests are built but not released
(**Actions → Build → Artifacts**). The exe is a self-contained single file, so you don't need to install .NET.

## Build locally

```powershell
dotnet publish src/RetroCap/RetroCap.csproj -c Release -r win-x64 -o dist
dist\RetroCap.exe
```

Command-line switches: `--tray` (start hidden in the notification area), `--capture`
(start a capture immediately) and `--record` (start picking a region to record).

## Project layout

```
src/RetroCap/
  App.xaml(.cs)          startup, single instance, hotkey, tray
  MainWindow.xaml(.cs)   settings window (SAP window chrome)
  Capture/
    CaptureWindow.cs     full-screen overlay: selection, tools, output
    CaptureToolbar.xaml  floating SAP toolbar (tools, sizes, palette)
    RecordToolbar.cs     format / Record toolbar shown in record mode
    Annotations.cs       rectangle / ellipse / arrow / pen / highlighter / mosaic / text
    Magnifier.cs         pixel loupe
  Recording/
    ScreenRecorder.cs    capture thread (GDI + cursor) feeding an encoder thread
    Mp4Encoder.cs        H.264 MP4 via the Media Foundation sink writer
    GifEncoder.cs        animated GIF: changed-rectangle frames, median-cut palette, LZW
    RecordingOverlay.cs  red frame and Stop bar shown while recording
  Services/              screen capture, hotkey, clipboard/save, settings, tray icon
  Theme/
    Theme.xaml           Retro SAP GUI tokens and control styles for WPF
    Icons.xaml           GENERATED from design/retro-sap-gui/icons.svg
  Ui/                    Bevel decorator, SapWindow chrome, pin window, toast
design/retro-sap-gui/    design-system tokens, component CSS, icon sprite
tools/                   gen_icons.py (SVG to XAML), gen_appicon.py (app.ico)
```

### Design system notes

- Tokens in `Theme/Theme.xaml` mirror `design/retro-sap-gui/colors_and_type.css`. Corners are
  always square and there are no shadows; depth comes from 1px two-tone bevels drawn by `Ui/Bevel.cs`.
  The single warm accent is SAP amber `#F0AB00`, used for the selection frame, the primary
  button and pressed tools.
- The icon sprite gained a class palette (the original left the classes uncoloured) and new
  capture icons: `tool-rect`, `tool-ellipse`, `tool-arrow`, `tool-pen`, `tool-highlighter`,
  `tool-text`, `tool-mosaic`, `pin` and `capture`. After editing `icons.svg`, run
  `python3 tools/gen_icons.py` (CI fails if `Icons.xaml` is out of date).
- The UI font is Microsoft Sans Serif, which ships with Windows.
