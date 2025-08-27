# Test Docker build to verify fixes

## 1. Fixed Runtime Issue
- Changed from `mcr.microsoft.com/dotnet/runtime:8.0` to `mcr.microsoft.com/dotnet/aspnet:8.0`
- Reason: Project uses `Microsoft.AspNetCore.App` framework reference for health checks

## 2. Added Missing Contract
- Created `DomainChangeNotification` record in `src\KLIM.Events.Service\Infrastructure\ChangeTracking\`
- Added missing `OutboxInsert` record definition in `OutboxRepository.cs`
- Fixed `IOutboxWriter` interface definition and implementation

## 3. Docker Build Command
```powershell
# Test build command
docker build -t klim/events-service:test -f src/KLIM.Events.Service/Dockerfile .
```

## 4. Expected Resolution
- Previous error: `exit code: 1` during `dotnet publish` step
- Root cause: Missing ASP.NET Core runtime for health check endpoints
- Solution: Updated Dockerfile to use correct runtime base image

## 5. Deployment Command
```powershell
# After successful build, deploy with
.\deploy.ps1 deploy
```

## Key Changes Made:
1. **Dockerfile Runtime Fix**: Changed base image to support ASP.NET Core
2. **Missing Dependencies**: Added `DomainChangeNotification` contract
3. **Interface Alignment**: Fixed `IOutboxWriter` and `OutboxInsert` definitions
4. **Health Check Support**: Enabled proper ASP.NET Core runtime for port 8080 endpoints

The deployment should now work correctly with the fixed runtime environment.