# GolfCamBridge

Use two FLIR / Teledyne **Blackfly S** USB3 cameras as swing cameras in **Foresight Premier** (or any DirectShow app).
GolfCamBridge reads both cameras through the Spinnaker SDK, converts the Bayer image to color on the PC, and publishes
them as two DirectShow virtual webcams: **"Golf Cam 1"** and **"Golf Cam 2"**.

```
Blackfly (FO)  ─┐                            ┌─> Golf Cam 1 ─┐
                ├─> GolfCamBridge.exe  ──────┤               ├─> Foresight Premier
Blackfly (DTL) ─┘   Bayer -> color, settings └─> Golf Cam 2 ─┘
```

## Why

Spinnaker ships its own DirectShow driver, but with it Premier gave us a mono or upside-down picture, a low frame rate,
and only one camera at a time. The bridge avoids all of that:

- Frames arrive as 8-bit Bayer and are debayered on the PC, so the color is correct and nothing is flipped.
- FPS, exposure, gain, ISP off, etc. are written to the camera from `golfcam.json` every time a camera opens, so they
  survive SDK reinstalls and camera resets.
- The virtual cameras come from the open-source [softcam](https://github.com/tshino/softcam) (MIT). softcam can only
  create one camera, so we build two variants with their own CLSID, device name and shared-memory name.
  The changes are in `softcam-multicam/changes.diff`.

### What we learned (tested with 2x BFS-U3-16S2C)

- **Premier only lists devices that offer RGB32 and a frame-rate range.** It queries RGB24 / fixed-FPS devices and then
  ignores them. The modified softcam offers RGB32 + RGB24 and advertises 1–1000 fps.
- **223 fps needs `IspEnable = false` and `PixelFormat = BayerRG8`.** RGB output from the camera caps it at about 76 fps.
  The ADC on this model only goes up to 10 bit.
- **Two cameras at full resolution exceed USB3 bandwidth.** With a 1280x720 ROI both run at 223 fps.
- The bridge (the sender) must be running before an app opens a Golf Cam, otherwise softcam has no format to offer.
- **Upstream softcam cannot restart its sender while an app still holds the camera**: the shared memory name still
  exists, so `scCreateCamera` fails until every app lets go, and apps that held the camera stay on a dark
  placeholder. This happens after a bridge restart or Reload while Premier (or Chrome, Slack, …) has a Golf Cam
  open. The modified softcam takes over the
  leftover memory when no other sender is alive (guarded by a sender-only named object), and apps that held the
  camera reconnect by themselves.
- If the Spinnaker DirectShow filter ("PtGrey Camera") is registered, it grabs camera #0. Unregister it (see below).

## Status

Work in progress. The capture pipeline (223 fps, two cameras, Premier connection) works. The tray app and the
settings window build, but have not yet been tested on the camera hardware.

## Requirements

- Windows 10/11 x64
- Spinnaker SDK 4.x (tested with 4.4.0.246). The app is **.NET Framework 4.8 x64** because `SpinnakerNET_v140`
  is a .NET Framework assembly.
- Visual Studio 2022 or 2026 (or Build Tools) with the **"Desktop development with C++"** workload.
  The softcam projects target v143; `build-softcam.ps1` uses v143 if it is installed, otherwise the newest installed
  toolset (e.g. v145 on VS 2026). Force one with `.\build-softcam.ps1 -Toolset v145`.
- .NET SDK 8 or newer (only to build)
- git (`build-softcam.ps1` clones softcam)

## Install (once)

Open PowerShell in this folder. If scripts are blocked, first run `Set-ExecutionPolicy -Scope Process Bypass`
(affects this PowerShell window only) and, for downloaded files, `Unblock-File *.ps1`.

```powershell
.\build-softcam.ps1      # -> bin\softcam_golfcam1.dll, bin\softcam_golfcam2.dll   (-Log: DirectShow call log in C:\Users\Public\Golf Cam N.log)
.\register-cams.ps1      # registers both virtual cameras (asks for admin / UAC)
.\build-app.ps1          # -> app\GolfCamBridge.exe
```

To work on the app in Visual Studio, open `GolfCamBridge.slnx` (C# app only — the softcam DLLs are always built by
`build-softcam.ps1`). F5 builds into `app\` just like `build-app.ps1`.

> Registration stores the full DLL path, so do not move the `bin` folder afterwards. To move it:
> `.\register-cams.ps1 -Uninstall`, move, register again.

> Chrome, Slack and other apps that enumerate webcams load the Golf Cam DLLs and keep them locked. Close them before
> rebuilding or unregistering. `tasklist /m softcam_golfcam1.dll` shows who has it loaded.

**Unregister the Spinnaker DirectShow filter.** Otherwise Premier can open the camera through Spinnaker and fight the
bridge for it. Run `DirectShowUnRegister_v140_x64.bat` (in the same Spinnaker folder as `DirectShowRegister...bat`)
as administrator.

## Configuration

1. Start the app and use the tray menu **Show connected cameras** to see each camera's serial number
   (or run `app\GolfCamBridge.exe list` in a terminal).
2. Edit **`app\golfcam.json`** (not the copy in the source folder) — or use the tray menu **Settings...** (below).

Per camera:

| Key | Default | Meaning |
|---|---|---|
| `Name` | | Label shown in the tray and log, e.g. `FO`, `DTL` |
| `Serial` | | Camera serial number. Keeps FO and DTL from swapping. Empty = use `Index` |
| `Index` | | Enumeration order, used only when `Serial` is empty |
| `VirtualCam` | 1 | 1 = Golf Cam 1, 2 = Golf Cam 2 |
| `FrameRate` | 223 | Requested acquisition frame rate |
| `ExposureUs` | 2000 | Exposure time in µs |
| `GainDb` | 24 | Gain in dB |
| `PixelFormat` | `BayerRG8` | Falls back to any Bayer*8, then Mono8 |
| `ReverseX` / `ReverseY` | false | Only for a camera mounted upside down |
| `Width` / `Height` | 1440 / 1080 | Centered ROI **and** the virtual camera's size. Required, multiples of 4. Default = full sensor of the BFS-U3-16S2C; use 1280 / 720 to run two cameras at 223 fps on one USB controller |

Global:

| Key | Default | Meaning |
|---|---|---|
| `Mode` | `OnDemand` | `AlwaysOn` keeps the cameras streaming all the time |
| `IdleReleaseSeconds` | 60 | OnDemand: release the cameras this long after the last app disconnected |
| `StandbyFps` | 10 | Frame rate of the standby image while a camera is off |
| `ColorAlgorithm` | `HQ_LINEAR` | Debayer algorithm. Use `NEAREST_NEIGHBOR` if the CPU cannot keep up |
| `SoftcamDir` | `..\bin` | Folder with the softcam DLLs, relative to the exe |

Rebuilding with `build-app.ps1` never overwrites `app\golfcam.json`; the default file is copied only if it is missing.

## Running (tray app)

Double-click `app\GolfCamBridge.exe`. A camera icon appears in the notification area; there is no console window.

- **Icon color:** grey = standby, green = streaming, red = error (camera not found, etc.)
- **Right-click menu**
  - Per-camera status: `standby`, `streaming 223 fps (in use)`, `camera not found`, …
  - **Keep cameras on (AlwaysOn)** — stream regardless of whether an app is connected.
  - **Start with Windows** — starts at logon (HKCU Run key, no admin needed).
  - **Settings...** — settings window, see below.
  - **Edit settings (golfcam.json) / Reload settings** — edit the file, then Reload applies it without restarting the app.
    Reload recreates the virtual cameras, so connected apps are disconnected.
  - **Show connected cameras** — model and serial number of every camera the SDK sees.
  - **Open log / Exit**

### Settings window

One tab per camera, with a small live preview and a brightness histogram for setting exposure, plus a "Common" tab.
Settings are grouped by what it takes to apply them:

| Group | Settings | How it applies |
|---|---|---|
| **Immediate** | Exposure, Gain; Mode, IdleReleaseSeconds, StandbyFps | Written to the running camera as you edit, no restart. Slider ranges are read from the camera. |
| **Camera restart** (yellow) | FrameRate, PixelFormat, ReverseX/Y; ColorAlgorithm | *Apply* button. Only the physical camera restarts (~1 s); the virtual camera stays, so Premier stays connected. |
| **Reload** (red) | Width, Height (ROI) | *Apply ROI* button. Recreates both virtual cameras — **Premier is disconnected**. |

- The camera tab shows the maximum frame rate possible at the current exposure, and warns in red when `FrameRate` is
  higher than that.
- The preview reuses frames the bridge already produces (downscaled, at most 8 fps, only while the window is open).
  It adds no extra capture.
- If a camera is off (OnDemand idle, unplugged), changes are kept and used the next time it starts.
- **Save** writes `golfcam.json`. **Revert** restores the values from when the window was opened, including values
  already written to the camera. Closing with unsaved changes asks whether to save or go back to the last saved values.

### OnDemand mode (default)

- The virtual cameras exist the whole time the app runs. While the real camera is off they show a "Golf Cam N" standby image.
- When an app such as Premier opens a Golf Cam, the real camera is started (about 1 s).
- `IdleReleaseSeconds` (default 60 s) after the last app disconnects, the camera is released, so SpinView or
  Swing Catalyst can use it while Premier is not in use.
- If a camera disconnects, or sends no frames for 3 s, the bridge retries every 3 s.

> If Premier reopens the camera for every shot and **the start of the first shot is cut off**, turn on
> **Keep cameras on** in the tray menu.

### Console and log

- `app\GolfCamBridge.exe --console` starts the tray app plus a live log window. Stop it with Ctrl+C (closing the window skips the clean shutdown).
- Log file: `%LOCALAPPDATA%\GolfCamBridge\bridge.log`
- `GolfCamBridge.exe --selftest` runs the settings-window logic checks (no camera needed, exit code 0 = OK).

### Virtual camera test tool (no camera needed)

`tools\reopen.cpp` opens a Golf Cam the way a capture app does, closes it and opens it again in one process —
like Premier leaving practice mode and coming back. The bridge must be running; without a physical camera it
checks against the standby image.

```powershell
.\tools\build-tools.ps1                    # -> build\tools\reopen.exe
.\build\tools\reopen.exe "Golf Cam 1"      # reopen patterns A-D, exit code 0 = all OK
.\build\tools\reopen.exe --restart "Golf Cam 1"   # exit + restart the bridge when told; held cameras must reconnect
```

A session only passes with live frames (mean pixel above 20). With a real camera in a dark room, set
`$env:REOPEN_MIN_MEAN = 0`.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Camera FPS lower than `FrameRate` | Settings window → "max FPS" / "camera actual", or `camera reports ... fps achievable` in the log. Causes: exposure too long (frame period at 223 fps ≈ 4.48 ms), ROI too large, or both cameras on the same USB controller. Put one camera on a different controller. |
| Camera FPS fine but output FPS low | The CPU cannot keep up with debayering. Use `NEAREST_NEIGHBOR` or a smaller ROI. |
| Frames stall and the camera keeps restarting | USB bandwidth or cable. Use separate ports and short, good-quality cables. |
| Golf Cam missing in Premier | Run `register-cams.ps1` again and restart Premier. Check that it appears in OBS → Video Capture Device. The bridge must be running. |
| `scCreateCamera failed` | The bridge is already running, or two cameras use the same `VirtualCam` number. |
| Wrong colors (green / purple tint) | Try another `PixelFormat`: BayerRG8, BayerGB8, BayerGR8 or BayerBG8. |
| Build error: `DataPtr` not found | The Spinnaker .NET API differs between SDK versions. Please open an issue with the error message. |

**About the frame rate:** what Premier actually records through DirectShow is up to Premier. The bridge sends 223 fps;
step through a recorded clip frame by frame to see the real rate. If you do not get what you expect, compare with
`FrameRate` 120 or 60.

## Uninstall

```powershell
.\register-cams.ps1 -Uninstall
```

Unregister **before** deleting the `bin` folder, otherwise apps keep listing "Golf Cam 1/2" devices that no longer work.
If you enabled **Start with Windows**, turn it off in the tray menu first.

## License

The softcam part is based on [tshino/softcam](https://github.com/tshino/softcam) (MIT License); see the upstream
repository for its license text.
