@echo off
REM Milvaion SQL Worker Build - Windows Quick Launcher
REM Usage: build-sql-worker.bat -Registry milvasoft -Tag 1.0.0

powershell -ExecutionPolicy Bypass -File "%~dp0build-sql-worker.ps1" %*
