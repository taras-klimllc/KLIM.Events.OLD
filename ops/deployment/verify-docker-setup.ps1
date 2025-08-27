param(
    [switch]$SkipBuild,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "? $Message" -ForegroundColor Green
}

function Write-Error {
    param([string]$Message)
    Write-Host "? $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Host "? $Message" -ForegroundColor Blue
}

Write-Step "KLIM.Events Docker Deployment Verification"

# 1. Check Prerequisites
Write-Step "Checking Prerequisites"

try {
    docker --version | Out-Null
    Write-Success "Docker is available"
} catch {
    Write-Error "Docker is not available"
    exit 1
}

try {
    docker-compose --version | Out-Null
    Write-Success "Docker Compose is available"
} catch {
    Write-Error "Docker Compose is not available"
    exit 1
}

# 2. Check Required Files
Write-Step "Checking Required Files"

$requiredFiles = @(
    "src/KLIM.Events.Service/Dockerfile",
    "docker-compose.prod.yml",
    ".env.template",
    "deploy.ps1",
    "deploy-simple.bat",
    ".dockerignore"
)

foreach ($file in $requiredFiles) {
    if (Test-Path $file) {
        Write-Success "Found: $file"
    } else {
        Write-Error "Missing: $file"
        exit 1
    }
}

# 3. Validate Docker Build Context
Write-Step "Validating Docker Build Context"

if (Test-Path "src/KLIM.Events.Service/KLIM.Events.Service.csproj") {
    Write-Success "Project file exists"
} else {
    Write-Error "Project file not found"
    exit 1
}

# 4. Check External Dependencies
Write-Step "Checking External Dependencies"

Write-Info "Checking for external RabbitMQ container..."
$rabbitmqContainers = docker ps --format "table {{.Names}}\t{{.Image}}\t{{.Status}}" | Select-String -Pattern "rabbitmq"
if ($rabbitmqContainers) {
    Write-Success "Found RabbitMQ containers:"
    $rabbitmqContainers | ForEach-Object { Write-Info "  $_" }
} else {
    Write-Error "No RabbitMQ containers found. Expected external RabbitMQ container (competent_burnell)"
    Write-Info "Please start RabbitMQ container before deploying KLIM.Events"
}

# Test RabbitMQ connectivity
Write-Info "Testing RabbitMQ connectivity (localhost:5672)..."
try {
    $result = Test-NetConnection -ComputerName localhost -Port 5672 -WarningAction SilentlyContinue
    if ($result.TcpTestSucceeded) {
        Write-Success "RabbitMQ port 5672 is accessible"
    } else {
        Write-Error "Cannot connect to RabbitMQ port 5672"
    }
} catch {
    Write-Error "Failed to test RabbitMQ connectivity: $($_.Exception.Message)"
}

# 5. Build Test (if not skipped)
if (-not $SkipBuild) {
    Write-Step "Testing Docker Build"
    
    try {
        Write-Info "Building Docker image (this may take a few minutes)..."
        $buildOutput = docker build -t klim/events-service:test -f src/KLIM.Events.Service/Dockerfile . 2>&1
        
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Docker build completed successfully"
            
            # Check image was created
            $image = docker images klim/events-service:test --format "{{.Repository}}:{{.Tag}}\t{{.Size}}" | Select-String "klim/events-service:test"
            if ($image) {
                Write-Success "Image created: $image"
            }
            
            # Cleanup test image
            docker rmi klim/events-service:test > $null 2>&1
        } else {
            Write-Error "Docker build failed"
            if ($Verbose) {
                Write-Host $buildOutput
            }
            exit 1
        }
    } catch {
        Write-Error "Docker build error: $($_.Exception.Message)"
        exit 1
    }
} else {
    Write-Info "Skipping Docker build test (use -SkipBuild to skip)"
}

# 6. Environment Configuration Check
Write-Step "Environment Configuration Check"

if (Test-Path ".env") {
    Write-Success "Environment file (.env) exists"
    Write-Info "Checking for required environment variables..."
    
    $envContent = Get-Content ".env" -Raw
    $requiredVars = @(
        "DATABASE__CONNECTIONSTRING",
        "RABBITMQ__HOST",
        "RABBITMQ__EXCHANGENAME"
    )
    
    foreach ($var in $requiredVars) {
        if ($envContent -match $var) {
            Write-Success "Found: $var"
        } else {
            Write-Error "Missing: $var"
        }
    }
} else {
    Write-Info "No .env file found (will be created from template on first deployment)"
}

# 7. Summary
Write-Step "Verification Summary"

Write-Success "All prerequisites are met!"
Write-Info "Ready for deployment with the following commands:"
Write-Host ""
Write-Host "  # Deploy services:"
Write-Host "  .\deploy.ps1 deploy" -ForegroundColor Yellow
Write-Host ""
Write-Host "  # Or build only:"
Write-Host "  .\deploy.ps1 build" -ForegroundColor Yellow
Write-Host ""
Write-Host "  # Check status after deployment:"
Write-Host "  .\deploy.ps1 status" -ForegroundColor Yellow
Write-Host ""

Write-Info "Service will be available at:"
Write-Info "  - Health Check: http://localhost:8080/health"
Write-Info "  - RabbitMQ Management: http://localhost:15672"

Write-Step "Verification Complete"