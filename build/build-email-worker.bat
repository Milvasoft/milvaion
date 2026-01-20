@echo off
REM Milvaion Email Worker Build - Windows Quick Launcher
REM Usage: build-email-worker.bat -Registry milvasoft -Tag 1.0.0

powershell -ExecutionPolicy Bypass -File "%~dp0build-email-worker.ps1" %*
