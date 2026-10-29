@echo off
set "APP=%~dp0artifacts\current-preview\PortableRecorder.Desktop.exe"
if not exist "%APP%" (
  echo Build the native preview first: powershell -File tools\build.ps1
  pause
  exit /b 1
)
start "" "%APP%"
