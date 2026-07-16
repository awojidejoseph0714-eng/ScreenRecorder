# Screen Recorder v2

Screen Recorder v2 is a local-first, ambient desktop video DVR that continuously captures your screen into a rolling buffer on your own machine. Designed as a lightweight background utility, it indexes on-screen text using native Windows WinRT OCR, allowing you to instantly search, pause, rewind, and replay your work session. 

No cloud, no AI APIs, and no enterprise bloat. Just your recordings on your disk.

---

> ⚠️ **Windows SmartScreen Notice**: Because this application is a newly compiled, community-driven desktop app and is not code-signed with a commercial certificate, Windows may show a "protected your PC" popup on download.
>
> **How to Run:** Click **"More info"** on the popup, then click **"Run anyway"** to launch the installer.

---

## Features

- **Ambient Capture**: Efficiently records your chosen monitor at 20 FPS in the background.
- **Optimized Video Seeking**: Strict Group of Pictures (GOP) keyframe spacing built into the native H.264 encoder, enabling **instant, frame-accurate timeline scrubbing**.
- **Active App Exclusions**: Monitors foreground window process names. The capture loop automatically pauses and cuts video segments when you focus on private apps (e.g., browsers, password managers, or notepad) and resumes when you switch back.
- **Local OCR & Search (SQLite FTS5)**: Scans screen text asynchronously on a background thread using native Windows WinRT OCR. All entries are written to a SQLite database (WAL mode enabled) with full-text search index support.
- **Tray Blinker & Glow Indicator**: Disables intrusive frames, opting for a clean, blinking notification tray status icon and a transparent click-through red window border glow.
- **Storage Safety calculations**: Automatically limits disk footprint by cleaning up expired files older than your configured buffer length (e.g., 24 hours). The settings UI warns you when disk space is critically low.
- **Windows Autostart Option**: Can optionally configure a run registry key to run in the background automatically on Windows startup.

---

##  Data Directories

Your data and settings are kept strictly separate from the application binaries, ensuring that upgrading or uninstalling the app **never deletes your recordings**:

- **App Binaries**: `%localappdata%\Programs\ScreenRecorder\`
- **Recordings & SQLite Database**: `C:\Users\<Username>\Videos\RecordingsV2\`
  - Video clips are saved as chronological 1-minute MP4 files.
  - Pinned segments are renamed with a `pinned_` prefix to save them from rolling deletion.
  - Search indexes are kept in `ocr_index.db`.
- **Configuration Store**: `%localappdata%\ScreenRecorder\settings.json`

---

##  How to Build from Source

### Prerequisites
- Windows 10 (Build 19041+) or Windows 11.
- Visual Studio (with C++ Desktop Development tools installed).
- .NET 10.0 SDK.
- Inno Setup 6 (to compile the installer package).

### Developer Builds
Run the developer build script in the root repository folder:
```powershell
.\build.bat
```
This compiles the native C++ encoding library `Encoder.dll` and builds the C# WPF application in Release mode under the `bin/Release` folder.

### Installer Compilation
To compile a standalone, self-contained installer (`ScreenRecorder_Setup.exe`):
```powershell
.\build_installer.bat
```
This publishes the C# project in self-contained mode (bundling all required .NET runtimes), links the statically compiled C++ DLL (removing VC++ runtime redistributable requirements), and compiles the LZMA2-compressed setup wizard inside the `installer/` directory.

---

## 📜 License

This project is open-source and free to modify or distribute.
