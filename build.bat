@echo off
setlocal
cd /d "%~dp0"

if not exist ".venv\Scripts\python.exe" (
  py -m venv .venv || exit /b 1
)

".venv\Scripts\python.exe" -m pip install -r requirements.txt -r requirements-dev.txt || exit /b 1
".venv\Scripts\python.exe" -m PyInstaller --noconfirm --clean --windowed --onedir --name "GAP Video Recorder" --collect-all cv2 app.py || exit /b 1
copy /Y "support-report.bat" "dist\GAP Video Recorder\support-report.bat" >nul || exit /b 1

echo.
echo Portable app created in "dist\GAP Video Recorder".
echo Copy that whole folder to a work computer and run "GAP Video Recorder.exe".
