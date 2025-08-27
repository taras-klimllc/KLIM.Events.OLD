#!/usr/bin/env pwsh
# =============================================================================
# KLIM.Events Unified Docker Deployment Script
# =============================================================================
# Single deployment script for all environments using docker-compose.yml
# Usage: .\deploy.ps1 [Action] [Options]
# Examples:
#   .\deploy.ps1 up
#   .\deploy.ps1 build
#   .\deploy.ps1 health
#   .\deploy.ps1 logs

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("up", "down", "build", "deploy", "logs", "ps", "health", "restart")]
    [string]$Action,
    
    [Parameter(Position = 1)]
    [string]$Options = ""
)

# =============================================================================
# Configuration
# =============================================================================
$ErrorActionPreference = "Stop"

$ComposeFile = "docker-compose.yml"
$EnvFile = ".env"
$ServiceName = "klim-events-service"
$HealthPort = 8080

# =============================================================================
# Functions
# =============================================================================
function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "???????????????????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor White
    Write-Host "???????????????????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Info {
    param([string]$Message)
    Write-Host "?? $Message" -ForegroundColor Blue
}

function Write-Success {
    param([string]$Message)
    Write-Host "? $Message" -ForegroundColor Green
}

function Write-Warning {
    param([string]$Message)
    Write-Host "?? $Message" -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Message)
    Write-Host "? $Message" -ForegroundColor Red
}

function Test-Prerequisites {
    # Check Docker
    try {
        docker --version | Out-Null
        Write-Success "Docker is available"
    } catch {
        Write-Error "Docker is not available. Please install Docker Desktop."
        return $false
    }

    # Check Docker Compose
    try {
        docker-compose --version | Out-Null
        Write-Success "Docker Compose is available"
    } catch {
        Write-Error "Docker Compose is not available."
        return $false
    }

    # Check compose file
    if (-not (Test-Path $ComposeFile)) {
        Write-Error "Docker Compose file '$ComposeFile' not found!"
        return $false
    }

    # Check environment file
    if (-not (Test-Path $EnvFile)) {
        Write-Warning "Environment file '$EnvFile' not found!"
        
        $templateFile = ".env.template"
        if (Test-Path $templateFile) {
            Write-Info "Template file '$templateFile' found. Creating '$EnvFile'..."
            Copy-Item $templateFile $EnvFile
            Write-Success "Created '$EnvFile' from template."
            Write-Info "Please review and update the configuration in '$EnvFile' before continuing."
            return $false
        } else {
            Write-Error "No template file found at '$templateFile'."
            return $false
        }
    }

    # Check Azure authentication (if using Azure AD)
    $useAzureAd = Get-Content $EnvFile | Select-String "DATABASE__USEAZUREAD=true"
    if ($useAzureAd) {
        Write-Info "Checking Azure authentication..."
        try {
            $azAccount = az account show --query "user.name" -o tsv 2>$null
            if ($azAccount) {
                Write-Success "Azure authentication: $azAccount"
            }
        } catch {
            Write-Warning "Azure authentication may be required. Please run: az login"
        }
    }

    return $true
}

function Invoke-DockerCompose {
    param([string]$Command)
    
    Write-Info "Running: docker-compose -f $ComposeFile --env-file $EnvFile $Command"
    
    $process = Start-Process -FilePath "docker-compose" -ArgumentList "-f", $ComposeFile, "--env-file", $EnvFile, $Command.Split(" ") -Wait -PassThru -NoNewWindow
    
    if ($process.ExitCode -ne 0) {
        Write-Error "Docker Compose command failed with exit code $($process.ExitCode)"
        return $false
    }
    return $true
}

