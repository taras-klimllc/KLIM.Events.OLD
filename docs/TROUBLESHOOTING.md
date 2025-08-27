# KLIM.Events Docker Troubleshooting Guide

## Common Issues and Solutions

### 1. Docker Build Issues

#### Issue: "COPY failed: file not found"
```
ERROR [build 3/6] COPY src/KLIM.Events.Service/KLIM.Events.Service.csproj src/KLIM.Events.Service/
```

**Solution:**
```powershell
# Ensure you're running from the repository root directory
Get-Location  # Should show path ending with KLIM.Events
ls src/KLIM.Events.Service/  # Should show project files

# If in wrong directory:
cd path\to\KLIM.Events\repository
```

#### Issue: "Docker build fails with dependency errors"
**Solution:**
```powershell
# Clear Docker cache
docker builder prune -f
docker system prune -f

# Rebuild with verbose output
docker build -t klim/events-service:latest -f src/KLIM.Events.Service/Dockerfile . --progress=plain
```

### 2. RabbitMQ Connection Issues

#### Issue: "Connection refused to localhost:5672"
**Symptoms:**
- Service logs show RabbitMQ connection errors
- Health checks fail for RabbitMQ dependency

**Solutions:**

**Check RabbitMQ Container Status:**
```powershell
# List all containers with rabbitmq
docker ps -a | Select-String rabbitmq

# Check specific container (competent_burnell)
docker logs competent_burnell --tail=20
```

**Test Network Connectivity:**
```powershell
# Test from host
Test-NetConnection -ComputerName localhost -Port 5672

# Test from inside KLIM.Events container
docker exec klim-events-service curl -v telnet://host.docker.internal:5672
```

**Alternative Host Configurations:**
Try these in .env file in order:
```bash
# Option 1: Docker internal host (recommended)
RABBITMQ__HOST=host.docker.internal

# Option 2: Direct localhost (if using --network host)
RABBITMQ__HOST=localhost

# Option 3: Container IP (find with docker inspect)
RABBITMQ__HOST=172.17.0.2

# Option 4: Container name (requires shared network)
RABBITMQ__HOST=competent_burnell
```

#### Issue: "Exchange 'klim.events' not found"
**Solution:**
```powershell
# Check RabbitMQ Management UI
# Open: http://localhost:15672 (guest/guest)
# Navigate to Exchanges tab
# Manually create exchange if missing:
#   Name: klim.events
#   Type: topic
#   Durability: Durable
```

### 3. Database Connection Issues

#### Issue: "Login failed for user" or Azure AD authentication errors
**Symptoms:**
- Database health checks fail
- SQL connection errors in logs

**Solutions:**

**Check Connection String Format:**
```powershell
# View current environment variable
docker exec klim-events-service printenv DATABASE__CONNECTIONSTRING

# Correct format for Azure AD:
"Server=your-server.database.windows.net;Database=your-database;Encrypt=true;TrustServerCertificate=false;Connection Timeout=30;Authentication=Active Directory Default"
```

**Test Azure AD Authentication:**
```powershell
# Check if Azure CLI is logged in (on host)
az account show

# Check container can access Azure AD
docker exec klim-events-service env | Select-String -Pattern "AZURE"
```

**Connection String Troubleshooting:**
```bash
# .env file - ensure these settings
DATABASE__CONNECTIONSTRING=Server=klim-sql.2e340c2a848b.database.windows.net;Database=KLIM_IM_TK;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;Authentication=Active Directory Default
DATABASE__USEAZUREAD=true
```

### 4. Container Startup Issues

#### Issue: Container exits immediately
**Diagnosis:**
```powershell
# Check exit reason
docker logs klim-events-service

# Check resource constraints
docker stats klim-events-service

# Run container interactively for debugging
docker run -it --rm klim/events-service:latest /bin/bash
```

**Common Causes & Solutions:**

**Missing Environment Variables:**
```powershell
# Check required variables are set
docker exec klim-events-service printenv | Select-String -Pattern "DATABASE__\|RABBITMQ__"
```

**Configuration Validation Errors:**
```bash
# Check appsettings.json syntax
# Look for JSON formatting errors in configuration
```

**Permission Issues:**
```dockerfile
# Dockerfile already includes non-root user setup
USER appuser
# If issues persist, check file permissions in container
```

