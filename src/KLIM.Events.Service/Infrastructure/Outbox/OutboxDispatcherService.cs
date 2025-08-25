using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Simplified outbox dispatcher that delegates to specialized services
/// </summary>
public sealed class OutboxDispatcherService : BackgroundService
{
    private readonly ILogger<OutboxDispatcherService> _logger;
    private readonly OutboxRepository _repository;
    private readonly MessagePublisher _publisher;
    private readonly SqlAuthenticationService _authService;
    private readonly PollingStrategy _pollingStrategy = new();
    private readonly CleanupScheduler _cleanupScheduler = new();
    private readonly OutboxOptions _config;
    private readonly ChangeTrackingOptions _fallbackConfig;

    private int _consecutiveErrors;

    public OutboxDispatcherService(
        ILogger<OutboxDispatcherService> logger,
        OutboxRepository repository,
        MessagePublisher publisher,
        SqlAuthenticationService authService,
        IOptions<OutboxOptions> options,
        IOptions<ChangeTrackingOptions> fallbackOptions)
    {
        _logger = logger;
        _repository = repository;
        _publisher = publisher;
        _authService = authService;
        _config = options.Value;
        _fallbackConfig = fallbackOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("Outbox dispatcher disabled");
            return;
        }

        var (connectionString, useAzureAd) = ResolveConnectionConfig();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("No connection string available; dispatcher disabled");
            return;
        }

        connectionString = _authService.SanitizeConnectionString(connectionString, useAzureAd);

        if (!await InitializeAsync(connectionString, useAzureAd, stoppingToken))
            return;

        await RunDispatchLoopAsync(connectionString, useAzureAd, stoppingToken);
    }

    private (string ConnectionString, bool UseAzureAd) ResolveConnectionConfig()
    {
        if (!string.IsNullOrWhiteSpace(_config.ConnectionString))
            return (_config.ConnectionString, _config.UseAzureAd);

        if (!string.IsNullOrWhiteSpace(_fallbackConfig.ConnectionString))
        {
            _logger.LogInformation("Using fallback connection from ChangeTracking config");
            return (_fallbackConfig.ConnectionString, _fallbackConfig.UseAzureAd);
        }

        return (string.Empty, false);
    }

    private async Task<bool> InitializeAsync(string connectionString, bool useAzureAd, CancellationToken stoppingToken)
    {
        try
        {
            await _repository.EnsureSchemaAsync(connectionString, useAzureAd, stoppingToken);

            var dbName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            _logger.LogInformation("Outbox initialized. Database: {Database}, AzureAD: {UseAzureAd}",
                dbName, useAzureAd);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize outbox schema");
            return false;
        }
    }

    private async Task RunDispatchLoopAsync(string connectionString, bool useAzureAd, CancellationToken stoppingToken)
    {
        var currentInterval = TimeSpan.FromSeconds(_config.DeliveryIntervalSeconds);
        var batchSize = Math.Max(_config.BatchSize, 1);
        var maxConcurrent = Math.Max(_config.MaxConcurrentDispatches, 1);
        var retentionDays = Math.Max(_config.RetentionDays, 1);

        _logger.LogInformation("Starting dispatch loop. Batch: {Batch}, Concurrent: {Concurrent}, Retention: {Retention} days",
            batchSize, maxConcurrent, retentionDays);

        using var semaphore = new SemaphoreSlim(maxConcurrent);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TryCleanupAsync(connectionString, useAzureAd, retentionDays, stoppingToken);

                var messageCount = await ProcessBatchAsync(connectionString, useAzureAd, batchSize, semaphore, stoppingToken);

                currentInterval = _pollingStrategy.AdjustInterval(currentInterval, messageCount);
                _consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException ex) when (SqlExceptionHelper.IsTransient(ex))
            {
                await HandleTransientErrorAsync(ex, stoppingToken);
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                _logger.LogError(ex, "Unexpected error in dispatch loop. Consecutive errors: {Count}", _consecutiveErrors);
            }

            await DelayAsync(currentInterval, stoppingToken);
        }

        _logger.LogInformation("Outbox dispatcher stopped");
    }

    private async Task<int> ProcessBatchAsync(string connectionString, bool useAzureAd, int batchSize, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        var messages = await _repository.ReadPendingAsync(connectionString, useAzureAd, batchSize, stoppingToken);
        if (messages.Count == 0)
        {
            _logger.LogTrace("No pending messages");
            return 0;
        }

        _logger.LogDebug("Processing {Count} messages", messages.Count);

        var stopwatch = Stopwatch.StartNew();
        var tasks = messages.Select(msg => ProcessMessageAsync(msg, connectionString, useAzureAd, semaphore, stoppingToken));
        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        var errorCount = results.Count(r => !r.Success);

        _logger.LogBatchProcessed(messages.Count, stopwatch.ElapsedMilliseconds, successCount, errorCount);

        return messages.Count;
    }

    private async Task<ProcessResult> ProcessMessageAsync(OutboxMessage message, string connectionString, bool useAzureAd, SemaphoreSlim semaphore, CancellationToken stoppingToken)
    {
        await semaphore.WaitAsync(stoppingToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();

            var publishResult = await _publisher.PublishAsync(message, stoppingToken);
            if (!publishResult.IsSuccess)
            {
                _logger.LogOutboxDispatchError(new InvalidOperationException(publishResult.Error),
                    message.Id, message.Type, message.SourceEntity ?? "Unknown", message.SourceId ?? "Unknown");
                return ProcessResult.Failed;
            }

            await _repository.MarkDispatchedAsync(connectionString, useAzureAd, message.Id, stoppingToken);

            _logger.LogOutboxDispatchSuccess(
                message.Id,
                message.Type,
                publishResult.Exchange,
                publishResult.RoutingKey,
                message.SourceEntity ?? "Unknown",
                message.SourceId ?? "Unknown",
                message.ChangeVersion?.ToString() ?? "N/A",
                stopwatch.ElapsedMilliseconds);

            LogChangeDetails(message, publishResult);

            return ProcessResult.Successful;
        }
        catch (Exception ex)
        {
            _logger.LogOutboxDispatchError(ex, message.Id, message.Type,
                message.SourceEntity ?? "Unknown", message.SourceId ?? "Unknown");
            return ProcessResult.Failed;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void LogChangeDetails(OutboxMessage message, PublishResult publishResult)
    {
        var details = publishResult.ChangeDetails;
        _logger.LogChangeDetails(message.Id, details.Operation, details.EntityId,
            details.EntityType, details.DisplayName, details.ChangedColumns, details.PreImageInfo, details.PostImageInfo);

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["MessageId"] = message.Id,
            ["Exchange"] = publishResult.Exchange,
            ["RoutingKey"] = publishResult.RoutingKey,
            ["Operation"] = details.Operation,
            ["EntityType"] = details.EntityType,
            ["DisplayName"] = details.DisplayName
        });

        _logger.LogInformation("Routed {Operation} on {EntityType} {DisplayName} via {Exchange}/{RoutingKey}",
            details.Operation, details.EntityType, details.DisplayName, publishResult.Exchange, publishResult.RoutingKey);
    }

    private async Task TryCleanupAsync(string connectionString, bool useAzureAd, int retentionDays, CancellationToken stoppingToken)
    {
        if (!_cleanupScheduler.ShouldCleanup(_config.CleanupIntervalHours))
            return;

        try
        {
            var deletedRows = await _repository.CleanupOldAsync(connectionString, useAzureAd,
                retentionDays, 10000, stoppingToken);

            if (deletedRows > 0)
                _logger.LogInformation("Cleaned up {Count} old messages", deletedRows);

            _cleanupScheduler.MarkCleaned();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cleanup failed");
        }
    }

    private async Task HandleTransientErrorAsync(SqlException ex, CancellationToken stoppingToken)
    {
        _consecutiveErrors++;
        var backoffSeconds = Math.Min(30, 2 * _consecutiveErrors);

        _logger.LogWarning(ex, "Transient SQL error. Retry in {Seconds}s", backoffSeconds);

        await DelayAsync(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
    }

    private readonly struct ProcessResult
    {
        public bool Success { get; }

        private ProcessResult(bool success) => Success = success;

        public static readonly ProcessResult Successful = new(true);
        public static readonly ProcessResult Failed = new(false);
    }
}
