# KLIM.Events - Docker Deployment Simplification

## ?? Summary of Changes

This document summarizes the consolidation of KLIM.Events Docker deployment from multiple environment-specific files to a single, unified deployment approach.

## ?? What Was Changed

### **Before: Multi-Environment Complexity**
```
docker-compose.dev.yml     ? Development environment
docker-compose.prod.yml    ? Production environment  
docker-compose.test.yml    ? Test environment
.env.dev.template         ? Development config template
.env.template             ? Production config template
deploy-env.ps1            ? Multi-environment deployment script
DEVELOPMENT.md            ? Development-specific guide
ENVIRONMENTS.md           ? Environment comparison guide
```

### **After: Unified Simplicity**
```
docker-compose.yml        ? Single deployment file
.env.template            ? Unified config template
deploy.ps1               ? Simple deployment script  
deploy.bat               ? Windows batch wrapper
README.md                ? Complete deployment guide
```

## ? Benefits of Consolidation

### **1. Simplified Deployment**
- **Single Command**: `.\deploy.ps1 deploy` works for all scenarios
- **One Config File**: All environment differences controlled via `.env` variables
- **Reduced Complexity**: No environment selection needed

### **2. Easier Maintenance**
- **Single Compose File**: One file to update instead of three
- **Unified Documentation**: One README covers everything
- **Consistent Behavior**: Same deployment process regardless of use case

### **3. Flexible Configuration**
- **Environment Variables**: Easy switching between development/production
- **Azure AD Support**: Works with `az login` or service principals
- **Exchange Isolation**: Different RabbitMQ exchanges via `RABBITMQ__EXCHANGENAME`

## ?? How to Use

### **Production Deployment:**
```powershell
# Copy and configure environment
Copy-Item .env.template .env
# Edit .env with production settings

# Deploy
.\deploy.ps1 deploy
```

### **Development Usage:**
```powershell  
# Copy and configure environment
Copy-Item .env.template .env
# Edit .env and uncomment development overrides:
# RABBITMQ__EXCHANGENAME=klim.events.dev
# SERILOG__MINIMUMLEVEL__DEFAULT=Debug

# Deploy
.\deploy.ps1 deploy
```

## ?? Configuration Flexibility

The single `docker-compose.yml` file supports all scenarios through environment variables:

### **Database Configuration (unchanged):**
```bash
DATABASE__CONNECTIONSTRING=Server=klim-sql.2e340c2a848b.database.windows.net;...
DATABASE__USEAZUREAD=true
```

### **Environment Switching:**
```bash
# Production
DOTNET_ENVIRONMENT=Production
RABBITMQ__EXCHANGENAME=klim.events
SERILOG__MINIMUMLEVEL__DEFAULT=Information

# Development (uncomment to override)  
# DOTNET_ENVIRONMENT=Development
# RABBITMQ__EXCHANGENAME=klim.events.dev
# SERILOG__MINIMUMLEVEL__DEFAULT=Debug
```

## ?? Deployment Commands

| Action | Command | Description |
|--------|---------|-------------|
| Deploy | `.\deploy.ps1 deploy` | Build and start service |
| Health | `.\deploy.ps1 health` | Check service health |
| Logs | `.\deploy.ps1 logs` | View service logs |
| Stop | `.\deploy.ps1 down` | Stop service |
| Status | `.\deploy.ps1 ps` | Check service status |

## ? Backward Compatibility

### **What Still Works:**
- ? Azure AD authentication (via `az login` or service principal)
- ? External RabbitMQ connection (`host.docker.internal`)
- ? Health endpoints (`/health/live`, `/health/ready`)
- ? Log file access (`./logs` directory)
- ? All environment variables and configuration options

### **What Changed:**
- ? No more environment-specific Docker Compose files
- ? No more `deploy-env.ps1` multi-environment script
- ? No more environment selection parameters
- ? Everything now controlled via `.env` file configuration

## ?? Migration Guide

### **If you were using:**
```powershell
# Old multi-environment approach
.\deploy-env.ps1 prod deploy
.\deploy-env.ps1 dev up
.\deploy-env.ps1 test up
```

### **Now use:**
```powershell
# Configure environment in .env file
Copy-Item .env.template .env
# Edit .env for your specific use case

# Single deployment command
.\deploy.ps1 deploy
```

## ?? File Changes Summary

### **Removed Files:**
- `docker-compose.dev.yml`
- `docker-compose.prod.yml` 
- `docker-compose.test.yml`
- `deploy-env.ps1`
- `.env.dev.template`
- `DEVELOPMENT.md`
- `ENVIRONMENTS.md`

### **New/Updated Files:**
- `docker-compose.yml` ? Consolidated from docker-compose.prod.yml
- `deploy.ps1` ? Simplified deployment script
- `deploy.bat` ? Windows batch wrapper
- `.env.template` ? Unified configuration template  
- `README.md` ? Updated with unified deployment guide

## ?? Result

**Before**: Complex multi-file deployment with environment selection
**After**: Simple single-file deployment with environment variables

The KLIM.Events service now has a **clean, unified Docker deployment** that's easier to use and maintain while preserving all functionality and flexibility! ??