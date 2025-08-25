using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Polls SQL Change Tracking for configured tables, maintains per-table watermark in dbo.ChangeTrackingCursor,
/// projects row changes into domain events via pluggable projectors, and persists them to the Outbox.
/// Supports Azure AD token auth (set UseAzureAd=true) or SQL auth.
/// </summary>
public sealed class ChangeTrackingPollingService : BackgroundService
{
    // SQL constants
    private const string SQL_ENSURE_CURSOR_TABLE = @"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'ChangeTrackingCursor' AND schema_id = SCHEMA_ID('dbo'))
BEGIN
    CREATE TABLE dbo.ChangeTrackingCursor
    (
        TableName sysname NOT NULL CONSTRAINT PK_ChangeTrackingCursor PRIMARY KEY,
        LastVersion bigint NOT NULL
    );
END";
    private const string SQL_GET_CURSOR = "SELECT LastVersion FROM dbo.ChangeTrackingCursor WHERE TableName = @tableName";
    private const string SQL_UPDATE_CURSOR = @"
MERGE dbo.ChangeTrackingCursor AS tgt
USING (VALUES (@tableName, @version)) AS s(TableName, LastVersion)
   ON tgt.TableName = s.TableName
WHEN MATCHED THEN UPDATE SET LastVersion = s.LastVersion
WHEN NOT MATCHED THEN INSERT(TableName, LastVersion) VALUES(s.TableName, s.LastVersion);";
    private const string SQL_GET_CURRENT_VERSION = "SELECT CHANGE_TRACKING_CURRENT_VERSION()";
    private const string SQL_GET_MIN_VALID_VERSION = "SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@objectName))";
    private const string SQL_READ_CHANGES_FORMAT = @"
SELECT ct.{0} AS Id, ct.SYS_CHANGE_VERSION, ct.SYS_CHANGE_OPERATION, ct.SYS_CHANGE_COLUMNS
FROM CHANGETABLE(CHANGES {1}.{2}, @lastVersion) AS ct
ORDER BY ct.SYS_CHANGE_VERSION ASC
OFFSET 0 ROWS FETCH NEXT @batchSize ROWS ONLY;";

    private const int DEFAULT_COMMAND_TIMEOUT = 30; // seconds
    private const string AZURE_SQL_SCOPE = "https://database.windows.net/.default";
    private const int TABLE_NAME_PARAM_SIZE = 128;
    private const int OBJECT_NAME_PARAM_SIZE = 256;

    private const string AAD_USER_UPN = "taras.korytnyuk@klimllc.com";
    private const string LEGACY_SQL_LOGIN = "im_api_admin";

