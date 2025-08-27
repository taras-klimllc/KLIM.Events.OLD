# KLIM.Events Development Guide

A comprehensive guide for developing and testing the KLIM.Events service locally using Docker with Azure AD authentication.

## ?? Why Development Uses Production Database?

The development environment connects to the **same Azure SQL Database as production** for several important reasons:

### **? Benefits of Shared Database Approach**

1. **Realistic Data Testing**
   - Test against actual business entities (Issuers, Deals)
   - Validate projectors with real schema complexity
   - Debug with production-equivalent data volumes

2. **Schema Consistency**
   - Same Change Tracking configuration as production
   - Identical table structures and relationships
   - Real temporal table support

3. **Authentication Testing**
   - Validate Azure AD integration in development
   - Test DefaultAzureCredential chain behavior
   - Debug token acquisition and refresh

4. **Isolation Through Messaging**
   - Uses separate RabbitMQ exchange (`klim.events.dev`)
   - No interference with production message flows
   - Safe parallel development

### **?? Safety Measures**

- **Read-Only Operations**: Service only reads from database via Change Tracking
- **Separate Exchange**: `klim.events.dev` vs `klim.events` (production)
- **Different Ports**: Development health endpoint on 8081 vs production 8080
- **Local RabbitMQ**: Isolated message broker for development

## ?? Development Setup

### **Prerequisites**

1. **Docker Desktop** - Latest version with Docker Compose
2. **Azure CLI** - For authentication: `az login`
3. **PowerShell 7+** - For deployment scripts
4. **Azure Account** - With access to KLIM_IM_TK database

### **First-Time Setup**

```powershell
# 1. Clone and navigate to repository
cd C:\Users\your-username\source\repos\KLIM.Events

# 2. Create development environment file
Copy-Item .env.dev.template .env.dev

# 3. Review and customize .env.dev (usually defaults are fine)
notepad .env.dev

# 4. Authenticate with Azure
az login

# 5. Start development environment
.\deploy-env.ps1 dev up
```

### **Development Environment Architecture**

```
???????????????????????????????????????????????????????????????????????
?                     Development Environment                         ?
???????????????????????????????????????????????????????????????????????
?                                                                     ?
?  ???????????????????         ???????????????????                   ?
?  ?   Azure SQL     ?   AD    ? KLIM Events Dev ?                   ?
?  ?   Database      ???????????    Service      ?                   ?
?  ? klim-sql.2e...  ?  Auth   ?                 ?                   ?
?  ?                 ?         ? Port: 8081      ?                   ?
?  ? KLIM_IM_TK      ?         ? Exchange: .dev  ?                   ?
?  ? (Production DB) ?         ???????????????????                   ?
?  ???????????????????                   ?                           ?
?         Shared                          ? Local                     ?
?                                         ?                           ?
?                               ???????????????????                   ?
?                               ?  RabbitMQ Dev   ?                   ?
?                               ?                 ?                   ?
?                               ? Port: 5673      ?                   ?
?                               ? UI: 15673       ?                   ?
?                               ? Creds: dev/dev  ?                   ?
?                               ???????????????????                   ?
?                                     Local                           ?
???????????????????????????????????????????????????????????????????????
```

### **Configuration Details**

#### **Environment Variables (.env.dev)**

```bash
# Database - Same as Production (Azure AD Auth)
DATABASE__CONNECTIONSTRING=Server=klim-sql.2e340c2a848b.database.windows.net;Database=KLIM_IM_TK;...
DATABASE__USEAZUREAD=true

# RabbitMQ - Local Development Container
RABBITMQ__HOST=rabbitmq-dev
RABBITMQ__USERNAME=dev
RABBITMQ__PASSWORD=dev123
RABBITMQ__EXCHANGENAME=klim.events.dev

# Development Optimizations
CHANGETRACKING__POLLINGINTERVALSECONDS=10  # Slower for debugging
CHANGETRACKING__BATCHSIZE=50               # Smaller batches
SERILOG__MINIMUMLEVEL__DEFAULT=Debug       # Verbose logging
```

#### **Azure AD Authentication**

The service uses Azure AD authentication for the database connection. Authentication happens automatically through the **DefaultAzureCredential** chain:

1. **Environment Variables** (if set)
2. **Managed Identity** (if running on Azure)
3. **Visual Studio** (if logged in)
4. **Azure CLI** (if `az login` executed) ? **Most common for development**
5. **Interactive Browser** (fallback)

## ?? Development Workflow

### **Daily Development Commands**

```powershell
# Start your development session
.\deploy-env.ps1 dev up

# Check everything is healthy
.\deploy-env.ps1 dev health
# Expected: ? Service is healthy (Live: 200, Ready: 200)

# Monitor logs while developing
.\deploy-env.ps1 dev logs

# Make code changes, then rebuild and restart
.\deploy-env.ps1 dev deploy

# Stop when done
.\deploy-env.ps1 dev down
```

### **Development URLs**

| Service | URL | Credentials |
|---------|-----|-------------|
| **Health Check** | http://localhost:8081/health/live | None |
| **Readiness Check** | http://localhost:8081/health/ready | None |
| **RabbitMQ Management** | http://localhost:15673 | dev/dev123 |

### **Monitoring Development Activity**

#### **PowerShell Health Monitoring**

```powershell
# Quick health check
function Test-DevHealth {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:8081/health/live" -TimeoutSec 5
        Write-Host "? Dev service healthy: $($response.Content)" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "? Dev service unhealthy: $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }
}

# Use it
Test-DevHealth
```

#### **Log Analysis**

