# Multi-stage build for .NET 8 Worker Service with ASP.NET Core
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project file for dependency restore
COPY src/KLIM.Events.Service/KLIM.Events.Service.csproj src/KLIM.Events.Service/
RUN dotnet restore src/KLIM.Events.Service/KLIM.Events.Service.csproj

# Copy source and build (avoiding --no-restore to fix NuGet issue)
COPY src/ src/
RUN dotnet publish src/KLIM.Events.Service/KLIM.Events.Service.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    /p:PublishTrimmed=false

# Runtime stage - ASP.NET Core Runtime (required for Microsoft.AspNetCore.App framework reference)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Install curl for health checks and debugging
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN groupadd --gid 1001 appgroup && \
    useradd --uid 1001 --gid appgroup --shell /bin/false --create-home appuser

# Set working directory and copy app
WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish .

# Configure environment for worker service with ASP.NET Core
ENV DOTNET_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_USE_POLLING_FILE_WATCHER=true
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

# Expose health check port
EXPOSE 8080

# Health check - using correct path
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

# Switch to non-root user
USER appuser

# Start worker service
ENTRYPOINT ["dotnet", "KLIM.Events.Service.dll"]