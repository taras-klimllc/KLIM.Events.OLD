@echo off
setlocal EnableDelayedExpansion

set ACTION=%1
if "%ACTION%"=="" set ACTION=deploy

echo [INFO] KLIM.Events Docker Deployment - Action: %ACTION%

REM Validate Docker
docker --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker not available or not running
    exit /b 1
)

docker-compose --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker Compose not available
    exit /b 1
)

goto %ACTION%

:build
echo [INFO] Building KLIM.Events Docker images...
docker build -t klim/events-service:latest -f src/KLIM.Events.Service/Dockerfile .
if errorlevel 1 (
    echo [ERROR] Docker build failed
    exit /b 1
)

echo [SUCCESS] Images built successfully
docker images klim/*
goto end

:deploy
if not exist ".env" (
    if exist ".env.template" (
        copy .env.template .env >nul
        echo [WARNING] Created .env from template. Please configure before continuing.
        echo [INFO] Edit .env file with your specific configuration values.
        pause
    ) else (
        echo [ERROR] .env.template not found. Cannot create environment configuration.
        exit /b 1
    )
)

call :build
if errorlevel 1 exit /b 1

echo [INFO] Starting services...
docker-compose -f docker-compose.prod.yml up -d
if errorlevel 1 (
    echo [ERROR] Failed to start services
    exit /b 1
)

echo [SUCCESS] Services deployed successfully
docker-compose -f docker-compose.prod.yml ps
goto end

:stop
echo [INFO] Stopping KLIM.Events services...
docker-compose -f docker-compose.prod.yml down
echo [SUCCESS] Services stopped
goto end

:restart
echo [INFO] Restarting KLIM.Events services...
docker-compose -f docker-compose.prod.yml restart
echo [SUCCESS] Services restarted
goto end

:logs  
docker-compose -f docker-compose.prod.yml logs --tail=50
goto end

:status
echo [INFO] Service Status:
docker-compose -f docker-compose.prod.yml ps
echo.
echo [INFO] Docker Images:
docker images klim/*
echo.
echo [INFO] Testing health check...
curl -f http://localhost:8080/health >nul 2>&1
if errorlevel 1 (
    echo [WARNING] Health check failed or service not responding
) else (
    echo [SUCCESS] Health check passed
)
goto end

:help
echo KLIM.Events Docker Deployment Script
echo.
echo Usage: deploy-simple.bat [action]
echo.
echo Actions:
echo   build    - Build Docker images only
echo   deploy   - Build and deploy services (default)
echo   stop     - Stop all services
echo   restart  - Restart all services
echo   logs     - Show recent logs
echo   status   - Show service status and health
echo   help     - Show this help message
echo.
echo Examples:
echo   deploy-simple.bat                 # Deploy services
echo   deploy-simple.bat build          # Build images only
echo   deploy-simple.bat status         # Check service status
echo   deploy-simple.bat logs           # View logs
echo.
echo Prerequisites:
echo - Docker 20.10+ with BuildKit support
echo - External RabbitMQ container running (competent_burnell)
echo - .env file configured (created from .env.template)
echo.
echo Service URLs:
echo - Health Check: http://localhost:8080/health
echo - RabbitMQ Management: http://localhost:15672 (guest/guest)
goto end

:unknown
echo [ERROR] Unknown action: %ACTION%
call :help
exit /b 1

:end