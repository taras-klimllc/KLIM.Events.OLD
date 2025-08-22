# Mermaid Test File

This is a test file to verify Mermaid diagram rendering in VS Code.

## Simple Test Diagram

```mermaid
graph LR
    A[Start] --> B{Decision}
    B -->|Yes| C[Process A]
    B -->|No| D[Process B]
    C --> E[End]
    D --> E[End]
```

## Architecture Diagram Test

```mermaid
graph TB
    subgraph "Database"
        DB[(SQL Server)]
        TABLE[("Table")]
    end
    
    subgraph "Service"
        POLLING[Polling Service]
        OUTBOX[Outbox Service]
    end
    
    subgraph "Message Bus"
        QUEUE[RabbitMQ Queue]
    end
    
    TABLE --> POLLING
    POLLING --> OUTBOX  
    OUTBOX --> QUEUE
    
    classDef database fill:#e1f5fe
    classDef service fill:#f3e5f5
    classDef messaging fill:#e8f5e8
    
    class DB,TABLE database
    class POLLING,OUTBOX service
    class QUEUE messaging
```

## Sequence Diagram Test

```mermaid
sequenceDiagram
    participant User
    participant App
    participant DB
    
    User->>App: Request
    App->>DB: Query
    DB-->>App: Results
    App-->>User: Response
```

---

## Troubleshooting Steps:

1. **Open this file in VS Code**
2. **Press Ctrl+Shift+V** to open markdown preview
3. **Check if diagrams render properly**
4. **Try right-clicking in the preview pane** - look for Mermaid-related options

If diagrams don't render, the extensions may need VS Code restart or there might be conflicts.