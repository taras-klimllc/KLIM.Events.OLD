using Microsoft.Data.SqlClient;

namespace KLIM.Events.Service.Infrastructure.Outbox;

/// <summary>
/// Repository for outbox message operations
/// </summary>
public sealed class OutboxRepository
{
    private const int DEFAULT_COMMAND_TIMEOUT = 30;

    private const string SQL_FIND_PENDING = @"
SELECT TOP(@batchSize) Id, Type, Payload, Headers, OccurredAt, SourceEntity, SourceId, ChangeVersion, MessageKey
FROM dbo.OutboxMessages WITH (READPAST)
WHERE DispatchedAt IS NULL
ORDER BY OccurredAt ASC";

    private const string SQL_MARK_DISPATCHED = @"
UPDATE dbo.OutboxMessages 
SET DispatchedAt = GETUTCDATE()
WHERE Id = @id AND DispatchedAt IS NULL";

    private const string SQL_CLEANUP_OLD = @"
DELETE TOP(@maxRows) FROM dbo.OutboxMessages
WHERE DispatchedAt IS NOT NULL 
  AND DispatchedAt < DATEADD(DAY, -@retentionDays, GETUTCDATE())";

    // New non-destructive schema ensure script (preserves existing data)
    private const string SQL_ENSURE_SCHEMA = @"
BEGIN TRY
    BEGIN TRANSACTION;

    -- Create table if missing (non-destructive)
    IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OutboxMessages' AND schema_id = SCHEMA_ID('dbo'))
    BEGIN
        CREATE TABLE dbo.OutboxMessages
        (
            Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
            Type NVARCHAR(400) NOT NULL,
            Payload NVARCHAR(MAX) NOT NULL,
            Headers NVARCHAR(MAX) NULL,
            OccurredAt DATETIME2(7) NOT NULL CONSTRAINT DF_OutboxMessages_OccurredAt DEFAULT (SYSUTCDATETIME()),
            DispatchedAt DATETIME2(7) NULL,
            MessageKey NVARCHAR(256) NULL,
            SourceEntity NVARCHAR(128) NULL,
            SourceId NVARCHAR(128) NULL,
            ChangeVersion BIGINT NULL
        );
    END

    -- Add missing columns if future migrations added them (example pattern)
    IF COL_LENGTH('dbo.OutboxMessages', 'MessageKey') IS NULL ALTER TABLE dbo.OutboxMessages ADD MessageKey NVARCHAR(256) NULL;
    IF COL_LENGTH('dbo.OutboxMessages', 'SourceEntity') IS NULL ALTER TABLE dbo.OutboxMessages ADD SourceEntity NVARCHAR(128) NULL;
    IF COL_LENGTH('dbo.OutboxMessages', 'SourceId') IS NULL ALTER TABLE dbo.OutboxMessages ADD SourceId NVARCHAR(128) NULL;
    IF COL_LENGTH('dbo.OutboxMessages', 'ChangeVersion') IS NULL ALTER TABLE dbo.OutboxMessages ADD ChangeVersion BIGINT NULL;

    -- Create / recreate indexes only if they do not exist
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_OutboxMessages_MessageKey' AND object_id = OBJECT_ID('dbo.OutboxMessages'))
        CREATE UNIQUE INDEX UX_OutboxMessages_MessageKey ON dbo.OutboxMessages (MessageKey) WHERE MessageKey IS NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_Pending' AND object_id = OBJECT_ID('dbo.OutboxMessages'))
        CREATE INDEX IX_OutboxMessages_Pending ON dbo.OutboxMessages (DispatchedAt, OccurredAt) INCLUDE (Type, Payload, Headers) WHERE DispatchedAt IS NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_DispatchedAt' AND object_id = OBJECT_ID('dbo.OutboxMessages'))
        CREATE INDEX IX_OutboxMessages_DispatchedAt ON dbo.OutboxMessages (DispatchedAt) INCLUDE (OccurredAt);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH";

    private readonly SqlAuthenticationService _authService;
    private readonly ILogger<OutboxRepository> _logger;

    public OutboxRepository(SqlAuthenticationService authService, ILogger<OutboxRepository> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    public async Task EnsureSchemaAsync(string connectionString, bool useAzureAd, CancellationToken cancellationToken)
    {
        await using var conn = await CreateConnectionAsync(connectionString, useAzureAd, cancellationToken);
        await using var cmd = new SqlCommand(SQL_ENSURE_SCHEMA, conn) { CommandTimeout = DEFAULT_COMMAND_TIMEOUT };
        await conn.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<OutboxMessage>> ReadPendingAsync(string connectionString, bool useAzureAd, int batchSize, CancellationToken cancellationToken)
    {
        await using var conn = await CreateConnectionAsync(connectionString, useAzureAd, cancellationToken);
        await using var cmd = new SqlCommand(SQL_FIND_PENDING, conn) { CommandTimeout = DEFAULT_COMMAND_TIMEOUT };
        cmd.Parameters.AddWithValue("@batchSize", batchSize);
        await conn.OpenAsync(cancellationToken);

        var messages = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new OutboxMessage(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDateTime(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }

        return messages;
    }

    public async Task MarkDispatchedAsync(string connectionString, bool useAzureAd, Guid messageId, CancellationToken cancellationToken)
    {
        await using var conn = await CreateConnectionAsync(connectionString, useAzureAd, cancellationToken);
        await using var cmd = new SqlCommand(SQL_MARK_DISPATCHED, conn) { CommandTimeout = DEFAULT_COMMAND_TIMEOUT };
        cmd.Parameters.AddWithValue("@id", messageId);
        await conn.OpenAsync(cancellationToken);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CleanupOldAsync(string connectionString, bool useAzureAd, int retentionDays, int maxRows, CancellationToken cancellationToken)
    {
        await using var conn = await CreateConnectionAsync(connectionString, useAzureAd, cancellationToken);
        await using var cmd = new SqlCommand(SQL_CLEANUP_OLD, conn) { CommandTimeout = DEFAULT_COMMAND_TIMEOUT };
        cmd.Parameters.AddWithValue("@retentionDays", retentionDays);
        cmd.Parameters.AddWithValue("@maxRows", maxRows);
        await conn.OpenAsync(cancellationToken);
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlConnection> CreateConnectionAsync(string connectionString, bool useAzureAd, CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(connectionString);

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
            conn.AccessToken = await _authService.AcquireTokenAsync(cancellationToken);
        }

        return conn;
    }
}

public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    string? Headers,
    DateTime OccurredAt,
    string? SourceEntity,
    string? SourceId,
    long? ChangeVersion,
    string? MessageKey
);