@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Hermes.ps1"
if errorlevel 1 pause
