@echo off
REM Milvaion Maintenance Worker Build - Windows Quick Launcher
REM Usage: build-maintenance-worker.bat -Registry milvasoft -Tag 1.0.0

powershell -ExecutionPolicy Bypass -File "%~dp0build-maintenance-worker.ps1" %*

echo.
echo Press any key to exit...
pause >nul