### 5. Health Check Issues

#### Issue: Health endpoint not responding
**Diagnosis:**
```powershell
# Test directly
curl http://localhost:8080/health

# Check if port is bound
netstat -an | Select-String ":8080"

# Check container port mapping
docker port klim-events-service
```

**Solutions:**

**Port Conflicts:**
```bash
# Change port in .env if needed
EVENTS_HEALTH_PORT=8081  # Use different port
```

**Internal vs External Health Checks:**
```powershell
# Test from inside container
docker exec klim-events-service curl http://localhost:8080/health

# Test from host
curl http://localhost:8080/health
```

### 6. Performance Issues

#### Issue: High memory usage or OOM kills
**Diagnosis:**
```powershell
# Monitor resource usage
docker stats klim-events-service

# Check memory limits
docker inspect klim-events-service | Select-String -Pattern "Memory"
```

**Solutions:**

**Increase Memory Limits:**
```yaml
# In docker-compose.prod.yml
deploy:
  resources:
    limits:
      memory: 512M  # Increase from 256M
    reservations:
      memory: 256M  # Increase from 128M
```

**Optimize Batch Sizes:**
```bash
# In .env file - reduce batch sizes
CHANGETRACKING__BATCHSIZE=50         # Reduce from 100
MASSTRANSIT__OUTBOX__BATCHSIZE=25    # Reduce from 50
```

### 7. Log Analysis

#### Viewing Service Logs
```powershell
# Recent logs
docker-compose -f docker-compose.prod.yml logs --tail=100

# Follow logs in real-time
docker-compose -f docker-compose.prod.yml logs -f

# Filter specific log levels
docker logs klim-events-service 2>&1 | Select-String -Pattern "ERROR\|WARN"

# Check structured logs in container
docker exec klim-events-service ls -la /app/logs/
```

#### Important Log Patterns
```
# Successful startup
"Starting KLIM.Events service"
"MassTransit bus started"
"Health check endpoint started on port 8080"

# Database connectivity
"Database health check: Healthy"
"Connected to SQL Server"

# RabbitMQ connectivity  
"Connected to RabbitMQ"
"Exchange 'klim.events' ready"

# Message processing
"PUBLISHED DataChangedV1"
"Processed batch of X change events"
```

### 8. Complete Reset Procedure

If all else fails, perform a complete reset:

```powershell
# 1. Stop and remove all containers
docker-compose -f docker-compose.prod.yml down -v

# 2. Remove images
docker rmi klim/events-service:latest

# 3. Clean Docker system
docker system prune -a -f

# 4. Rebuild from scratch
docker build -t klim/events-service:latest -f src/KLIM.Events.Service/Dockerfile .

# 5. Deploy fresh
docker-compose -f docker-compose.prod.yml up -d

# 6. Verify deployment
.\deploy.ps1 status
```

### 9. Diagnostic Commands Quick Reference

```powershell
# Service status
.\deploy.ps1 status

# Detailed container info
docker inspect klim-events-service

# Process list in container
docker exec klim-events-service ps aux

# Network connectivity
docker exec klim-events-service netstat -tuln

# Environment variables
docker exec klim-events-service printenv

# File system
docker exec klim-events-service ls -la /app/

# Test specific ports
Test-NetConnection -ComputerName localhost -Port 8080
Test-NetConnection -ComputerName localhost -Port 5672

# RabbitMQ management API
Invoke-RestMethod -Uri "http://localhost:15672/api/overview" -Headers @{Authorization="Basic Z3Vlc3Q6Z3Vlc3Q="}
```

### 10. Getting Help

If issues persist:

1. **Run verification script:**
   ```powershell
   .\verify-docker-setup.ps1 -Verbose
   ```

2. **Collect diagnostic info:**
   ```powershell
   # Create diagnostic report
   .\deploy.ps1 status > diagnostic-report.txt
   docker logs klim-events-service >> diagnostic-report.txt
   docker inspect klim-events-service >> diagnostic-report.txt
   ```

3. **Check configuration:**
   - Review .env file for typos
   - Validate connection strings
   - Confirm external dependencies (RabbitMQ, database) are accessible

4. **Test minimal setup:**
   - Start with default .env.template values
   - Verify external RabbitMQ is running
   - Test database connectivity independently