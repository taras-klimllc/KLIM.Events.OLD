using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KLIM.Events.Service.Infrastructure.HealthChecks;

/// <summary>
/// Hosts a minimal ASP.NET Core pipeline dedicated to health probes (/health/live and /health/ready).
/// Isolated from the worker host to decouple lifecycle & avoid blocking the messaging loop.
/// </summary>
public sealed class HealthEndpointHostService : BackgroundService
{
    private readonly ILogger<HealthEndpointHostService> _log;
    private readonly IHostApplicationLifetime _lifetime;
    private IHost? _webHost;
    private readonly TaskCompletionSource _completionSource = new();

    public HealthEndpointHostService(
        ILogger<HealthEndpointHostService> log,
        IHostApplicationLifetime lifetime) => (_log, _lifetime) = (log, lifetime);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var builder = WebApplication.CreateBuilder();

            // Import health checks from main host
            builder.Services.AddHealthChecks();

            // Configure server options
            var port = builder.Configuration.GetValue<int?>("Health:Port") ?? 8080;
            builder.WebHost
                .ConfigureKestrel(o =>
                {
                    o.AddServerHeader = false; // Security: don't expose server details
                })
                .UseUrls($"http://0.0.0.0:{port}");

            var app = builder.Build();

            // Configure endpoints with proper tagging
            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                // Liveness just checks if process is running, no dependencies
                Predicate = _ => false,
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = 200,
                    [HealthStatus.Degraded] = 200, // Still live even if degraded
                    [HealthStatus.Unhealthy] = 503
                }
            });

            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                // Readiness checks all dependencies with "readiness" tag
                Predicate = check => check.Tags.Contains("readiness"),
                ResultStatusCodes =
                {
                    [HealthStatus.Healthy] = 200,
                    [HealthStatus.Degraded] = 200, // Can still serve traffic when degraded
                    [HealthStatus.Unhealthy] = 503
                }
            });

            await app.StartAsync(stoppingToken);
            _webHost = app;

            _log.LogInformation("Health endpoints started on http://localhost:{Port}", port);

            // Register graceful shutdown
            _lifetime.ApplicationStopping.Register(() =>
            {
                _log.LogInformation("Health endpoints stopping");
                app.StopAsync().GetAwaiter().GetResult();
                _completionSource.TrySetResult();
            });

            // Wait for cancellation
            stoppingToken.Register(() => _completionSource.TrySetResult());

            // Wait until cancellation or application shutdown
            await _completionSource.Task;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal cancellation, don't log as error
            _log.LogInformation("Health endpoints stopping due to host shutdown");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error starting health endpoints");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_webHost is IHost host)
        {
            try
            {
                await host.StopAsync(cancellationToken);
                _log.LogInformation("Health endpoints stopped");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Error stopping health endpoints");
            }
        }

        await base.StopAsync(cancellationToken);
    }
}
