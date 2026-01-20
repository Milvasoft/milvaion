@echo off
REM Quick Docker build script for Milvaion API
REM Usage: build.bat [TAG] [--skip-push]

SET REGISTRY=milvasoft
SET TAG=%1
SET SKIP_PUSH=

IF "%TAG%"=="" SET TAG=latest
IF "%2"=="--skip-push" SET SKIP_PUSH=-SkipPush

echo ========================================
echo   Milvaion API - Docker Hub Build
echo ========================================
echo   Registry: %REGISTRY%
echo   Tag:      %TAG%
echo   Push:     %IF DEFINED SKIP_PUSH echo Disabled ELSE echo Enabled%
echo ========================================
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0build-api.ps1" -Registry "%REGISTRY%" -Tag "%TAG%" %SKIP_PUSH%

IF %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Build failed!
    pause
    exit /b 1
)

echo.
echo [SUCCESS] Build complete!
echo.
echo Image: %REGISTRY%/milvaion-api:%TAG%
echo.
IF NOT DEFINED SKIP_PUSH (
    echo Pull with: docker pull %REGISTRY%/milvaion-api:%TAG%
)
echo.
pause