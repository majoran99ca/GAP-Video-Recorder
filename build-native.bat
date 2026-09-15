@echo off
setlocal
cd /d "%~dp0"

if not exist ".\src\GapVideoRecorder\assets\ffmpeg\ffmpeg.exe" (
  echo The FFmpeg encoder is missing. Download the portable release and copy
  echo ffmpeg.exe into src\GapVideoRecorder\assets\ffmpeg before building.
  exit /b 1
)

where dotnet >nul 2>nul || (
  echo .NET 8 SDK is required to build the native release.
  exit /b 1
)

dotnet publish ".\src\GapVideoRecorder\GapVideoRecorder.csproj" --configuration Release --runtime win-x64 --self-contained true --output ".\dist\GAP Video Recorder" || exit /b 1
copy /Y "support-report.bat" ".\dist\GAP Video Recorder\support-report.bat" >nul || exit /b 1
copy /Y "LICENSE" ".\dist\GAP Video Recorder\LICENSE.txt" >nul || exit /b 1

echo.
echo Native portable app created in "dist\GAP Video Recorder".
echo Copy that entire folder to a 64-bit Windows 10 or Windows 11 computer.
