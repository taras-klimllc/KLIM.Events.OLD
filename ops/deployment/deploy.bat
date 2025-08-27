@echo off
REM =============================================================================
REM KLIM.Events Docker Deployment - Windows Batch Script
REM =============================================================================
REM Simple wrapper for PowerShell deployment script
REM Usage: deploy.bat [action]
REM Examples: deploy.bat deploy, deploy.bat health, deploy.bat logs

if "%1"=="" (
    echo Usage: deploy.bat [action]
    echo.
    echo Available actions:
    echo   build    - Build Docker image
    echo   up       - Start service
    echo   deploy   - Build and start service ^(recommended^)
    echo   down     - Stop service
    echo   restart  - Restart service
    echo   logs     - View logs
    echo   ps       - Check status
    echo   health   - Health check
    echo.
    echo Examples:
    echo   deploy.bat deploy
    echo   deploy.bat health
    echo   deploy.bat logs
    exit /b 1
)

REM Check if PowerShell is available
powershell -Command "Write-Host 'PowerShell available'" >nul 2>&1
if errorlevel 1 (
    echo Error: PowerShell is required but not found.
    echo Please install PowerShell 7+ or use PowerShell directly:
    echo   .\deploy.ps1 %1
    exit /b 1
)

REM Execute PowerShell script
powershell -ExecutionPolicy Bypass -File ".\deploy.ps1" %*