function Test-ServiceHealth {
    Write-Info "Testing health endpoint on port $HealthPort..."
    
    for ($i = 1; $i -le 30; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:$HealthPort/health/live" -Method GET -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Success "$ServiceName is healthy! (HTTP $($response.StatusCode))"
                Write-Info "Content: $($response.Content)"
                
                # Also test readiness
                try {
                    $readyResponse = Invoke-WebRequest -Uri "http://localhost:$HealthPort/health/ready" -Method GET -TimeoutSec 5
                    Write-Success "Service is ready! (HTTP $($readyResponse.StatusCode))"
                } catch {
                    Write-Warning "Service is live but not ready: $($_.Exception.Message)"
                }
                return $true
            }
        } catch {
            if ($i -eq 30) {
                Write-Error "Health check failed after 30 attempts: $($_.Exception.Message)"
                return $false
            }
            Write-Host "." -NoNewline
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

function Show-ServiceInfo {
    Write-Info "Service Information:"
    Write-Host "  Health Endpoint: http://localhost:$HealthPort/health/live" -ForegroundColor Gray
    Write-Host "  Readiness Endpoint: http://localhost:$HealthPort/health/ready" -ForegroundColor Gray
    Write-Host "  RabbitMQ Management: http://localhost:15672 (guest/guest)" -ForegroundColor Gray
    Write-Host "  Logs Directory: ./logs" -ForegroundColor Gray
}

# =============================================================================
# Main Logic
# =============================================================================
Write-Header "KLIM.Events Docker Deployment"

Write-Info "Action: $Action"
Write-Info "Compose File: $ComposeFile"
Write-Info "Environment File: $EnvFile"

# Validate prerequisites
if (-not (Test-Prerequisites)) { 
    Write-Error "Prerequisites check failed. Please resolve the issues above."
    exit 1 
}

# Execute action
switch ($Action) {
    "build" {
        Write-Header "Building KLIM.Events Service"
        if (Invoke-DockerCompose "build --no-cache") {
            Write-Success "Build completed successfully!"
        }
    }
    
    "up" {
        Write-Header "Starting KLIM.Events Service"
        if (Invoke-DockerCompose "up -d") {
            Write-Success "Service started successfully!"
            Show-ServiceInfo
            
            # Wait a moment then test health
            Start-Sleep -Seconds 10
            Test-ServiceHealth
        }
    }
    
    "deploy" {
        Write-Header "Deploying KLIM.Events Service"
        if ((Invoke-DockerCompose "build --no-cache") -and (Invoke-DockerCompose "up -d --force-recreate")) {
            Write-Success "Service deployed successfully!"
            Show-ServiceInfo
            
            # Wait a moment then test health
            Start-Sleep -Seconds 10
            Test-ServiceHealth
        }
    }
    
    "down" {
        Write-Header "Stopping KLIM.Events Service"
        if (Invoke-DockerCompose "down") {
            Write-Success "Service stopped successfully!"
        }
    }
    
    "restart" {
        Write-Header "Restarting KLIM.Events Service"
        if (Invoke-DockerCompose "restart") {
            Write-Success "Service restarted successfully!"
            Show-ServiceInfo
            
            # Wait a moment then test health
            Start-Sleep -Seconds 10
            Test-ServiceHealth
        }
    }
    
    "logs" {
        Write-Header "KLIM.Events Service Logs"
        Invoke-DockerCompose "logs --tail=50 -f"
    }
    
    "ps" {
        Write-Header "KLIM.Events Service Status"
        Invoke-DockerCompose "ps"
        Show-ServiceInfo
    }
    
    "health" {
        Write-Header "KLIM.Events Service Health Check"
        if (Test-ServiceHealth) {
            Show-ServiceInfo
        }
    }
}

Write-Success "Action '$Action' completed!"

# =============================================================================
# Usage Examples
# =============================================================================
Write-Host ""
Write-Host "?? Usage Examples:" -ForegroundColor Cyan
Write-Host "  Build:        .\deploy.ps1 build" -ForegroundColor Gray
Write-Host "  Start:        .\deploy.ps1 up" -ForegroundColor Gray
Write-Host "  Deploy:       .\deploy.ps1 deploy" -ForegroundColor Gray
Write-Host "  Stop:         .\deploy.ps1 down" -ForegroundColor Gray
Write-Host "  Restart:      .\deploy.ps1 restart" -ForegroundColor Gray
Write-Host "  Logs:         .\deploy.ps1 logs" -ForegroundColor Gray
Write-Host "  Status:       .\deploy.ps1 ps" -ForegroundColor Gray
Write-Host "  Health:       .\deploy.ps1 health" -ForegroundColor Gray
Write-Host ""