    private readonly ILogger<ChangeTrackingPollingService> _log;
    private readonly ChangeTrackingOptions _opt;
    private readonly DatabaseOptions _databaseOpt;
    private readonly IEnumerable<IChangeEventProjector> _projectors;
    private readonly IOutboxWriter _outboxWriter;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ChangeTrackingPollingService(
        ILogger<ChangeTrackingPollingService> log,
        IOptions<ChangeTrackingOptions> opt,
        IOptions<DatabaseOptions> databaseOpt,
        IEnumerable<IChangeEventProjector> projectors,
        IOutboxWriter outboxWriter)
        => (_log, _opt, _databaseOpt, _projectors, _outboxWriter) = (log, opt.Value, databaseOpt.Value, projectors, outboxWriter);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_opt.PollingIntervalSeconds);
        if (string.IsNullOrWhiteSpace(_databaseOpt.ConnectionString))
        {
            _log.LogWarning("Database connection string empty; poller disabled.");
            return;
        }

        var cleanConnStr = SanitizeForAzureAd(_databaseOpt.ConnectionString, _databaseOpt.UseAzureAd);
        WarnIfLegacyLogin(cleanConnStr, _databaseOpt.UseAzureAd);

        _log.LogInformation("ChangeTracking poller started. Interval={Interval}s BatchSize={Batch} UseAzureAd={UseAzureAd} Tables={Tables}",
            _opt.PollingIntervalSeconds, _opt.BatchSize, _databaseOpt.UseAzureAd, string.Join(',', GetTrackedTables(_opt).Select(t => $"{t.Schema}.{t.Name}")));

        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollCycleAsync(cleanConnStr, _opt.BatchSize, _databaseOpt.UseAzureAd, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error during change tracking cycle");
            }

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        _log.LogInformation("ChangeTracking poller stopping");
    }

    private IEnumerable<TrackedTable> GetTrackedTables(ChangeTrackingOptions cfg)
    {
        if (cfg.Tables == null || cfg.Tables.Count == 0)
        {
            // Fallback default (existing behavior) if not configured
            yield return new TrackedTable("dbo", "Issuers", "IssuerID");
            yield return new TrackedTable("dbo", "Deals", "DealID");
            yield break;
        }
        foreach (var t in cfg.Tables)
            yield return new TrackedTable(t.Schema, t.Name, t.Pk);
    }

    private async Task PollCycleAsync(string connectionString, int batchSize, bool useAzureAd, CancellationToken ct)
    {
        await using var conn = CreateConnection(connectionString, useAzureAd, ct);
        await conn.OpenAsync(ct);
        await EnsureCursorTableAsync(conn, ct);

        var cfgTables = GetTrackedTables(_opt).ToList();
        foreach (var table in cfgTables)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessTableAsync(conn, table, batchSize, ct);
        }
    }

    private async Task ProcessTableAsync(SqlConnection conn, TrackedTable table, int batchSize, CancellationToken ct)
    {
        var full = $"{table.Schema}.{table.Name}";
        long last = await GetCursorAsync(conn, full, ct);
        long minValid = await GetMinValidVersionAsync(conn, table, ct);
        long current = await GetCurrentVersionAsync(conn, ct);

        if (last != 0 && last < minValid)
        {
            _log.LogWarning("Cursor for {Table} at {Last} below MIN_VALID_VERSION {Min}; resetting.", full, last, minValid);
            last = minValid;
        }

        if (last >= current)
        {
            _log.LogTrace("No new changes for {Table} (Watermark={W} Current={C})", full, last, current);
            return;
        }

        var changes = await ReadChangesAsync(conn, table, last, batchSize, ct);
        if (changes.Count == 0)
        {
            _log.LogTrace("No rows returned for {Table} despite version gap (W={W} C={C})", full, last, current);
            await UpdateCursorAsync(conn, full, current, ct); // advance to avoid rescanning gap
            return;
        }

        // Decode column masks once per change for update operations
        foreach (var rc in changes)
        {
            rc.DecodedColumns = rc.Operation == "U" ? await DecodeChangedColumnsAsync(conn, table, rc.ChangedColumns, ct) : rc.Operation == "I" ? new[] { "*" } : rc.Operation == "D" ? new[] { "__Deleted" } : Array.Empty<string>();
        }

        // Build batch context for projectors
        var batchContext = new TableChangeBatch(table.Schema, table.Name, table.PkColumn, changes.Select(c => new ProjectorRowChange(c.Id, c.Version, c.Operation, c.DecodedColumns!)).ToList());

        // Transactional: project -> insert outbox -> advance cursor
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var inserts = new List<OutboxInsert>();
            foreach (var projector in _projectors)
            {
                if (!projector.Supports(table.Schema, table.Name)) continue;
                var produced = await projector.ProjectAsync(conn, (SqlTransaction)tx, batchContext, ct);
                inserts.AddRange(produced);
            }

            if (inserts.Count > 0)
            {
                await _outboxWriter.InsertManyAsync(conn, (SqlTransaction)tx, inserts, ct);
                _log.LogInformation("Inserted {Count} outbox messages for {Table}", inserts.Count, full);
            }
            else
            {
                _log.LogDebug("No projector output for {Table} (changes={ChangeCount})", full, changes.Count);
            }

            long newCursor = changes[^1].Version;
            await UpdateCursorAsync(conn, (SqlTransaction)tx, full, newCursor, ct);
            await tx.CommitAsync(ct);
            _log.LogDebug("Advanced cursor for {Table} to {Ver}", full, newCursor);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(CancellationToken.None);
            _log.LogError(ex, "Projection failed for {Table}; rolled back.", full);
            throw;
        }
    }

    private async Task<string[]> DecodeChangedColumnsAsync(SqlConnection conn, TrackedTable table, byte[]? mask, CancellationToken ct)
    {
        if (mask == null || mask.Length == 0) return Array.Empty<string>();
        
        const string COL_SQL = @"SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@obj) ORDER BY column_id";
        var cacheKey = $"{table.Schema}.{table.Name}";
        if (!_columnCache.TryGetValue(cacheKey, out var cols))
        {
            await using var cmd = new SqlCommand(COL_SQL, conn);
            cmd.Parameters.AddWithValue("@obj", cacheKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<string>();
            while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
            cols = list.ToArray();
            _columnCache[cacheKey] = cols;
        }

        // SQL Server Change Tracking stores column IDs as 4-byte integers
        var changed = new List<string>();
        
        for (int i = 0; i < mask.Length; i += 4)
        {
            if (i + 3 >= mask.Length) break;
            
            // Read 4-byte integer (little-endian)
            int columnId = BitConverter.ToInt32(mask, i);
            
            // Column IDs are 1-based, our array is 0-based
            if (columnId > 0 && columnId <= cols.Length)
            {
                changed.Add(cols[columnId - 1]);
            }
        }
            
        return changed.ToArray();
    }

    private readonly Dictionary<string, string[]> _columnCache = new();

    private void WarnIfLegacyLogin(string connectionString, bool useAzureAd)
    {
        if (useAzureAd) return;
        var csBuilder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(csBuilder.UserID) && csBuilder.UserID.Equals(LEGACY_SQL_LOGIN, StringComparison.OrdinalIgnoreCase))
            _log.LogWarning("Connection string uses legacy SQL login '{Login}'. Set Database:UseAzureAd=true for AAD.", LEGACY_SQL_LOGIN);
    }

    private SqlConnection CreateConnection(string connectionString, bool useAzureAd, CancellationToken ct)
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
            conn.AccessToken = AcquireAzureAdToken(ct);
        }
        
        return conn;
    }

    private string AcquireAzureAdToken(CancellationToken ct)
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeSharedTokenCacheCredential = false,
            ExcludeVisualStudioCredential = false,
            ExcludeManagedIdentityCredential = false,
            ExcludeAzureCliCredential = false,
            ExcludeEnvironmentCredential = false,
            ExcludeVisualStudioCodeCredential = false
        });
        var token = credential.GetToken(new TokenRequestContext(new[] { AZURE_SQL_SCOPE }), ct);
        return token.Token;
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string commandText, SqlTransaction? transaction = null)
        => new(commandText, connection, transaction) { CommandTimeout = DEFAULT_COMMAND_TIMEOUT };

    private static void AddParameter(SqlCommand cmd, string name, SqlDbType type, int size, object value)
        => cmd.Parameters.Add(new SqlParameter(name, type, size) { Value = value });

    private static void AddParameter(SqlCommand cmd, string name, SqlDbType type, object value)
        => cmd.Parameters.Add(new SqlParameter(name, type) { Value = value });

    private static string SanitizeForAzureAd(string connectionString, bool useAzureAd)
    {
        if (!useAzureAd) return connectionString;
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Remove("User ID");
        builder.Remove("Password");
        return builder.ConnectionString;
    }

    private static async Task EnsureCursorTableAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_ENSURE_CURSOR_TABLE);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> GetCursorAsync(SqlConnection connection, string tableName, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_GET_CURSOR);
        AddParameter(cmd, "@tableName", SqlDbType.NVarChar, TABLE_NAME_PARAM_SIZE, tableName);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null || result is DBNull ? 0L : (long)result;
    }

    private static async Task UpdateCursorAsync(SqlConnection connection, string tableName, long version, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_UPDATE_CURSOR); // no ambient transaction
        AddParameter(cmd, "@tableName", SqlDbType.NVarChar, TABLE_NAME_PARAM_SIZE, tableName);
        AddParameter(cmd, "@version", SqlDbType.BigInt, version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpdateCursorAsync(SqlConnection connection, SqlTransaction tx, string tableName, long version, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_UPDATE_CURSOR, tx); // enlist in explicit transaction
        AddParameter(cmd, "@tableName", SqlDbType.NVarChar, TABLE_NAME_PARAM_SIZE, tableName);
        AddParameter(cmd, "@version", SqlDbType.BigInt, version);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> GetCurrentVersionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_GET_CURRENT_VERSION);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null ? 0L : (long)result;
    }

    private static async Task<long> GetMinValidVersionAsync(SqlConnection connection, TrackedTable table, CancellationToken ct)
    {
        await using var cmd = CreateCommand(connection, SQL_GET_MIN_VALID_VERSION);
        AddParameter(cmd, "@objectName", SqlDbType.NVarChar, OBJECT_NAME_PARAM_SIZE, $"{table.Schema}.{table.Name}");
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null || result is DBNull ? 0L : (long)result;
    }

    private static async Task<List<RawRowChange>> ReadChangesAsync(SqlConnection connection, TrackedTable table, long lastVersion, int batchSize, CancellationToken ct)
    {
        string sql = string.Format(SQL_READ_CHANGES_FORMAT, table.PkColumn, table.Schema, table.Name);
        var list = new List<RawRowChange>();
        await using var cmd = CreateCommand(connection, sql);
        AddParameter(cmd, "@lastVersion", SqlDbType.BigInt, lastVersion);
        AddParameter(cmd, "@batchSize", SqlDbType.Int, batchSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new RawRowChange(
                Id: reader["Id"],
                Version: (long)reader["SYS_CHANGE_VERSION"],
                Operation: (string)reader["SYS_CHANGE_OPERATION"]!,
                ChangedColumns: reader["SYS_CHANGE_COLUMNS"] as byte[]
            ));
        }
        return list;
    }

    private sealed record TrackedTable(string Schema, string Name, string PkColumn);
    private sealed record RawRowChange(object Id, long Version, string Operation, byte[]? ChangedColumns)
    {
        public string[]? DecodedColumns { get; set; }
    }

    // Projector-facing immutable row representation
    public sealed record ProjectorRowChange(object Id, long Version, string Operation, string[] ChangedColumns);

    public sealed record TableChangeBatch(string Schema, string Table, string PkColumn, IReadOnlyList<ProjectorRowChange> Changes);
}
