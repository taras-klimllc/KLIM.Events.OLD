# Clean Refactoring: Generic Data Change Tracking System

## Overview
Since we are in the development phase with no deployed code, this refactoring completely replaces the instrument-specific logic with a clean, generic data change tracking system that supports Issuers, Deals, and future entity types.

## ? **REFACTORING COMPLETE**

All legacy code has been removed and replaced with a modern, extensible architecture.

## Key Changes Made

### 1. Generic Message Contracts
- **`DataChangedV1`** - Generic data change event for any tracked entity
- **`DataChangeProcessed`** - Downstream simplified change notification
- Clean contracts file: `src/KLIM.Events.Service/Messaging/Contracts/DataChangedV1.cs`

### 2. Entity-Specific Projectors
- **`IssuerProjector`** - Handles `dbo.Issuers` table changes, maps to "Issuer" EntityType
- **`DealProjector`** - Handles `dbo.Deals` table changes, maps to "Deal" EntityType  
- **`GenericDomainChangeProjector`** - Handles future tables not covered by specialized projectors

### 3. Clean Consumer Architecture
- **`DataChangedConsumer`** - Processes generic `DataChangedV1` events
- Validates messages and publishes processed events

### 4. Simplified Messaging Topology
- **Queue**: `klim.events.datachanged.v1` with routing key `data.changed.v1`
- **Exchange**: `klim.change.events` (topic)
- **Dead Letter Queue**: `klim.events.datachanged.v1.dlq`
- No legacy queues or backward compatibility overhead

### 5. Enhanced Logging System
- `OutboxLoggerExtensions` supports generic entity types and display names
- Enhanced structured logging with `EntityType`, `DisplayName`, and `EntityId`
- Comprehensive performance and batch metrics

## Entity Type Mappings

| Table | EntityType | DisplayName Logic | Projector |
|-------|------------|-------------------|-----------|
| `dbo.Issuers` | "Issuer" | `IssuerTicker` ?? `IssuerName` | `IssuerProjector` |
| `dbo.Deals` | "Deal" | `ShortName` ?? `DealName` | `DealProjector` |
| Other tables | Dynamic | Generic fallback | `GenericDomainChangeProjector` |

## Message Flow

```
SQL Change Tracking ? IssuerProjector/DealProjector ? DataChangedV1 ? klim.events.datachanged.v1 ? DataChangedConsumer ? DataChangeProcessed
```

## Configuration Structure

### Clean Table Configuration
```json
{
  "ChangeTracking": {
    "Tables": [
      { "Schema": "dbo", "Name": "Issuers", "Pk": "IssuerID" },
      { "Schema": "dbo", "Name": "Deals", "Pk": "DealID" }
    ]
  }
}
```

### Program.cs - Clean Registration
```csharp
// Entity-specific projectors
builder.Services.AddSingleton<IChangeEventProjector, IssuerProjector>();
builder.Services.AddSingleton<IChangeEventProjector, DealProjector>();
builder.Services.AddSingleton<IChangeEventProjector, GenericDomainChangeProjector>();

// Single consumer for all entity types
x.AddConsumer<DataChangedConsumer>();

// Single messaging endpoint
cfg.ReceiveEndpoint("klim.events.datachanged.v1", ep =>
{
    ep.ConfigureConsumer<DataChangedConsumer>(context);
    ep.Bind<DataChangedV1>(b => { b.RoutingKey = "data.changed.v1"; });
});
```

### RabbitMQ Topology - Simplified
```json
{
  "queues": [
    {
      "name": "klim.events.datachanged.v1",
      "arguments": {
        "x-dead-letter-exchange": "klim.events.datachanged.v1.dlq"
      }
    },
    {
      "name": "klim.events.datachanged.v1.dlq"
    }
  ],
  "bindings": [
    {
      "source": "klim.change.events",
      "destination": "klim.events.datachanged.v1",
      "routing_key": "data.changed.v1"
    }
  ]
}
```

