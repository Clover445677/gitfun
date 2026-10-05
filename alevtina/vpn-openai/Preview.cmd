@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-OpenAIRouting.ps1" -Action Preview
pause
