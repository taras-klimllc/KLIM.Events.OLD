# KLIM.Events Architecture Diagrams

This directory contains all the Mermaid diagrams and diagram-related documentation for the KLIM.Events service.

## ?? **Directory Contents**

### **?? Mermaid Source Files (.mmd)**
- **`diagram-architecture.mmd`** — Main system architecture diagram showing SQL Server, Change Tracking, Outbox Pattern, and RabbitMQ
- **`diagram-sequence.mmd`** — Message flow sequence diagram showing the complete change detection to message publishing workflow
- **`diagram-overview.mmd`** — Simplified system overview diagram for executive presentations

### **?? High-Resolution Diagram Images**
Located in the parent `docs/` directory:
- **`KLIM-Architecture-Main-HiRes.png`** (327KB) — 1600x1200, 3x scale
- **`KLIM-Message-Flow-Sequence-HiRes.png`** (355KB) — 1400x1000, 3x scale
- **`KLIM-Overview-Simplified-HiRes.png`** (98KB) — 1200x800, 3x scale

### **??? Vector SVG Files** 
Located in the parent `docs/` directory:
- **`KLIM-Architecture-Main.svg`** (47KB) — Infinite scalability
- **`KLIM-Message-Flow-Sequence.svg`** (35KB) — Perfect for web
- **`KLIM-Overview-Simplified.svg`** (17KB) — Smallest file size

### **?? Documentation Files**
- **`README-HighResDiagrams.md`** — Complete guide for high-resolution diagram generation and usage
- **`README-ArchitectureDiagrams.md`** — Architecture diagram documentation
- **`Enhanced_Flow_Architecture.md`** — Enhanced flow documentation
- **`MermaidTroubleshooting.md`** — Troubleshooting guide for Mermaid in VS Code
- **`VisualStudio2022-MermaidGuide.md`** — Guide for using Mermaid diagrams in Visual Studio 2022
- **`MermaidTest.md`** — Test file for verifying Mermaid rendering

---

## ?? **Quick Start**

### **Viewing Diagrams in Visual Studio 2022**
1. **Open** any `.mmd` file in VS Code with Mermaid extensions
2. **Press** `Ctrl+Shift+V` for preview
3. **View** high-res PNGs directly in VS2022 Solution Explorer

### **Editing Diagrams**
1. **Edit** the `.mmd` source files in VS Code
2. **Regenerate** images using the commands in `README-HighResDiagrams.md`
3. **Test** changes using `MermaidTest.md`

### **Regenerating High-Resolution Images**
```bash
# From solution root directory
mmdc -i docs/diagrams/diagram-architecture.mmd -o docs/KLIM-Architecture-Main-HiRes.png -s 3 -w 1600 -H 1200
mmdc -i docs/diagrams/diagram-sequence.mmd -o docs/KLIM-Message-Flow-Sequence-HiRes.png -s 3 -w 1400 -H 1000
mmdc -i docs/diagrams/diagram-overview.mmd -o docs/KLIM-Overview-Simplified-HiRes.png -s 3 -w 1200 -H 800
```

---

## ?? **Architecture Overview**

The KLIM.Events service implements a **production-ready outbox pattern** with the following key components:

### **Core Architecture Flow**
```
SQL Server (Change Tracking) ? Polling Service ? Entity Projectors ? Outbox Table ? Message Publisher ? RabbitMQ ? Consumers
```

### **Key Components Visualized**
1. **SQL Change Tracking** — Monitors `dbo.Issuers`, `dbo.Deals`, `dbo.InstrumentMaster`
2. **Entity-Specific Projectors** — `IssuerProjector`, `DealProjector`, `InstrumentProjector`, `GenericProjector`
3. **Outbox Pattern** — Transactional guarantees with deduplication
4. **Message Publishing** — RabbitMQ topic exchange with retry policies
5. **Observability** — Real-time diagnostics, structured logging, health monitoring

### **Message Contracts**
- **Primary**: `DataChangedV1` — Generic change event supporting all entity types
- **Downstream**: `DataChangeProcessed` — Simplified notification for consumers

---

## ?? **Troubleshooting**

If diagrams aren't rendering properly:
1. **Check** `MermaidTroubleshooting.md` for VS Code setup
2. **Review** `VisualStudio2022-MermaidGuide.md` for VS2022 integration
3. **Test** with `MermaidTest.md` to verify extensions are working
4. **Regenerate** high-res images following `README-HighResDiagrams.md`

---

## ?? **Related Documentation**
- **`../../README.md`** — Main service documentation
- **`../../README_Best_Practices.md`** — Architecture best practices and recommendations
- **`../../REFACTORING_SUMMARY.md`** — Clean architecture refactoring summary

This organized structure keeps all diagram-related files together while maintaining clean separation from the main codebase documentation.