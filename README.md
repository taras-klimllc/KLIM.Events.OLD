# KLIM.Events

A .NET 8 worker service that publishes generic data change events from a SQL Server (Change Tracking enabled) to RabbitMQ using MassTransit. Features production-ready outbox pattern implementation, comprehensive structured logging, health probes, retry policies, correlation propagation, and real-time diagnostics.

## Table of Contents
1. [Overview](#overview)
2. [Architecture & Components](#architecture-components)
3. [Message Contracts](#message-contracts)
4. [Messaging Topology & Retry](#messaging-topology-retry)
5. [Correlation & Logging](#correlation-logging)
6. [Outbox Pattern Implementation](#outbox-pattern)
7. [Enhanced Observability](#enhanced-observability)
8. [Health Endpoints](#health-endpoints)
9. [SQL Change Tracking Enablement](#sql-change-tracking)
10. [Running Locally (Docker Compose)](#running-locally)
11. [Run Instructions (Service)](#run-instructions)
12. [Configuration](#configuration)
13. [Adding New Tables](#adding-new-tables)
14. [File Map / Source Links](#file-map)
15. [Message Flow Architecture](#message-flow)
16. [Troubleshooting & Diagnostics](#troubleshooting)
17. [Secure Configuration (Secrets)](#secure-configuration)
18. [Versioning Strategy](#versioning-strategy)
19. [Security Notes](#security-notes)
20. [Performance & Scalability](#performance-scalability)

---
<a id="overview"></a>
## 1. Overview <a href="#table-of-contents" style="float:right;">↑</a>

The service provides a **production-ready outbox pattern implementation** that polls a SQL Server database for entity changes (via SQL Change Tracking), converts them into immutable versioned events, and publishes them to a RabbitMQ topic exchange (`klim.change.events`). 

**Key Features:**
- ✅ **Generic entity support** - Handles Issuers, Deals, and future entity types
- ✅ **Complete outbox implementation** with transactional guarantees
- 🔍 **Real-time diagnostics** and comprehensive logging
- 📊 **Structured observability** with detailed change tracking  
- 🏥 **Health monitoring** with separate liveness/readiness probes
- 🔄 **Adaptive polling** and concurrent message processing
- 🛡️ **Azure AD authentication** support for SQL connections
- 📈 **Performance optimized** with batching and connection pooling

---
<a id="architecture-components"></a>
## 2. Architecture & Components <a href="#table-of-contents" style="float:right;">↑</a>

### Clean Modular Architecture
The service implements a clean, extensible architecture with focused components:

#### Core Services Layer:
- **[`OutboxDispatcherService`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxDispatcherService.cs)** — Orchestrates outbox message dispatch with batching and concurrency
- **[`OutboxRepository`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxRepository.cs)** — Database operations with connection management
- **[`MessagePublisher`](src/KLIM.Events.Service/Infrastructure/Outbox/MessagePublisher.cs)** — RabbitMQ publishing via MassTransit
- **[`SqlAuthenticationService`](src/KLIM.Events.Service/Infrastructure/Outbox/SqlAuthenticationService.cs)** — Azure AD token management

#### Change Tracking Layer:
- **[`ChangeTrackingPollingService`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/ChangeTrackingPollingService.cs)** — SQL Change Tracking poller with watermark management
- **[`IssuerProjector`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/IssuerProjector.cs)** — Projects `dbo.Issuers` changes to `DataChangedV1` events
- **[`DealProjector`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/DealProjector.cs)** — Projects `dbo.Deals` changes to `DataChangedV1` events
- **[`InstrumentProjector`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/InstrumentProjector.cs)** — Projects `dbo.InstrumentMaster` changes to `DataChangedV1` events
- **[`GenericDomainChangeProjector`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/GenericDomainChangeProjector.cs)** — Fallback projector for future tables

#### Observability Layer:
- **[`OutboxDiagnosticService`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxDiagnosticService.cs)** — Real-time system health monitoring
- **[`OutboxLoggerExtensions`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxLoggerExtensions.cs)** — Structured logging with rich context
- **[`CorrelationConsumeFilter`](src/KLIM.Events.Service/Logging/CorrelationConsumeFilter.cs)** — Distributed tracing correlation
- **[`HealthEndpointHostService`](src/KLIM.Events.Service/Infrastructure/HealthChecks/HealthEndpointHostService.cs)** — Dedicated health probe host

---
<a id="message-contracts"></a>
## 3. Message Contracts <a href="#table-of-contents" style="float:right;">↑</a>

#### Primary Contract
**[`DataChangedV1`](src/KLIM.Events.Service/Messaging/Contracts/DataChangedV1.cs)** — Generic change event supporting multiple entity types:

```csharp
public sealed record DataChangedV1(
    Guid EntityId,            // Stable identifier (RowGUID)
    string EntityType,        // Entity type: "Issuer", "Deal", etc.
    string DisplayName,       // Primary display identifier
    long ChangeVersion,       // SQL Change Tracking version
    DateTimeOffset ChangedAt, // UTC timestamp
    string ChangeSource,      // Source table (e.g., "dbo.Issuers")
    string Operation,         // I/U/D (Insert/Update/Delete)
    string[] ChangedFields,   // Changed column names
    Dictionary<string, object?>? PreImage,  // Before state
    Dictionary<string, object?>? PostImage, // After state
    Guid? CorrelationId = null
);
```

#### Downstream Contract
**[`DataChangeProcessed`](src/KLIM.Events.Service/Messaging/Contracts/DataChangedV1.cs)** — Simplified notification for downstream consumers:

```csharp
public sealed record DataChangeProcessed(
    Guid EntityId,
    string EntityType,
    string DisplayName,
    long ChangeVersion,
    DateTimeOffset ChangedAt,
    string Operation,
    string ChangeSource,
    string[] Fields,
    DateTimeOffset ProcessedAt,
    int DeliveryCount
);
```

**Versioning Policy:**
- `V{n}` suffix for explicit version management
- Additive-only changes (no breaking mutations)
- Multiple versions can coexist during migrations

---
<a id="messaging-topology-retry"></a>
## 4. Messaging Topology & Retry <a href="#table-of-contents" style="float:right;">↑</a>

#### RabbitMQ Configuration
- **Exchange**: `klim.change.events` (topic)
- **Queue**: `klim.events.datachanged.v1`
- **Routing Key**: `data.changed.v1`
- **Dead Letter Queue**: `klim.events.datachanged.v1.dlq`
- **Consumer**: [`DataChangedConsumer`](src/KLIM.Events.Service/Messaging/Consumers/DataChangedConsumer.cs)

#### Entity Type Mappings
| Table | EntityType | DisplayName Logic |
|-------|------------|-------------------|
| `dbo.Issuers` | "Issuer" | `IssuerTicker` ?? `IssuerName` |
| `dbo.Deals` | "Deal" | `ShortName` ?? `DealName` |
| `dbo.InstrumentMaster` | "Instrument" | `Ticker` ?? `CUSIP` ?? `ISIN` with `InstrumentName` |
| Other tables | Dynamic | Table-specific logic |

#### Retry Policy (MassTransit)
```csharp
cfg.UseMessageRetry(r =>
{
    r.Immediate(3);           // 3 immediate retries
    r.Exponential(5,          // 5 exponential retries
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 
        TimeSpan.FromSeconds(5));
    r.Ignore<ArgumentException>(); // Skip validation errors
});
```

---
<a id="correlation-logging"></a>
## 5. Correlation & Logging <a href="#table-of-contents" style="float:right;">↑</a>

### Enhanced Structured Logging
Comprehensive logging system with multiple sinks and structured properties:

#### Serilog Configuration
```json
{
  "Serilog": {
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "logs/outbox-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 10
        }
      }
    ]
  }
}
```

#### Correlation Tracking
**Resolution order** (first non-empty wins):
1. `ConsumeContext.CorrelationId` 
2. Message property `CorrelationId` (Guid/string)
3. `ConversationId`

Automatically injected into all log entries within message processing scope.

---
<a id="outbox-pattern"></a>
## 6. Outbox Pattern Implementation <a href="#table-of-contents" style="float:right;">↑</a>

### ✅ Production-Ready Implementation
**Status**: **Fully Implemented** with complete transactional guarantees

#### Complete Outbox Table Schema
```sql
CREATE TABLE dbo.OutboxMessages (
    Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Type NVARCHAR(400) NOT NULL,
    Payload NVARCHAR(MAX) NOT NULL,
    Headers NVARCHAR(MAX) NULL,
    OccurredAt DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    DispatchedAt DATETIME2(7) NULL,
    MessageKey NVARCHAR(256) NULL,      -- Deduplication key
    SourceEntity NVARCHAR(128) NULL,    -- Source table name
    SourceId NVARCHAR(128) NULL,        -- Entity identifier
    ChangeVersion BIGINT NULL           -- SQL Change Tracking version
);
```

#### Features Implemented
- ✅ **Transactional guarantees** — Change detection and outbox insert in single transaction
- ✅ **Deduplication** — Unique constraint on MessageKey prevents duplicates
- ✅ **Batch processing** — Configurable batch sizes for optimal performance
- ✅ **Concurrent dispatch** — Configurable parallelism with semaphore control
- ✅ **Adaptive polling** — Dynamic interval adjustment based on message availability
- ✅ **Automatic cleanup** — Configurable retention policy for dispatched messages
- ✅ **Connection management** — Proper connection lifecycle and Azure AD support

#### Two-Phase Architecture
1. **[`ChangeTrackingPollingService`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/ChangeTrackingPollingService.cs)** — Detects changes → populates outbox
2. **[`OutboxDispatcherService`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxDispatcherService.cs)** — Dispatches messages → marks as sent

---
<a id="enhanced-observability"></a>
## 7. Enhanced Observability <a href="#table-of-contents" style="float:right;">↑</a>

### Real-Time Diagnostics
**[`OutboxDiagnosticService`](src/KLIM.Events.Service/Infrastructure/Outbox/OutboxDiagnosticService.cs)** provides automated monitoring every 5 minutes:

#### Sample Diagnostic Output
```
🔍 OUTBOX STATUS: Pending=5 | Dispatched=120 | Total=125 | Enabled=True
📋 RECENT MESSAGES:
  ✅ SENT DataChangedV1 | dbo.Issuers#12345 | 14:30:15
  ⏳ PENDING DataChangedV1 | dbo.Deals#67890 | 14:30:20
```

### Structured Message Logging
Each dispatched message generates comprehensive logs:

#### Success Logging
```
✅ OUTBOX DISPATCH SUCCESS: a1b2c3d4-e5f6... | Type: DataChangedV1 | 
Exchange: klim.change.events | RoutingKey: data.changed.v1 | 
Source: dbo.Issuers#12345 | ChangeVersion: 98765 | Duration: 23ms
```

#### Change Details
```
📋 CHANGE DETAILS: a1b2c3d4-e5f6... | U | Issuer: 12345-guid (AAPL) | 
Changed: [IssuerName, LastUpdated] | PreImage[IssuerName: Apple Inc] | PostImage[IssuerName: Apple Inc.]
```

#### Batch Statistics
```
📊 BATCH PROCESSED: 15 messages | ✅ 14 success | ❌ 1 errors | 
Total: 250ms | Avg: 16ms/msg
```

### Structured Properties
All log entries include searchable properties:
- `MessageId`, `Exchange`, `RoutingKey`, `Operation`
- `EntityId`, `EntityType`, `DisplayName`, `ChangedColumns` 
- `Duration`, `BatchSize`, `SuccessCount`

---
<a id="health-endpoints"></a>
## 8. Health Endpoints <a href="#table-of-contents" style="float:right;">↑</a>

Hosted separately by [`HealthEndpointHostService`](src/KLIM.Events.Service/Infrastructure/HealthChecks/HealthEndpointHostService.cs):

#### Endpoints
- **Liveness**: `GET /health/live` — Service is running
- **Readiness**: `GET /health/ready` — Dependencies are healthy

#### Health Checks
- ✅ **SQL Server connectivity** (with Azure AD support)
- ✅ **RabbitMQ connectivity** 
- ✅ **Outbox table existence**
- ✅ **Change tracking configuration**

Port configurable via `Health:Port` (defaults to 8080).

---
<a id="sql-change-tracking"></a>
## 9. SQL Change Tracking Enablement <a href="#table-of-contents" style="float:right;">↑</a>

#### Required Setup
Script: [`scripts/sql/enable_change_tracking.sql`](scripts/sql/enable_change_tracking.sql)
```sql
-- Enable database-level change tracking
ALTER DATABASE [KLIM_IM_TK] SET CHANGE_TRACKING = ON 
(CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON);

-- Enable table-level tracking with column tracking
ALTER TABLE dbo.Issuers ENABLE CHANGE_TRACKING 
WITH (TRACK_COLUMNS_UPDATED = ON);

ALTER TABLE dbo.Deals ENABLE CHANGE_TRACKING 
WITH (TRACK_COLUMNS_UPDATED = ON);
```

#### Automatic Schema Management
The service automatically:
- ✅ Creates outbox table and indexes
- ✅ Manages watermark cursors per table
- ✅ Handles version gaps and retention cleanup
- ✅ Validates minimum valid versions

---
<a id="running-locally"></a>
## 10. Running Locally (Docker Compose) <a href="#table-of-contents" style="float:right;">↑</a>

#### Infrastructure Stack
File: [`docker-compose.yml`](docker-compose.yml)
- **RabbitMQ 4.0** (Management UI: :15672, guest/guest)
- **SQL Server 2022** (:1433)

```bash
docker compose up -d
```

RabbitMQ topology imported automatically via: [`ops/rabbitmq/definitions.json`](ops/rabbitmq/definitions.json)

---
<a id="run-instructions"></a>
## 11. Run Instructions (Service) <a href="#table-of-contents" style="float:right;">↑</a>

### Prerequisites
- .NET 8 SDK
- Running RabbitMQ & SQL Server
- Connection string configuration

### Local Development Setup
```bash
# Initialize user secrets
dotnet user-secrets init --project src/KLIM.Events.Service/KLIM.Events.Service.csproj

# Set connection string (replace with actual values)
dotnet user-secrets set "ChangeTracking:ConnectionString" \
  "Data Source=localhost;Database=KLIM_IM_TK;Integrated Security=true;MultipleActiveResultSets=True"

# Build and run
dotnet build
dotnet run --project src/KLIM.Events.Service/KLIM.Events.Service.csproj
```

### Verification Steps
1. ✅ **Logs show**: "MassTransit bus started"
2. ✅ **Logs show**: "Outbox initialized. Database: KLIM_IM_TK"
3. ✅ **Health check**: `http://localhost:8080/health/live` (200 OK)
4. ✅ **Health check**: `http://localhost:8080/health/ready` (200 OK)
5. ✅ **Diagnostics**: Look for "🔍 OUTBOX STATUS" logs every 5 minutes

---
<a id="configuration"></a>
## 12. Configuration <a href="#table-of-contents" style="float:right;">↑</a>

### Enhanced Configuration Structure
```json
{
  "ChangeTracking": {
    "ConnectionString": "...",
    "PollingIntervalSeconds": 5,
    "BatchSize": 500,
    "UseAzureAd": true,
    "Tables": [
      { "Schema": "dbo", "Name": "Issuers", "Pk": "IssuerID" },
      { "Schema": "dbo", "Name": "Deals", "Pk": "DealID" }
    ]
  },
  "MassTransit": {
    "RabbitMQ": {
      "Host": "localhost", "Port": 5672,
      "VirtualHost": "/", "Username": "guest", "Password": "guest"
    },
    "Outbox": {
      "Enabled": true,
      "DeliveryIntervalSeconds": 2,
      "BatchSize": 100,
      "MaxConcurrentDispatches": 10,
      "RetentionDays": 7,
      "CleanupIntervalHours": 6,
      "UseAzureAd": false
    }
  }
}
```

### Environment Variables
Primary connection string: `ChangeTracking__ConnectionString`

---
<a id="adding-new-tables"></a>
## 13. Adding New Tables <a href="#table-of-contents" style="float:right;">↑</a>

The KLIM.Events service uses a **modular projector architecture** that makes adding new tables straightforward. You have two options depending on your requirements:

### 🚀 **Quick Setup (Generic Support)**
For basic change tracking without custom logic, simply **add to configuration** - no code changes needed:

#### Step 1: Update Configuration
Add the new table to your `appsettings.json` or configuration source:
```json
{
  "ChangeTracking": {
    "Tables": [
      { "Schema": "dbo", "Name": "Issuers", "Pk": "IssuerID" },
      { "Schema": "dbo", "Name": "Deals", "Pk": "DealID" },
      { "Schema": "dbo", "Name": "InstrumentMaster", "Pk": "InstrumentID" },
      { "Schema": "dbo", "Name": "Users", "Pk": "UserID" }
    ]
  }
}
```

#### Step 2: Enable SQL Change Tracking
```sql
-- Enable change tracking for the new table
ALTER TABLE dbo.InstrumentMaster ENABLE CHANGE_TRACKING 
WITH (TRACK_COLUMNS_UPDATED = ON);

ALTER TABLE dbo.Users ENABLE CHANGE_TRACKING 
WITH (TRACK_COLUMNS_UPDATED = ON);
```

#### ✅ **Result**: 
- Tables tracked automatically using [`GenericDomainChangeProjector`](src/KLIM.Events.Service/Infrastructure/ChangeTracking/GenericDomainChangeProjector.cs)
- Basic `DomainChangeNotification` events published
- No custom DisplayName or entity-specific logic

---

### 🎯 **Custom Projector (Full Control)**
For rich domain events with custom logic, DisplayName rules, PreImage/PostImage data:

#### Step 1: Enable SQL Change Tracking
```sql
-- Enable change tracking for your table
ALTER TABLE dbo.InstrumentMaster ENABLE CHANGE_TRACKING 
WITH (TRACK_COLUMNS_UPDATED = ON);
```

#### Step 2: Create Entity-Specific Projector
Create `src/KLIM.Events.Service/Infrastructure/ChangeTracking/InstrumentProjector.cs`:

```csharp
using KLIM.Events.Messaging.Contracts;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace KLIM.Events.Service.Infrastructure.ChangeTracking;

/// <summary>
/// Projector for Instrument entities - converts raw change tracking data into DataChangedV1 events
/// </summary>
public sealed class InstrumentProjector : IChangeEventProjector
{
    private static readonly string EventType = typeof(DataChangedV1).AssemblyQualifiedName!;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // SQL queries to load current and historical data
    private const string SQL_INSTRUMENT_CURRENT = "SELECT RowGUID, InstrumentName, Ticker, CUSIP, ISIN FROM dbo.InstrumentMaster WHERE InstrumentID = @id";
    private const string SQL_INSTRUMENT_HISTORY_WINDOW = @"SELECT TOP (2) RowGUID, InstrumentName, Ticker, CUSIP, ISIN, ValidFrom
FROM dbo.InstrumentMaster FOR SYSTEM_TIME ALL
WHERE InstrumentID = @id
ORDER BY ValidFrom DESC"; // newest then prior

    public bool Supports(string schema, string table)
        => (schema, table) is ("dbo", "InstrumentMaster");

    public async Task<IEnumerable<OutboxInsert>> ProjectAsync(SqlConnection connection, SqlTransaction tx, ChangeTrackingPollingService.TableChangeBatch batch, CancellationToken ct)
    {
        var inserts = new List<OutboxInsert>(batch.Changes.Count);
        foreach (var change in batch.Changes)
        {
            var op = change.Operation;
            Guid rowGuid = Guid.Empty;
            string displayName = string.Empty;
            Dictionary<string, object?>? preImage = null;
            Dictionary<string, object?>? postImage = null;

            if (op == "D") // Delete operation
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    var last = hist[0];
                    rowGuid = last.RowGuid;
                    displayName = $"{last.Ticker ?? last.CUSIP ?? last.ISIN} ({last.InstrumentName})" ?? $"DELETED-INSTRUMENT-{change.Id}";
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["InstrumentName"] = last.InstrumentName,
                        ["Ticker"] = last.Ticker,
                        ["CUSIP"] = last.CUSIP,
                        ["ISIN"] = last.ISIN
                    };
                }
                else
                {
                    displayName = $"DELETED-INSTRUMENT-{change.Id}";
                }
            }
            else if (op == "I") // Insert operation
            {
                (rowGuid, var nullableDisplayName) = await LoadCurrentAsync(connection, tx, change.Id, ct);
                displayName = nullableDisplayName ?? $"INSTRUMENT-{change.Id}";
                postImage = new Dictionary<string, object?>
                {
                    ["RowGUID"] = rowGuid,
                    ["DisplayName"] = displayName
                };
            }
            else if (op == "U") // Update operation
            {
                var hist = await LoadHistoryWindowAsync(connection, tx, change.Id, ct);
                if (hist.Count > 0)
                {
                    var last = hist[0];
                    rowGuid = last.RowGuid;
                    displayName = $"{last.Ticker ?? last.CUSIP ?? last.ISIN} ({last.InstrumentName})" ?? $"INSTRUMENT-{change.Id}";
                    postImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = last.RowGuid,
                        ["InstrumentName"] = last.InstrumentName,
                        ["Ticker"] = last.Ticker,
                        ["CUSIP"] = last.CUSIP,
                        ["ISIN"] = last.ISIN
                    };
                }
                if (hist.Count > 1)
                {
                    var prev = hist[1];
                    preImage = new Dictionary<string, object?>
                    {
                        ["RowGUID"] = prev.RowGuid,
                        ["InstrumentName"] = prev.InstrumentName,
                        ["Ticker"] = prev.Ticker,
                        ["CUSIP"] = prev.CUSIP,
                        ["ISIN"] = prev.ISIN
                    };
                }
            }

            // Fallback handling
            if (rowGuid == Guid.Empty)
                rowGuid = Guid.NewGuid(); // fallback
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = $"INSTRUMENT-{change.Id}";

            var changedFields = op switch
            {
                "I" => new[] { "*" },
                "U" => change.ChangedColumns,
                "D" => new[] { "__Deleted" },
                _ => Array.Empty<string>()
            };

            // Create the domain event
            var now = DateTimeOffset.UtcNow;
            var evt = new DataChangedV1(
                EntityId: rowGuid,
                EntityType: "Instrument", // Your entity type
                DisplayName: displayName,
                ChangeVersion: change.Version,
                ChangedAt: now,
                ChangeSource: $"{batch.Schema}.{batch.Table}",
                Operation: op,
                ChangedFields: changedFields,
                PreImage: preImage,
                PostImage: postImage,
                CorrelationId: null
            );

            var payload = JsonSerializer.Serialize(evt, _json);
            inserts.Add(new OutboxInsert(
                Id: Guid.NewGuid(),
                Type: EventType,
                Payload: payload,
                OccurredAt: now.UtcDateTime,
                MessageKey: $"DataChangedV1|{evt.EntityType}|{evt.EntityId}|{batch.Schema}.{batch.Table}|{change.Version}|{op}",
                SourceEntity: $"{batch.Schema}.{batch.Table}",
                SourceId: evt.EntityId.ToString(),
                ChangeVersion: change.Version));
        }
        return inserts;
    }

    // Helper methods to load current and historical data
    private static async Task<(Guid RowGuid, string? DisplayName)> LoadCurrentAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(SQL_INSTRUMENT_CURRENT, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        if (await rdr.ReadAsync(ct))
        {
            var guid = rdr.GetGuid(0);
            var instrumentName = rdr.IsDBNull(1) ? null : rdr.GetString(1);
            var ticker = rdr.IsDBNull(2) ? null : rdr.GetString(2);
            var cusip = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            var isin = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            var identifier = ticker ?? cusip ?? isin ?? "N/A";
            return (guid, $"{identifier} ({instrumentName})");
        }
        return (Guid.Empty, null);
    }

    private static async Task<List<HistRow>> LoadHistoryWindowAsync(SqlConnection conn, SqlTransaction tx, object pk, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(SQL_INSTRUMENT_HISTORY_WINDOW, conn, tx);
        cmd.Parameters.AddWithValue("@id", pk);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<HistRow>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new HistRow(
                RowGuid: rdr.GetGuid(0),
                InstrumentName: rdr.IsDBNull(1) ? null : rdr.GetString(1),
                Ticker: rdr.IsDBNull(2) ? null : rdr.GetString(2),
                CUSIP: rdr.IsDBNull(3) ? null : rdr.GetString(3),
                ISIN: rdr.IsDBNull(4) ? null : rdr.GetString(4)
            ));
        }
        return list;
    }

    private sealed record HistRow(Guid RowGuid, string? InstrumentName, string? Ticker, string? CUSIP, string? ISIN);
}
```

#### Step 3: Register the Projector
Add to [`Program.cs`](src/KLIM.Events.Service/Program.cs):
```csharp
// Change tracking infrastructure - specialized projectors for each entity type
builder.Services.AddSingleton<IOutboxWriter, SqlOutboxWriter>();
builder.Services.AddSingleton<IChangeEventProjector, IssuerProjector>();
builder.Services.AddSingleton<IChangeEventProjector, DealProjector>();
builder.Services.AddSingleton<IChangeEventProjector, InstrumentProjector>(); // Add your projector
builder.Services.AddSingleton<IChangeEventProjector, GenericDomainChangeProjector>();
```

#### Step 4: Update Configuration
Add the table to `appsettings.json`:
```json
{
  "ChangeTracking": {
    "Tables": [
      { "Schema": "dbo", "Name": "Issuers", "Pk": "IssuerID" },
      { "Schema": "dbo", "Name": "Deals", "Pk": "DealID" },
      { "Schema": "dbo", "Name": "InstrumentMaster", "Pk": "InstrumentID" }
    ]
  }
}
```

#### Step 5: Test & Deploy
```bash
# Build and verify
dotnet build

# Run locally to test
dotnet run --project src/KLIM.Events.Service/KLIM.Events.Service.csproj

# Check logs for your entity type
grep "EntityType.*Instrument" logs/outbox-*.log
```

---

### 📋 **Design Guidelines**

#### **Entity-Specific Projector Best Practices**
- ✅ **Single Responsibility** — One projector per entity type
- ✅ **Meaningful DisplayName** — Use business-friendly identifiers (e.g., "AAPL (Apple Inc.)", "US037833100 (Apple Inc)")
- ✅ **Rich PreImage/PostImage** — Include relevant fields for downstream processing
- ✅ **Consistent EntityType** — Use singular nouns ("Instrument", "User", "Order")
- ✅ **Fallback Logic** — Handle missing data gracefully with fallback DisplayNames
- ✅ **Error Handling** — Log warnings for data inconsistencies but don't fail the batch

#### **SQL Query Patterns**
```csharp
// Current data (for Insert/Update)
private const string SQL_CURRENT = "SELECT RowGUID, Field1, Field2 FROM dbo.YourTable WHERE YourTableID = @id";

// Historical data (for Update/Delete with temporal tables)
private const string SQL_HISTORY_WINDOW = @"SELECT TOP (2) RowGUID, Field1, Field2, ValidFrom
FROM dbo.YourTable FOR SYSTEM_TIME ALL
WHERE YourTableID = @id
ORDER BY ValidFrom DESC"; // newest then prior
```

#### **Message Key Pattern**
Always use this format for deduplication:
```csharp
MessageKey: $"DataChangedV1|{evt.EntityType}|{evt.EntityId}|{batch.Schema}.{batch.Table}|{change.Version}|{op}"
```

---
<a id="troubleshooting"></a>
## 16. Troubleshooting & Diagnostics <a href="#table-of-contents" style="float:right;">↑</a>

### Automated Diagnostics
The service provides comprehensive self-monitoring:

#### Real-Time Status (Every 5 Minutes)
```
🔍 OUTBOX STATUS: Pending=0 | Dispatched=250 | Total=250 | Enabled=True
```

#### No Messages Scenarios
```
⚠️  NO MESSAGES FOUND - Check if ChangeTrackingPollingService is populating outbox
```

### Log-Based Troubleshooting

#### Filter by Message Flow
```bash
# View all outbox activity
grep "OUTBOX" logs/outbox-*.log

# Check for specific entity changes
grep "EntityType.*Issuer" logs/outbox-*.log
grep "EntityType.*Deal" logs/outbox-*.log
grep "EntityType.*Instrument" logs/outbox-*.log

# Monitor batch processing
grep "BATCH PROCESSED" logs/outbox-*.log

# Track errors
grep "ERROR" logs/outbox-*.log
```

#### Structured Property Searches
- **By Exchange**: `Exchange = "klim.change.events"`
- **By EntityType**: `EntityType = "Issuer"` or `EntityType = "Deal"` or `EntityType = "Instrument"`
- **By Operation**: `Operation = "U"`
- **By Performance**: `DurationMs > 100`
- **By Column Changes**: `ChangedColumns CONTAINS "Name"`

### Common Resolution Steps
1. **No Messages**: Check change tracking enabled on tables
2. **Health Failures**: Verify SQL/RabbitMQ connectivity
3. **Performance Issues**: Review batch sizes and concurrency settings
4. **Authentication**: Ensure Azure AD tokens or SQL credentials valid
5. **Entity Issues**: Verify projectors are registered and table configuration is correct

### Debug Configuration
For enhanced debugging, set in `appsettings.Development.json`:
```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "KLIM.Events.Service.Infrastructure.Outbox": "Debug",
        "KLIM.Events.Service.Infrastructure.ChangeTracking": "Debug"
      }
    }
  }
}
```

---
<a id="secure-configuration"></a>
## 17. Secure Configuration (Secrets) <a href="#table-of-contents" style="float:right;">↑</a>

### Connection String Security
- ✅ **User Secrets** for local development
- ✅ **Environment Variables** for production
- ✅ **Azure Key Vault** integration ready
- ✅ **Azure AD authentication** eliminates password storage

### Never Commit
- Database connection strings
- RabbitMQ credentials  
- Azure AD client secrets
- Any authentication tokens

---
<a id="versioning-strategy"></a>
## 18. Versioning Strategy <a href="#table-of-contents" style="float:right;">↑</a>

### Contract Evolution
- **Explicit Versioning**: `DataChangedV1`, `DataChangedV2`
- **Additive Changes**: Only add fields, never remove or change semantics
- **Coexistence**: Multiple versions can run simultaneously during migrations
- **Routing Keys**: Include version in routing (`data.changed.v1`)

### Entity Type Evolution
- New entity types can be added without breaking changes
- Entity-specific projectors can evolve independently
- Generic projector provides fallback for new tables

---
<a id="security-notes"></a>
## 19. Security Notes <a href="#table-of-contents" style="float:right;">↑</a>

### Data Protection
- ✅ **No PII in logs** — Only identifiers and metadata logged
- ✅ **Secure connections** — TLS for RabbitMQ and SQL Server
- ✅ **Azure AD integration** — Token-based authentication
- ✅ **Minimal permissions** — Service accounts with least privilege

### Production Considerations
- Protect health endpoints with authentication
- Use Azure Key Vault for secrets management
- Enable audit logging for compliance
- Regular security reviews of dependencies

---
<a id="performance-scalability"></a>
## 20. Performance & Scalability <a href="#table-of-contents" style="float:right;">↑</a>

### Optimizations Implemented
- ✅ **Adaptive Polling** — Speeds up when messages available, backs off when idle
- ✅ **Batch Processing** — Configurable batch sizes (default: 100-500 messages)
- ✅ **Concurrent Dispatch** — Parallel message publishing with semaphore control
- ✅ **Connection Pooling** — Efficient SQL connection lifecycle management
- ✅ **Entity-Specific Projectors** — Optimized projection logic per entity type
- ✅ **Column Mask Caching** — Cached column metadata for change decoding

### Performance Monitoring
Built-in metrics tracking:
```
📊 BATCH PROCESSED: 100 messages | ✅ 98 success | ❌ 2 errors | 
Total: 450ms | Avg: 4.5ms/msg
```

### Scalability Configuration
```json
{
  "MassTransit": {
    "Outbox": {
      "BatchSize": 100,                    // Messages per batch
      "MaxConcurrentDispatches": 10,       // Parallel dispatch threads
      "DeliveryIntervalSeconds": 2,        // Min polling interval
      "CleanupIntervalHours": 6            // Cleanup frequency
    }
  }
}
```

### Production Recommendations
- **Batch Size**: 100-500 (balance throughput vs. latency)
- **Concurrency**: 5-15 (based on RabbitMQ connection limits)  
- **Polling Interval**: 2-5 seconds (based on change frequency)
- **Retention**: 7-30 days (based on compliance requirements)

---

## Architecture Evolution Summary

This service represents a **clean, production-ready, and extensible system**:

### ✅ **Completed Features**
- **Generic entity support** — Handles multiple entity types with specialized projectors
- **Full outbox pattern implementation** with transactional guarantees
- **Real-time diagnostics** and monitoring  
- **Structured logging** with searchable properties and entity context
- **Modular architecture** with clear separation of concerns
- **Azure AD authentication** support with connection string sanitization
- **Performance optimization** with adaptive polling and concurrent processing
- **Comprehensive health checks** with separate liveness/readiness probes
- **Automated schema management** with watermark cursors and retention cleanup

### 🔄 **Ongoing Benefits**  
- **Easy entity extension** — Add new tables with minimal projector implementation
- **Scalable concurrent processing** with configurable batching and parallelism
- **Reliable message delivery guarantees** via outbox pattern
- **Complete system visibility** with structured logging and real-time diagnostics
- **Clean, maintainable codebase** with modern .NET 8 patterns
- **Zero technical debt** — No legacy or deprecated code

The system provides complete visibility into multi-entity change flows, making it easy to troubleshoot issues, monitor performance, and extend functionality in production environments.

### Enhanced Flow with Entity-Specific Projectors
```
┌─────────────┐    ┌──────────────┐    ┌─────────────┐    ┌─────────────┐
│   SQL DB    │    │   Polling    │    │   Outbox    │    │  RabbitMQ   │
│ Change      │ CT │   Service    │ TX │ Table (DB)  │ PUB│   Topic     │
│ Tracking    ├───▶│ (Watermark)  ├───▶│  Messages   ├───▶│  Exchange   │
└─────────────┘    └──────┬───────┘    └─────────────┘    └─────────────┘
                           │                   │                  │
                           ▼                   ▼                  ▼
                   ┌───────────────┐ ┌──────────────┐    ┌─────────────┐
                   │   Projectors  │ │  Diagnostics │    │ Downstream  │
                   │ Issuer | Deal │ │   Service    │    │ Consumers   │
                   │ | Instrument  │ │ (Health Mon.)│    │             │
                   │   | Generic   │ │              │    │             │
                   └───────────────┘ └──────────────┘    └─────────────┘
