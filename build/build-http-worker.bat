@echo off
REM Milvaion HTTP Worker Build - Windows Quick Launcher
REM Usage: build-http-worker.bat -Registry milvasoft -Tag 1.0.0

powershell -ExecutionPolicy Bypass -File "%~dp0build-http-worker.ps1" %*
