@echo off
setlocal EnableDelayedExpansion

REM ============================================================================
REM Milvaion Sample Worker Build Script
REM Builds and pushes Milvaion Sample Worker Docker image.
REM
REM Usage:
REM   build-worker.bat -Registry milvasoft -Tag 1.0.0
REM   build-worker.bat -Registry ghcr.io/milvasoft -Tag latest -SkipPush
REM ============================================================================

set "Registry="
set "Tag=latest"
set "SkipPush=0"

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
shift
goto :parse_args

:args_done

REM Validate required parameters
if "%Registry%"=="" (
    echo [ERROR] Registry parameter is required.
    echo.
    echo Usage: build-worker.bat -Registry ^<registry^> [-Tag ^<tag^>] [-SkipPush]
    echo.
    echo Example:
    echo   build-worker.bat -Registry milvasoft -Tag 1.0.0
    echo.
    pause
    exit /b 1
)

REM Paths
set "ScriptDir=%~dp0"
for %%i in ("%ScriptDir%..") do set "RootDir=%%~fi"
set "Dockerfile=%RootDir%\src\Workers\SampleWorker\Dockerfile"

REM Display configuration
echo.
echo ===============================================================
echo           Milvaion Sample Worker Build Script
echo ===============================================================
echo   Registry: %Registry%
echo   Tag:      %Tag%
if %SkipPush%==1 (echo   Push:     Disabled) else (echo   Push:     Enabled)
echo ===============================================================
echo.

set "ImageName=%Registry%/milvaion-sampleworker:%Tag%"

REM Build Docker Image
echo [BUILD] Building Docker image...
echo [INFO] Building %ImageName%...

pushd "%RootDir%"
docker build -t %ImageName% -f "%Dockerfile%" .

if !ERRORLEVEL! neq 0 (
    echo [ERROR] Docker build failed
    popd
    exit /b 1
)
popd
echo [SUCCESS] Image built successfully
echo.

REM Tag as latest
if /i not "%Tag%"=="latest" (
    echo [TAG] Tagging image as 'latest'...
    docker tag %ImageName% "%Registry%/milvaion-sampleworker:latest"
    echo [SUCCESS] Image tagged as 'latest'
    echo.
)

REM Push to Registry
if %SkipPush%==0 (
    echo [PUSH] Pushing image to registry...
    
    echo [INFO] Pushing %ImageName%...
    docker push %ImageName%
    
    if !ERRORLEVEL! neq 0 (
        echo [ERROR] Docker push failed
        exit /b 1
    )
    
    if /i not "%Tag%"=="latest" (
        echo [INFO] Pushing %Registry%/milvaion-sampleworker:latest...
        docker push "%Registry%/milvaion-sampleworker:latest"
        
        if !ERRORLEVEL! neq 0 (
            echo [ERROR] Docker push ^(latest^) failed
            exit /b 1
        )
    )
    
    echo [SUCCESS] Image pushed to registry
) else (
    echo [INFO] Push skipped
)

echo.
echo ===============================================================
echo   Worker Image: %ImageName%
echo ===============================================================
echo.

pause
exit /b 0
