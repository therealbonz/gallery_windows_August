# My-3D-Cube: Windows Live Desktop Wallpaper

An auto-updating 3D spinning cube live desktop wallpaper for Windows 10 & 11, powered by **.NET 8**, **WebView2 (Edge Chromium)**, and **Three.js WebGL**.

---

## Features

- **Behind-The-Icons Wallpaper**: Uses the native Windows Explorer `WorkerW` architecture to dock directly behind your desktop icons. Your desktop icons, selection boxes, and shortcuts stay 100% interactive.
- **Auto-Syncing Media**: Connects to the **My-3D-Cube** Rails API backend (`http://162.35.101.183:3000`) and automatically refreshes whenever new photos or short videos are uploaded.
- **Live Video Textures**: Uploaded videos play smoothly on the cube faces in loop with WebGL video textures.
- **System Tray Controller**:
  - 🔄 **Refresh Cube Media**: Forces immediate re-sync with the server.
  - 🌐 **Open Web Gallery**: Opens the web gallery in your default browser.
  - ⏯️ **Pause / Resume Rotation**: Pause the cube spin when desired.
  - ⚡ **Rotation Speed**: Choose Slow (0.4x), Normal (0.8x), or Fast (1.6x).
  - 🚀 **Start with Windows**: Enable/disable launching automatically when your PC boots.
  - ❌ **Exit Wallpaper**: Restores your original desktop wallpaper cleanly and exits.
- **Multi-Monitor Support**: Covers the full virtual desktop space across multiple displays.

---

## Quick Start (Run the Executable)

1. Open the `publish/` folder or download `My3DCubeWallpaper.exe`.
2. Double-click **`My3DCubeWallpaper.exe`**.
3. The 3D cube wallpaper will appear on your desktop behind your icons, and the 3D Cube icon will appear in your Windows System Tray (next to the clock).

---

## Development & Building

### Requirements
- Windows 10 or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer
- [Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (pre-installed on Windows 10/11)

### Build Debug
```bash
dotnet build My3DCubeWallpaper/My3DCubeWallpaper.csproj
```

### Publish Single-File Executable
```bash
dotnet publish My3DCubeWallpaper/My3DCubeWallpaper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## Project Structure
```
gallery_windows_August/
├── My3DCubeWallpaper/
│   ├── app.ico                    # Custom 32x32 3D isometric cube icon
│   ├── DesktopHook.cs             # Win32 WorkerW and Progman desktop hooking
│   ├── WallpaperForm.cs           # Borderless full-screen WebView2 form
│   ├── TrayApplicationContext.cs  # System tray NotifyIcon and context menu
│   ├── Program.cs                 # Mutex single-instance entrypoint
│   └── My3DCubeWallpaper.csproj   # .NET 8 WinForms project file
├── publish/
│   └── My3DCubeWallpaper.exe      # Compiled standalone executable
└── README.md
```
