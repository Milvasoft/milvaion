@echo off
setlocal EnableDelayedExpansion

REM ============================================================================
REM Milvaion Build All - Docker Hub
REM Builds and pushes both Milvaion API and Worker Docker images.
REM
REM Usage:
REM   build-all.bat -Registry milvasoft -Tag 1.0.0
REM   build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipPush
REM   build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipWorker
REM ============================================================================

set "Registry="
set "Tag=latest"
set "SkipPush=0"
set "SkipApi=0"
set "SkipWorker=0"

REM Parse arguments
:parse_args
if "%~1"=="" goto :args_done
if /i "%~1"=="-Registry" (
    set "Registry=%~2"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="-Tag" (
    set "Tag=%~2"
    shift
    shift
    goto :parse_args
)
if /i "%~1"=="-SkipPush" (
    set "SkipPush=1"
    shift
    goto :parse_args
)
if /i "%~1"=="-SkipApi" (
    set "SkipApi=1"
    shift
    goto :parse_args
)
if /i "%~1"=="-SkipWorker" (
    set "SkipWorker=1"
    shift
    goto :parse_args
)
shift
goto :parse_args

:args_done

REM Validate required parameters
if "%Registry%"=="" (
    echo [ERROR] Registry parameter is required.
    echo.
    echo Usage: build-all.bat -Registry ^<registry^> [-Tag ^<tag^>] [-SkipPush] [-SkipApi] [-SkipWorker]
    echo.
    echo Example:
    echo   build-all.bat -Registry milvasoft -Tag 1.0.0
    echo   build-all.bat -Registry milvasoft -Tag 1.0.0 -SkipPush
    echo.
    pause
    exit /b 1
)

REM Display configuration
echo.
echo ===============================================================
echo              Milvaion Build All - Docker Hub
echo ===============================================================
echo   Registry: %Registry%
echo   Tag:      %Tag%
if %SkipApi%==1 (echo   API:      Skipped) else (echo   API:      Enabled)
if %SkipWorker%==1 (echo   Worker:   Skipped) else (echo   Worker:   Enabled)
if %SkipPush%==1 (echo   Push:     Disabled) else (echo   Push:     Enabled)
echo ===============================================================
echo.

set "ScriptDir=%~dp0"
set "TotalSteps=0"
set "CurrentStep=0"

if %SkipApi%==0 set /a TotalSteps+=1
if %SkipWorker%==0 set /a TotalSteps+=1

REM Build API
if %SkipApi%==0 (
    set /a CurrentStep+=1
    echo.
    echo [!CurrentStep!/%TotalSteps%] Building Milvaion API...
    echo.
    
    if %SkipPush%==1 (
        call "%ScriptDir%build-api.bat" -Registry %Registry% -Tag %Tag% -SkipPush
    ) else (
        call "%ScriptDir%build-api.bat" -Registry %Registry% -Tag %Tag%
    )
    
    if !ERRORLEVEL! neq 0 (
        echo [ERROR] API build failed!
        exit /b 1
    )
)

REM Build Worker
if %SkipWorker%==0 (
    set /a CurrentStep+=1
    echo.
    echo [!CurrentStep!/%TotalSteps%] Building Milvaion Worker...
    echo.
    
    if %SkipPush%==1 (
        call "%ScriptDir%build-worker.bat" -Registry %Registry% -Tag %Tag% -SkipPush
    ) else (
        call "%ScriptDir%build-worker.bat" -Registry %Registry% -Tag %Tag%
    )
    
    if !ERRORLEVEL! neq 0 (
        echo [ERROR] Worker build failed!
        exit /b 1
    )
)

REM Summary
echo.
echo ===============================================================
echo                  All Builds Complete!
echo ===============================================================
if %SkipApi%==0 echo   API:    %Registry%/milvaion-api:%Tag%
if %SkipWorker%==0 echo   Worker: %Registry%/milvaion-sampleworker:%Tag%
echo ===============================================================
echo.

if %SkipPush%==0 (
    echo Images available on Docker Hub:
    if %SkipApi%==0 echo   docker pull %Registry%/milvaion-api:%Tag%
    if %SkipWorker%==0 echo   docker pull %Registry%/milvaion-sampleworker:%Tag%
    echo.
)

echo Deploy with Docker Compose:
echo   docker-compose up -d
echo.

pause
exit /b 0
