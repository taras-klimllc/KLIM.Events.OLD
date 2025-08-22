using Microsoft.Data.SqlClient;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

public sealed class SqlOutboxWriter : IOutboxWriter
{
    private const int COMMAND_TIMEOUT_SECONDS = 30;

    // Ensure script (idempotent) – duplicated here so inserts succeed even if dispatcher (which also ensures) is disabled or pointed at another DB.
    private const string SQL_ENSURE_OUTBOX = @"
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
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_OutboxMessages_MessageKey')
BEGIN
    CREATE UNIQUE INDEX UX_OutboxMessages_MessageKey ON dbo.OutboxMessages (MessageKey) WHERE MessageKey IS NOT NULL;
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_Pending')
BEGIN
    CREATE INDEX IX_OutboxMessages_Pending ON dbo.OutboxMessages (DispatchedAt, OccurredAt) INCLUDE (Type, Payload, Headers) WHERE DispatchedAt IS NULL;
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_OutboxMessages_DispatchedAt')
BEGIN
    CREATE INDEX IX_OutboxMessages_DispatchedAt ON dbo.OutboxMessages (DispatchedAt) INCLUDE (OccurredAt);
END";

    private const string SQL_INSERT = @"INSERT INTO dbo.OutboxMessages
(Id, Type, Payload, Headers, OccurredAt, DispatchedAt, MessageKey, SourceEntity, SourceId, ChangeVersion)
VALUES (@Id, @Type, @Payload, NULL, @OccurredAt, NULL, @MessageKey, @SourceEntity, @SourceId, @ChangeVersion)";

    private static bool _ensured;
    private static readonly object _ensureLock = new();

    public async Task InsertManyAsync(SqlConnection conn, SqlTransaction tx, IReadOnlyCollection<OutboxInsert> inserts, CancellationToken ct)
    {
        // Ensure (once per process) inside same transaction before first insert.
        if (!_ensured)
        {
            await EnsureSchemaAsync(conn, tx, ct);
        }

        foreach (var m in inserts)
        {
            await using var cmd = new SqlCommand(SQL_INSERT, conn, tx)
            {
                CommandTimeout = COMMAND_TIMEOUT_SECONDS
            };
            cmd.Parameters.AddWithValue("@Id", m.Id);
            cmd.Parameters.AddWithValue("@Type", m.Type);
            cmd.Parameters.AddWithValue("@Payload", m.Payload);
            cmd.Parameters.AddWithValue("@OccurredAt", m.OccurredAt);
            cmd.Parameters.AddWithValue("@MessageKey", (object?)m.MessageKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceEntity", (object?)m.SourceEntity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceId", (object?)m.SourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ChangeVersion", m.ChangeVersion); // non-nullable long
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Duplicate (MessageKey) - idempotent replay, ignore
            }
        }
    }

    private static async Task EnsureSchemaAsync(SqlConnection conn, SqlTransaction tx, CancellationToken ct)
    {
        if (_ensured) return;
        lock (_ensureLock)
        {
            if (_ensured) return;
            _ensured = true; // optimistic; if execution fails next call will retry
        }
        await using var cmd = new SqlCommand(SQL_ENSURE_OUTBOX, conn, tx)
        {
            CommandTimeout = COMMAND_TIMEOUT_SECONDS
        };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