## Key Architecture Benefits

1. **Clean Codebase**: No deprecated code or backward compatibility overhead
2. **Extensibility**: Easy to add new entity types (just implement `IChangeEventProjector`)
3. **Type Safety**: Strongly-typed entity-specific projectors with clear responsibilities
4. **Consistency**: Unified event format across all entity types
5. **Maintainability**: Clear separation of concerns with entity-specific projection logic
6. **Observability**: Enhanced logging with entity type context and structured properties
7. **Performance**: No conversion overhead or legacy message processing

## Database Schema Support

The system properly supports the actual database schema:
- ? **`dbo.Issuers`** with `IssuerID`, `IssuerName`, `IssuerTicker`, `RowGUID`
- ? **`dbo.Deals`** with `DealID`, `DealName`, `ShortName`, `RowGUID`  
- ? **Future tables** via `GenericDomainChangeProjector` with automatic EntityType detection

## Sample Message Structure

### DataChangedV1 Event
```json
{
  "entityId": "12345678-1234-1234-1234-123456789012",
  "entityType": "Issuer",
  "displayName": "AAPL",
  "changeVersion": 98765,
  "changedAt": "2023-12-01T14:30:15.123Z",
  "changeSource": "dbo.Issuers",
  "operation": "U",
  "changedFields": ["IssuerName", "LastUpdated"],
  "preImage": {
    "IssuerName": "Apple Inc"
  },
  "postImage": {
    "IssuerName": "Apple Inc."
  },
  "correlationId": "87654321-4321-4321-4321-210987654321"
}
```

### DataChangeProcessed Event
```json
{
  "entityId": "12345678-1234-1234-1234-123456789012",
  "entityType": "Issuer",
  "displayName": "AAPL",
  "changeVersion": 98765,
  "changedAt": "2023-12-01T14:30:15.123Z",
  "operation": "U",
  "changeSource": "dbo.Issuers",
  "fields": ["IssuerName", "LastUpdated"],
  "processedAt": "2023-12-01T14:30:15.456Z",
  "deliveryCount": 1
}
```

## Testing Strategy

### Entity-Specific Testing
- Test `IssuerProjector` with Issuer table changes
- Test `DealProjector` with Deal table changes  
- Test `GenericDomainChangeProjector` with future table additions
- Validate message routing to correct queue
- Verify structured logging includes new properties (`EntityType`, `DisplayName`)

### Integration Testing
- End-to-end flow: SQL change ? projection ? outbox ? message dispatch ? consumption
- RabbitMQ topology verification
- Health check validation
- Performance benchmarking

## Future Enhancements

### Immediate Opportunities
- Add more entity-specific projectors as new tables are tracked
- Implement contract versioning strategy (DataChangedV2, etc.)
- Add metrics for entity type distribution
- Consider entity-specific routing keys (e.g., `issuer.changed.v1`, `deal.changed.v1`)

### Advanced Features
- Add support for bulk change operations
- Implement change event aggregation for related entities
- Add change event replay capabilities
- Integrate with external event sourcing systems

## Development Benefits

Since we're in development phase:
- ? **No Migration Overhead**: Direct implementation of clean architecture
- ? **No Technical Debt**: Clean, maintainable codebase from day one
- ? **Optimal Performance**: No backward compatibility processing overhead
- ? **Clear Intent**: Code clearly expresses generic data change tracking purpose
- ? **Modern Patterns**: Uses latest .NET 8 features and best practices
- ? **Production Ready**: Full outbox pattern, health checks, structured logging

## Success Criteria Met

- ? **Clean Architecture**: Removed all instrument-specific legacy code
- ? **Generic Support**: Handles multiple entity types (Issuers, Deals, future tables)
- ? **Extensible Design**: Easy to add new entity types without core changes
- ? **Production Ready**: Complete outbox pattern with transactional guarantees
- ? **Observable**: Comprehensive logging and diagnostics
- ? **Maintainable**: Clear separation of concerns and modern C# patterns