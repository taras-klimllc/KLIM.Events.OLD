using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Diagnostic service to check outbox and messaging status
/// </summary>
public sealed class OutboxDiagnosticService : BackgroundService
{
    private readonly ILogger<OutboxDiagnosticService> _logger;
    private readonly OutboxRepository _repository;
    private readonly SqlAuthenticationService _authService;
    private readonly OutboxOptions _config;
    private readonly DatabaseOptions _databaseConfig;

    public OutboxDiagnosticService(
        ILogger<OutboxDiagnosticService> logger,
        OutboxRepository repository,
        SqlAuthenticationService authService,
        IOptions<OutboxOptions> options,
        IOptions<DatabaseOptions> databaseOptions)
    {
        _logger = logger;
        _repository = repository;
        _authService = authService;
        _config = options.Value;
        _databaseConfig = databaseOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run diagnostics once on startup, then every 5 minutes
        await RunDiagnosticsAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await RunDiagnosticsAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        try
        {
            var (connectionString, useAzureAd) = GetDatabaseConfig();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogWarning("?? DIAGNOSTICS: No database connection string configured");
                return;
            }

            connectionString = _authService.SanitizeConnectionString(connectionString, useAzureAd);

            await using var connection = new SqlConnection(connectionString);

            // Check if connection string already has Azure AD authentication configured
            var csBuilder = new SqlConnectionStringBuilder(connectionString);
            var hasAzureAdAuth = csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryDefault ||
                                csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryIntegrated ||
                                csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryInteractive ||
                                csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryManagedIdentity ||
                                csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryServicePrincipal ||
                                csBuilder.Authentication == SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow;

            // Only set AccessToken if using Azure AD but connection string doesn't already specify Azure AD authentication
            if (useAzureAd && !hasAzureAdAuth)
            {
                connection.AccessToken = await _authService.AcquireTokenAsync();
            }

            await connection.OpenAsync();

            // Check if outbox table exists
            const string checkTableSql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'OutboxMessages'";
            await using var checkCmd = new SqlCommand(checkTableSql, connection);
            var tableExists = ((int?)await checkCmd.ExecuteScalarAsync()) > 0;

            if (!tableExists)
            {
                _logger.LogWarning("?? DIAGNOSTICS: OutboxMessages table does not exist");
                return;
            }

            // Count pending messages
            const string pendingSql = "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE DispatchedAt IS NULL";
            await using var pendingCmd = new SqlCommand(pendingSql, connection);
            var pendingCount = (int?)await pendingCmd.ExecuteScalarAsync() ?? 0;

            // Count dispatched messages
            const string dispatchedSql = "SELECT COUNT(*) FROM dbo.OutboxMessages WHERE DispatchedAt IS NOT NULL";
            await using var dispatchedCmd = new SqlCommand(dispatchedSql, connection);
            var dispatchedCount = (int?)await dispatchedCmd.ExecuteScalarAsync() ?? 0;

            // Get recent activity
            const string recentSql = @"
                SELECT TOP 5 Id, Type, SourceEntity, SourceId, OccurredAt, DispatchedAt 
                FROM dbo.OutboxMessages 
                ORDER BY OccurredAt DESC";
            await using var recentCmd = new SqlCommand(recentSql, connection);
            await using var reader = await recentCmd.ExecuteReaderAsync();

            var recentMessages = new List<(Guid Id, string Type, string? Entity, string? SourceId, DateTime Occurred, DateTime? Dispatched)>();
            while (await reader.ReadAsync())
            {
                recentMessages.Add((
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetDateTime(4),
                    reader.IsDBNull(5) ? null : reader.GetDateTime(5)
                ));
            }

            _logger.LogInformation(
                "?? OUTBOX STATUS: Pending={Pending} | Dispatched={Dispatched} | Total={Total} | Enabled={Enabled}",
                pendingCount, dispatchedCount, pendingCount + dispatchedCount, _config.Enabled);

            if (recentMessages.Count > 0)
            {
                _logger.LogInformation("?? RECENT MESSAGES:");
                foreach (var msg in recentMessages)
                {
                    var status = msg.Dispatched.HasValue ? "? SENT" : "? PENDING";
                    var friendlyType = GetFriendlyTypeName(msg.Type);
                    _logger.LogInformation("  {Status} {Type} | {Entity}#{SourceId} | {Occurred:HH:mm:ss}",
                        status, friendlyType, msg.Entity ?? "Unknown", msg.SourceId ?? "Unknown", msg.Occurred);
                }
            }
            else if (pendingCount == 0 && dispatchedCount == 0)
            {
                _logger.LogWarning("?? NO MESSAGES FOUND - Check if ChangeTrackingPollingService is populating outbox");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "?? DIAGNOSTICS FAILED");
        }
    }

    private static string GetFriendlyTypeName(string fullType)
    {
        if (string.IsNullOrWhiteSpace(fullType)) return fullType;
        // Remove assembly details if present
        var primary = fullType.Split(',')[0];
        var lastDot = primary.LastIndexOf('.');
        return lastDot >= 0 ? primary[(lastDot + 1)..] : primary; // return simple class name
    }

    private (string ConnectionString, bool UseAzureAd) GetDatabaseConfig()
    {
        return (_databaseConfig.ConnectionString, _databaseConfig.UseAzureAd);
    }
}