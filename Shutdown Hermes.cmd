@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Shutdown-Hermes.ps1"
if errorlevel 1 pause
