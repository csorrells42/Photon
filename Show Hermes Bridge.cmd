@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Show-HermesBridge.ps1"
if errorlevel 1 pause