```powershell
# Monitor change events being generated
Select-String -Path "logs\outbox-*.log" -Pattern "PUBLISHED.*Issuer" -CaseSensitive:$false

# Check payload sizes (should show 2KB+)
Select-String -Path "logs\outbox-*.log" -Pattern "Payload size:"

# Monitor Azure AD token acquisition
Select-String -Path "logs\outbox-*.log" -Pattern "Azure|DefaultAzureCredential|token"

# Watch for any errors
Select-String -Path "logs\outbox-*.log" -Pattern "error|exception|fail" -CaseSensitive:$false
```

#### **Database Activity Monitoring**

```powershell
# Check if change tracking is detecting changes
Select-String -Path "logs\outbox-*.log" -Pattern "change.*detected|inserted.*outbox"

# Monitor polling activity
Select-String -Path "logs\outbox-*.log" -Pattern "ChangeTracking poller"

# Verify projector activity
Select-String -Path "logs\outbox-*.log" -Pattern "IssuerProjector|DealProjector"
```

## ?? Testing Your Changes

### **Manual Testing Process**

1. **Start Development Environment**
   ```powershell
   .\deploy-env.ps1 dev up
   ```

2. **Verify Service Health**
   ```powershell
   Test-DevHealth  # Should return ? healthy
   ```

3. **Monitor RabbitMQ**
   - Open http://localhost:15673
   - Login with `dev`/`dev123`
   - Go to "Exchanges" ? Find `klim.events.dev`
   - Monitor message flow

4. **Trigger Database Changes** (if you have database access)
   ```sql
   -- Example: Update an issuer to trigger change event
   UPDATE dbo.Issuers 
   SET IssuerDesc = 'Updated Description ' + CAST(GETDATE() as varchar(50))
   WHERE IssuerID = 1
   ```

5. **Verify Message Publishing**
   ```powershell
   # Check recent logs for published messages
   .\deploy-env.ps1 dev logs | Select-String "PUBLISHED"
   ```

### **Automated Testing with Test Environment**

```powershell
# Start isolated test environment
.\deploy-env.ps1 test up

# Run your integration tests against http://localhost:8082

# Clean up
.\deploy-env.ps1 test down
```

## ?? Troubleshooting Development Issues

### **Common Issues and Solutions**

#### **1. Azure AD Authentication Failed**

**Symptoms:**
- Service fails to start
- Logs show authentication errors
- Health checks return 503

**Solutions:**
```powershell
# Check Azure login status
az account show

# Re-authenticate if needed
az login

# Verify you have access to the database
sqlcmd -S klim-sql.2e340c2a848b.database.windows.net -d KLIM_IM_TK -G
```

#### **2. RabbitMQ Connection Issues**

**Symptoms:**
- Service starts but readiness check fails
- Logs show RabbitMQ connection errors

**Solutions:**
```powershell
# Check if RabbitMQ container is running
docker ps | Select-String "rabbitmq"

# Restart development environment
.\deploy-env.ps1 dev down
.\deploy-env.ps1 dev up
```

#### **3. Port Conflicts**

**Symptoms:**
- Cannot start development environment
- Port already in use errors

**Solutions:**
```powershell
# Check what's using development ports
netstat -an | Select-String "8081|5673|15673"

# Stop conflicting services or change ports in .env.dev
```

#### **4. Empty Payloads (0KB Messages)**

**Symptoms:**
- Messages published but with no entity data
- Logs show 0KB payload sizes

**Investigation:**
```powershell
# Check if change tracking is working
Select-String -Path "logs\outbox-*.log" -Pattern "column.*mask|DecodeChangedColumns"

# Verify projector activity
Select-String -Path "logs\outbox-*.log" -Pattern "PreImage.*PostImage"
```

### **Advanced Debugging**

#### **Docker Container Inspection**

```powershell
# Get detailed info about dev service container
docker inspect klim-events-service-dev

# Check resource usage
docker stats klim-events-service-dev

# Access container shell for debugging
docker exec -it klim-events-service-dev /bin/bash
```

#### **Network Debugging**

```powershell
# Test database connectivity from container
docker exec klim-events-service-dev curl -f http://localhost:8080/health/ready

# Test RabbitMQ connectivity
docker exec klim-events-service-dev nc -zv rabbitmq-dev 5672
```

## ?? Development Checklist

### **Before Starting Development**

- [ ] Azure CLI installed and authenticated (`az login`)
- [ ] Docker Desktop running
- [ ] `.env.dev` file configured
- [ ] PowerShell 7+ available

### **Before Committing Changes**

- [ ] Development environment starts successfully
- [ ] Health checks pass (both live and ready)
- [ ] Messages are being published to `klim.events.dev` exchange
- [ ] No errors in logs
- [ ] Test environment passes integration tests

### **Before Deploying to Production**

- [ ] All development tests pass
- [ ] Code changes peer reviewed
- [ ] Production environment configuration validated
- [ ] Production health checks verified

## ?? Performance Expectations

### **Development Environment Performance**

| Metric | Expected Value | Notes |
|--------|---------------|--------|
| **Startup Time** | 30-60 seconds | Including Azure AD auth |
| **Health Check Response** | < 200ms | Both live and ready |
| **Message Payload Size** | 2KB+ | Rich entity data |
| **Processing Time** | 1-3 seconds | Per change event |
| **Memory Usage** | 128-256MB | Development optimized |

### **When to Be Concerned**

- ?? **Startup > 2 minutes**: Likely Azure AD auth issues
- ?? **Payload < 1KB**: Column mask decoding problems
- ?? **Memory > 512MB**: Memory leak or configuration issue
- ?? **Health checks fail**: Database or RabbitMQ connectivity

## ?? Next Steps

1. **Set up your development environment** using this guide
2. **Make your code changes** with confidence
3. **Test locally** using the development environment
4. **Validate with test environment** before deployment
5. **Deploy to production** using `.\deploy-env.ps1 prod deploy`

Happy developing! ??