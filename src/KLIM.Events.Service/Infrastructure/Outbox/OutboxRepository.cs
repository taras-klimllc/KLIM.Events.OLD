using Microsoft.Data.SqlClient;
using System.Diagnostics;

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

    private const string SQL_ENSURE_SCHEMA = @"
-- Robust outbox schema initialization with complete cleanup
BEGIN TRY
    BEGIN TRANSACTION;
    
    -- Step 1: Force drop everything related to OutboxMessages to ensure clean state
    -- This approach is more reliable than trying to selectively clean orphaned objects
    
    -- Drop indexes first (if they exist)
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_OutboxMessages_MessageKey')
    BEGIN
        DECLARE @table_name_1 NVARCHAR(256);
        SELECT @table_name_1 = SCHEMA_NAME(t.schema_id) + '.' + t.name 
        FROM sys.indexes i 
        JOIN sys.tables t ON i.object_id = t.object_id 
        WHERE i.name = 'UX_OutboxMessages_MessageKey';
        
        IF @table_name_1 IS NOT NULL
            EXEC('DROP INDEX UX_OutboxMessages_MessageKey ON ' + @table_name_1);
    END
    
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_Pending')
    BEGIN
        DECLARE @table_name_2 NVARCHAR(256);
        SELECT @table_name_2 = SCHEMA_NAME(t.schema_id) + '.' + t.name 
        FROM sys.indexes i 
        JOIN sys.tables t ON i.object_id = t.object_id 
        WHERE i.name = 'IX_OutboxMessages_Pending';
        
        IF @table_name_2 IS NOT NULL
            EXEC('DROP INDEX IX_OutboxMessages_Pending ON ' + @table_name_2);
    END
    
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_DispatchedAt')
    BEGIN
        DECLARE @table_name_3 NVARCHAR(256);
        SELECT @table_name_3 = SCHEMA_NAME(t.schema_id) + '.' + t.name 
        FROM sys.indexes i 
        JOIN sys.tables t ON i.object_id = t.object_id 
        WHERE i.name = 'IX_OutboxMessages_DispatchedAt';
        
        IF @table_name_3 IS NOT NULL
            EXEC('DROP INDEX IX_OutboxMessages_DispatchedAt ON ' + @table_name_3);
    END
    
    -- Drop primary key constraint if it exists
    IF EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = 'PK_OutboxMessages')
    BEGIN
        DECLARE @pk_table_name NVARCHAR(256);
        SELECT @pk_table_name = SCHEMA_NAME(t.schema_id) + '.' + t.name 
        FROM sys.key_constraints k 
        JOIN sys.tables t ON k.parent_object_id = t.object_id 
        WHERE k.name = 'PK_OutboxMessages';
        
        IF @pk_table_name IS NOT NULL
            EXEC('ALTER TABLE ' + @pk_table_name + ' DROP CONSTRAINT PK_OutboxMessages');
    END
    
    -- Drop default constraint if it exists
    IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_OutboxMessages_OccurredAt')
    BEGIN
        DECLARE @df_table_name NVARCHAR(256);
        SELECT @df_table_name = SCHEMA_NAME(t.schema_id) + '.' + t.name 
        FROM sys.default_constraints d 
        JOIN sys.tables t ON d.parent_object_id = t.object_id 
        WHERE d.name = 'DF_OutboxMessages_OccurredAt';
        
        IF @df_table_name IS NOT NULL
            EXEC('ALTER TABLE ' + @df_table_name + ' DROP CONSTRAINT DF_OutboxMessages_OccurredAt');
    END
    
    -- Drop table if it exists
    IF EXISTS (SELECT 1 FROM sys.tables WHERE name = 'OutboxMessages' AND schema_id = SCHEMA_ID('dbo'))
    BEGIN
        DROP TABLE dbo.OutboxMessages;
    END
    
    -- Step 2: Create the complete OutboxMessages table
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
    
    -- Create indexes
    CREATE UNIQUE INDEX UX_OutboxMessages_MessageKey ON dbo.OutboxMessages (MessageKey) WHERE MessageKey IS NOT NULL;
    CREATE INDEX IX_OutboxMessages_Pending ON dbo.OutboxMessages (DispatchedAt, OccurredAt) INCLUDE (Type, Payload, Headers) WHERE DispatchedAt IS NULL;
    CREATE INDEX IX_OutboxMessages_DispatchedAt ON dbo.OutboxMessages (DispatchedAt) INCLUDE (OccurredAt);
    
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
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
        if (useAzureAd)
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