# KLIM.Events Docker Deployment

## Prerequisites
- Docker 20.10+ with BuildKit support
- External RabbitMQ container (competent_burnell) running
- Same Azure SQL Database access as KLIM.Integrations

## Quick Start

### 1. Configure Environment
```powershell
# Copy template and edit with your settings
Copy-Item .env.template .env
# Edit .env with your database connection string and RabbitMQ settings
```

### 2. Deploy Services
```powershell
# PowerShell (Recommended)
.\deploy.ps1 deploy

# Or Windows Batch
deploy-simple.bat deploy

# Or Direct Docker Commands
docker build -t klim/events-service:latest -f src/KLIM.Events.Service/Dockerfile .
docker-compose -f docker-compose.prod.yml up -d
```

## Service Information

### Infrastructure
- **Service Type**: .NET 8 Worker Service (Background Service)
- **Port**: 8080 (Health Checks)
- **RabbitMQ**: External container (localhost:5672)
- **Database**: Same Azure SQL as KLIM.Integrations
- **Exchange**: klim.events

### Key Features
- SQL Change Tracking polling
- Outbox pattern implementation
- Rich event payloads (2KB+ with entity snapshots)
- Health monitoring endpoints
- Structured logging with Serilog

## Management Commands

```powershell
# Build images only
.\deploy.ps1 build

# Deploy services
.\deploy.ps1 deploy

# Check status and health
.\deploy.ps1 status

# View logs
.\deploy.ps1 logs

# Restart services
.\deploy.ps1 restart

# Stop services
.\deploy.ps1 stop
```

## Verification

### Health Check
```powershell
# PowerShell
Invoke-RestMethod -Uri "http://localhost:8080/health"

# curl
curl http://localhost:8080/health
```

### Expected Health Response
```json
{
  "status": "ok",
  "checks": {
    "database": { "status": "healthy" },
    "rabbitmq": { "status": "healthy" }
  }
}
```

### RabbitMQ Verification
- Management UI: http://localhost:15672 (guest/guest)
- Exchange: klim.events should be visible
- Check for published messages in exchange

## Troubleshooting

### Common Issues

#### 1. RabbitMQ Connection
```powershell
# Check RabbitMQ container
docker ps | Select-String rabbitmq

# Test connectivity
Test-NetConnection -ComputerName localhost -Port 5672
```

#### 2. Database Connection
```powershell
# Check logs for Azure AD auth
.\deploy.ps1 logs | Select-String "database\|sql\|auth"
```

#### 3. Service Not Starting
```powershell
# Check container logs
docker logs klim-events-service

# Check resource usage
docker stats klim-events-service
```

## Production Notes

### Security
- Uses non-root user in container
- Azure AD authentication for database
- Secure connection strings (no embedded credentials)

### Performance
- Memory limit: 256M (can adjust in docker-compose.prod.yml)
- Optimized for batch processing
- Payload size monitoring and truncation

### Monitoring
- Structured logging to files and console
- Health endpoints for liveness/readiness
- Real-time diagnostics every 5 minutes

## Integration with KLIM.Integrations

### Shared Infrastructure
- Same RabbitMQ instance (external container)
- Same Azure SQL Database
- Exchange: klim.events (shared namespace)

### Port Allocation
- KLIM.Events: 8080 (health checks)
- KLIM.Integrations: 8088 (API), 8089 (health)
- No conflicts in deployment