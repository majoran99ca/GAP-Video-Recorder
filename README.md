# GAP Video Recorder

A native Windows app that combines connected cameras into one silent H.264 MP4 video.

## Start

### Portable app for work computers

You do **not** need Python, Qt, or .NET installed on the computers where the recorder will be used. Build the portable release once on a developer computer by double-clicking `build-native.bat`. It creates `dist\GAP Video Recorder`. Copy that entire folder to a 64-bit Windows 10 or Windows 11 computer and launch `GapVideoRecorder.exe`.

Windows may show a SmartScreen notice for an unsigned in-house app. Your IT team can distribute or code-sign the folder's executable. No installer, administrator permission, Python, or internet connection is needed on the target computer.

If the program fails to open, run `support-report.bat` from the same folder and send the generated `GAP Video Recorder support report.txt` with the error screenshot. It contains the Windows version and architecture needed to diagnose a deployment issue.

### Native source

The deployable application is the C# WPF project in `src\GapVideoRecorder`. It uses Windows DirectShow for camera capture and bundles FFmpeg with libx264 for H.264 MP4 output and reliable quality control. The release is published self-contained for `win-x64`, so its target computers do not need a runtime installed. The bundled FFmpeg license is included beside the executable.

The GitHub source intentionally omits `ffmpeg.exe` because it exceeds GitHub's normal repository file limit. To build a portable release from source, download the portable release ZIP first and copy its `ffmpeg.exe` into `src\GapVideoRecorder\assets\ffmpeg` before running the build script.

Run this on a development computer with the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0):

```powershell
.\build-native.bat
```

### Python prototype

For development, install [Python 3.10 or newer](https://www.python.org/downloads/windows/) on Windows, then double-click `run.bat`. The first launch creates a local virtual environment and installs the Python dependencies. Later launches open the recorder directly.

## Record

Drag a camera from the left panel onto the black canvas. Drag the camera to move it, or drag any blue corner handle to resize it. A camera can extend beyond the canvas edge when only part of its view should be recorded. Double-clicking a camera in the list also adds it. Select a camera and press Delete to remove it. Each camera can appear once. The visible camera area is center-cropped to its tile, with no stretching; uncovered canvas remains black.

When the app closes, it remembers the connected cameras in use and their positions and sizes. It restores that layout next time the same cameras are connected and Windows assigns them the same camera numbers.

Choose 1080p (1920×1080), 2K (2560×1440), or 4K (3840×2160), then press **Record**. Press **Stop** to finalize the MP4. Files are saved automatically in your Windows **Videos** folder. The output contains one H.264 video stream and no audio. There is no playback screen.

Use the five-position **Quality** slider to choose the trade-off between file size and image detail. **Balanced** is the default and should be far smaller than the previous release's unrestricted encoder output. **Smallest** is useful for long recordings; **High** and **Maximum** preserve more detail. Output size still changes with movement, lighting, camera count, and resolution.

The app records at 30 frames per second. Cameras are captured through Windows DirectShow; FFmpeg writes standard H.264 High Profile with 4:2:0 color for Windows player compatibility. If composing a frame takes longer than one frame interval, the app repeats the latest composition so the MP4 duration stays aligned with the recording timer. High resolutions need enough system performance to capture, compose, and encode every frame. Windows camera privacy settings must allow desktop apps to use your cameras. Some cameras or USB controllers cannot stream from multiple devices simultaneously.

## Development

```powershell
py -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe app.py
```

To produce the older Python prototype from the command line:

```powershell
.\build.bat
```

This project is licensed under GPL-3.0 (see `LICENSE`).
