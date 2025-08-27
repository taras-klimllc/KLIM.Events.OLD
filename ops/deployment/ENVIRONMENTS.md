# KLIM.Events - Environment Comparison

## ?? Environment Overview

The KLIM.Events service supports **three distinct deployment environments**, each optimized for specific use cases. Here's why we have `.prod` suffix and how to work with different environments.

## ?? Environment Comparison Table

| Aspect | Development | Production | Test |
|--------|-------------|------------|------|
| **Purpose** | Local development with realistic data | Production workloads | Automated testing & CI/CD |
| **Database** | Azure SQL Database (shared) | Azure SQL Database | Local SQL Server (in-memory) |
| **Authentication** | Azure AD (your account) | Azure AD (service principal) | SQL Authentication |
| **RabbitMQ** | Local container | External container | Local container (in-memory) |
| **Exchange** | `klim.events.dev` | `klim.events` | `klim.events.test` |
| **Health Port** | 8081 | 8080 | 8082 |
| **RabbitMQ Port** | 5673/15673 | 5672/15672 | 5674/15674 |
| **Logging Level** | Debug | Information | Warning |
| **Polling Interval** | 10 seconds | 5 seconds | 2 seconds |
| **Memory Limit** | 256MB | 256MB | 64MB |
| **Storage** | Persistent | Persistent | In-memory (tmpfs) |

## ?? Why `.prod` Suffix Exists

### **Historical Context**
The `.prod` suffix exists because initially, **Docker deployment was designed production-first**:

1. **Production Priority**: The original implementation focused on production deployment
2. **Infrastructure Sharing**: Used external RabbitMQ and Azure SQL Database
3. **Security First**: Azure AD authentication was the primary concern
4. **Naming Convention**: Distinguished from potential future dev/test configs

### **Current Multi-Environment Approach**
Now we have **comprehensive multi-environment support**:

- `docker-compose.dev.yml` - Development environment
- `docker-compose.prod.yml` - Production environment  
- `docker-compose.test.yml` - Testing environment
- `docker-compose.yml` - Legacy infrastructure services only

## ?? Environment Use Cases

### **?? Development Environment**
**When to use:** Daily development, debugging, feature development

```powershell
.\deploy-env.ps1 dev up
```

**Characteristics:**
- ? **Realistic data** from production Azure SQL database
- ? **Isolated messaging** via `klim.events.dev` exchange
- ? **Your Azure credentials** via `az login`
- ? **Verbose logging** for debugging
- ? **Slower polling** for easier debugging
- ? **Local RabbitMQ** to avoid production interference

**Perfect for:**
- Testing projectors against real entity data
- Debugging Azure AD authentication
- Validating change tracking behavior
- Developing new entity support

### **?? Production Environment**  
**When to use:** Production deployments, production monitoring

```powershell
.\deploy-env.ps1 prod deploy
```

**Characteristics:**
- ? **Production database** with production data
- ? **External RabbitMQ** shared with other KLIM services
- ? **Service principal authentication** for security
- ? **Optimized performance** settings
- ? **Production logging** levels
- ? **Resource limits** for stability

**Perfect for:**
- Production workloads
- Real business event processing
- Integration with downstream services
- Production monitoring and alerting

### **?? Test Environment**
**When to use:** Automated testing, CI/CD pipelines, unit testing

```powershell
.\deploy-env.ps1 test up
```

**Characteristics:**
- ? **Local SQL Server** with clean state
- ? **In-memory storage** for speed
- ? **No Azure dependencies** 
- ? **Fast polling** for quick tests
- ? **Minimal logging** to reduce noise
- ? **Easy cleanup** between test runs

**Perfect for:**
- Integration testing
- CI/CD pipeline validation
- Performance testing
- Contract testing

## ?? Authentication Strategy by Environment

### **Development & Production: Azure AD**
Both environments use Azure SQL Database, so Azure AD authentication is **required**:

```bash
# Personal development
az login

# Service principal (production/CI)
AZURE_CLIENT_ID=your-service-principal-id
AZURE_CLIENT_SECRET=your-service-principal-secret
AZURE_TENANT_ID=your-tenant-id
```

**Why Azure AD for development?**
- ? **Realistic security testing** - Same auth as production
- ? **No credential management** - Uses your existing Azure login
- ? **Enterprise compliance** - Follows security best practices
- ? **Token refresh handling** - Tests production authentication flows

### **Test: SQL Authentication**
Test environment uses local SQL Server with simple SQL auth:

```bash
# Simple SQL authentication for tests
Database__UseAzureAd=false
ConnectionString="Server=sqlserver-test;User Id=sa;Password=TestPassword123!;..."
```

**Why SQL auth for testing?**
- ? **No external dependencies** - Runs without Azure
- ? **Faster startup** - No token acquisition delays
- ? **CI/CD friendly** - Works in any environment
- ? **Isolated testing** - No production dependencies

## ?? Migration from Production-Only to Multi-Environment

### **Before (Production-Only)**
```
docker-compose.prod.yml  ? Only production
.env                     ? Production config only
deploy.ps1              ? Production deployment only
```

### **After (Multi-Environment)**
```
docker-compose.dev.yml   ? Development environment
docker-compose.prod.yml  ? Production environment  
docker-compose.test.yml  ? Test environment
.env.dev                ? Development config
.env                    ? Production config
.env.test              ? Test config (auto-generated)
deploy-env.ps1         ? Multi-environment deployment
```

### **Backward Compatibility**
Old production commands still work:
```powershell
# Old way (still works)
docker-compose -f docker-compose.prod.yml up -d

# New way (recommended)
.\deploy-env.ps1 prod up
```

## ?? When to Use Which Environment

### **Daily Development Workflow**
```powershell
# Start development environment
.\deploy-env.ps1 dev up

# Code, debug, test...

# Stop when done
.\deploy-env.ps1 dev down
```

### **Testing Workflow**
```powershell
# Quick integration tests
.\deploy-env.ps1 test up
# Run tests...
.\deploy-env.ps1 test down
```

### **Production Deployment**
```powershell
# Production deployment
.\deploy-env.ps1 prod deploy

# Monitor health
.\deploy-env.ps1 prod health
```

### **CI/CD Pipeline**
```yaml
# Example GitHub Actions
- name: Test
  run: |
    .\deploy-env.ps1 test up
    # Run integration tests
    .\deploy-env.ps1 test down

- name: Deploy to Production
  run: .\deploy-env.ps1 prod deploy
```

## ?? Best Practices

### **Development**
- ? Always use `dev` environment for local development
- ? Monitor logs with `.\deploy-env.ps1 dev logs`
- ? Use RabbitMQ UI at http://localhost:15673 for message debugging
- ? Keep `az login` session active

### **Production**
- ? Use service principal authentication in production
- ? Monitor health endpoints regularly
- ? Set up proper resource limits
- ? Configure log aggregation

### **Testing**
- ? Use `test` environment for automated tests
- ? Clean up after tests with `.\deploy-env.ps1 test down`
- ? Design tests to be independent and repeatable
- ? Use in-memory storage for speed

## ?? Summary

The **`.prod` suffix exists for historical reasons** when Docker deployment was production-focused. Now we have **comprehensive multi-environment support** that allows:

- **Realistic development** against production Azure SQL with your credentials
- **Safe isolation** through separate RabbitMQ exchanges
- **Fast testing** with local, in-memory infrastructure  
- **Production-grade deployment** with proper security and monitoring

Each environment is optimized for its specific use case while maintaining consistency in deployment tooling and configuration patterns.

**Use the right environment for the right job** and enjoy productive, safe development! ??