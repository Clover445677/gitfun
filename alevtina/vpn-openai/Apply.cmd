@echo off
echo Fully exit FlClashX using its tray menu before continuing.
pause
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-OpenAIRouting.ps1" -Action Apply
pause
