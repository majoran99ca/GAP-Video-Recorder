@echo off
setlocal
set "report=%~dp0GAP Video Recorder support report.txt"
(
  echo GAP Video Recorder support report
  echo Generated: %DATE% %TIME%
  echo.
  ver
  echo.
  systeminfo | findstr /B /C:"OS Name" /C:"OS Version" /C:"System Type"
  echo.
  echo Application folder:
  dir /B "%~dp0"
) > "%report%"
start "" notepad.exe "%report%"
