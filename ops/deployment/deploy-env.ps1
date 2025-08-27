#!/usr/bin/env pwsh
# =============================================================================
# KLIM.Events Multi-Environment Deployment Script
# =============================================================================
# Supports: Production, Development, Test
# Usage: .\deploy-env.ps1 [Environment] [Action] [Options]
# Examples:
#   .\deploy-env.ps1 dev up
#   .\deploy-env.ps1 prod deploy
#   .\deploy-env.ps1 test down

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("dev", "development", "prod", "production", "test")]
    [string]$Environment,
    
    [Parameter(Mandatory = $true, Position = 1)]  
    [ValidateSet("up", "down", "build", "deploy", "logs", "ps", "health")]
    [string]$Action,
    
    [Parameter(Position = 2)]
    [string]$Options = ""
)

# =============================================================================
# Configuration
# =============================================================================
$ErrorActionPreference = "Stop"

# Environment mappings
$EnvConfig = @{
    "dev" = @{
        ComposeFile = "docker-compose.dev.yml"
        EnvFile = ".env.dev"
        ServiceName = "klim-events-dev"
        HealthPort = 8081
        Description = "Development (Azure SQL + Local RabbitMQ + Azure AD)"
    }
    "development" = @{
        ComposeFile = "docker-compose.dev.yml"
        EnvFile = ".env.dev"
        ServiceName = "klim-events-dev"
        HealthPort = 8081
        Description = "Development (Azure SQL + Local RabbitMQ + Azure AD)"
    }
    "prod" = @{
        ComposeFile = "docker-compose.prod.yml"
        EnvFile = ".env"
        ServiceName = "klim-events-service"
        HealthPort = 8080
        Description = "Production (Azure SQL + External RabbitMQ + Azure AD)"
    }
    "production" = @{
        ComposeFile = "docker-compose.prod.yml"
        EnvFile = ".env"
        ServiceName = "klim-events-service"  
        HealthPort = 8080
        Description = "Production (Azure SQL + External RabbitMQ + Azure AD)"
    }
    "test" = @{
        ComposeFile = "docker-compose.test.yml"
        EnvFile = ".env.test"
        ServiceName = "klim-events-test"
        HealthPort = 8082
        Description = "Test (In-Memory SQL + Local RabbitMQ + No Auth)"
    }
}

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

function Test-EnvironmentFile {
    param([string]$EnvFile)
    
    if (-not (Test-Path $EnvFile)) {
        Write-Warning "Environment file '$EnvFile' not found!"
        
        $templateFile = "$EnvFile.template"
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
    return $true
}

function Test-DockerComposeFile {
    param([string]$ComposeFile)
    
    if (-not (Test-Path $ComposeFile)) {
        Write-Error "Docker Compose file '$ComposeFile' not found!"
        return $false
    }
    return $true
}

function Test-AzureLogin {
    if ($Environment -in @("dev", "development", "prod", "production")) {
        Write-Info "Checking Azure authentication..."
        try {
            $azAccount = az account show --query "user.name" -o tsv 2>$null
            if ($azAccount) {
                Write-Success "Azure authentication: $azAccount"
                return $true
            }
        } catch {
            # Ignore error, will handle below
        }
        
        Write-Warning "Azure authentication required for $Environment environment."
        Write-Info "Please run: az login"
        return $false
    }
    return $true
}

function Invoke-DockerCompose {
    param([string]$ComposeFile, [string]$EnvFile, [string]$Command)
    
    $composeArgs = @("-f", $ComposeFile, "--env-file", $EnvFile)
    $composeArgs += $Command.Split(" ")
    
    Write-Info "Running: docker-compose $($composeArgs -join ' ')"
    & docker-compose @composeArgs
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker Compose command failed with exit code $LASTEXITCODE"
        return $false
    }
    return $true
}

function Test-ServiceHealth {
    param([int]$Port, [string]$ServiceName)
    
    Write-Info "Testing health endpoint on port $Port..."
    
    for ($i = 1; $i -le 30; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:$Port/health/live" -Method GET -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Success "$ServiceName is healthy! (HTTP $($response.StatusCode))"
                return $true
            }
        } catch {
            if ($i -eq 30) {
                Write-Error "Health check failed after 30 attempts: $($_.Exception.Message)"
                return $false
            }
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

# =============================================================================
# Main Logic
# =============================================================================
Write-Header "KLIM.Events Multi-Environment Deployment"

# Get environment configuration
$config = $EnvConfig[$Environment]
Write-Info "Environment: $Environment"
Write-Info "Description: $($config.Description)"
Write-Info "Compose File: $($config.ComposeFile)"
Write-Info "Environment File: $($config.EnvFile)"

# Validate prerequisites
if (-not (Test-DockerComposeFile $config.ComposeFile)) { exit 1 }
if (-not (Test-EnvironmentFile $config.EnvFile)) { exit 1 }
if (-not (Test-AzureLogin)) { exit 1 }

# Execute action
switch ($Action) {
    "build" {
        Write-Header "Building $Environment Environment"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "build --no-cache"
    }
    
    "up" {
        Write-Header "Starting $Environment Environment"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "up -d"
        Write-Success "$Environment environment started!"
        
        # Test health
        Start-Sleep -Seconds 10
        Test-ServiceHealth $config.HealthPort $config.ServiceName
    }
    
    "deploy" {
        Write-Header "Deploying $Environment Environment"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "build --no-cache"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "up -d --force-recreate"
        Write-Success "$Environment environment deployed!"
        
        # Test health
        Start-Sleep -Seconds 10
        Test-ServiceHealth $config.HealthPort $config.ServiceName
    }
    
    "down" {
        Write-Header "Stopping $Environment Environment"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "down"
        Write-Success "$Environment environment stopped!"
    }
    
    "logs" {
        Write-Header "$Environment Environment Logs"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "logs --tail=50 -f"
    }
    
    "ps" {
        Write-Header "$Environment Environment Status"
        Invoke-DockerCompose $config.ComposeFile $config.EnvFile "ps"
    }
    
    "health" {
        Write-Header "$Environment Environment Health Check"
        Test-ServiceHealth $config.HealthPort $config.ServiceName
    }
}

Write-Success "Action '$Action' completed for '$Environment' environment!"

# =============================================================================
# Usage Examples
# =============================================================================
Write-Host ""
Write-Host "?? Usage Examples:" -ForegroundColor Cyan
Write-Host "  Development:  .\deploy-env.ps1 dev up" -ForegroundColor Gray
Write-Host "  Production:   .\deploy-env.ps1 prod deploy" -ForegroundColor Gray
Write-Host "  Test:         .\deploy-env.ps1 test up" -ForegroundColor Gray
Write-Host "  Health Check: .\deploy-env.ps1 dev health" -ForegroundColor Gray
Write-Host "  Logs:         .\deploy-env.ps1 dev logs" -ForegroundColor Gray
Write-Host "  Stop:         .\deploy-env.ps1 dev down" -ForegroundColor Gray
Write-Host ""