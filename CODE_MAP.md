# CODE_MAP.md

AI-only navigation map (canonical). This file answers **where to look**, never **how to change**.
Rules, conventions, and guard rails live in `CLAUDE.md`. Methods live in `domain/*.md`.

## Runtime Path

```text
AI client → stdio → MCP-Server (Node/TS) → WebSocket :8964 → MCP (C# add-in) → Revit API
```

A feature usually spans **both sides of the bridge**: a tool definition in `MCP-Server/src/tools/*.ts` and a matching command in `MCP/Core/Commands/CommandExecutor.*.cs`. Touch both or neither.

## Module Map

| Path | Responsibility | Local rules |
|---|---|---|
| `MCP-Server/` | Node/TS MCP stdio server. Entry `src/index.ts`; WebSocket client to Revit `src/socket.ts`. | `CLAUDE.md` → Build Commands |
| `MCP-Server/src/tools/` | One file per workflow area (wall, room, sheet, dimension, clash, stair-compliance, smoke-exhaust, curtain-wall, dwg-column, mep, visualization, …). Registry + `MCP_PROFILE` filtering: `index.ts`. Tool-name → Revit-command bridge: `revit-tools.ts`. | snake_case tool names |
| `MCP/` | C# Revit add-in (namespace `RevitMCP`). Entry `Application.cs` (ribbon + IExternalApplication). Single `RevitMCP.csproj`, configs `Release.R22`–`R26`. | `CLAUDE.md` → Deployment Rules (forbidden files list) |
| `MCP/Core/` | Infrastructure: `SocketService.cs` (WebSocket server :8964), `ExternalEventManager.cs` (Revit UI-thread marshal), `CommandExecutor.cs` (dispatcher), `RevitCompatibility.cs` (cross-version ElementId). Standalone analyzers: `ClashDetector`, `FloorSlopeAnalyzer`, `ExteriorWallOpeningChecker`, `DwgColumnExecutor`. | Transactions + ExternalEvent mandatory |
| `MCP/Core/Commands/` | `CommandExecutor.{Workflow}.cs` partial classes, one per workflow area — mirrors `MCP-Server/src/tools/` naming. | follow existing switch/dispatcher pattern |
| `domain/` | Shared BIM SOPs: regulations, formulas, review methods. **Method source of truth — beats skills and model memory.** Building codes: `domain/references/`. | bilingual; frontmatter per `domain/frontmatter-standard.md` |
| `.claude/skills/` | AI workflow orchestration (tool sequences). If a skill conflicts with a domain file, domain wins. | `domain/skill-authoring-standard.md` |
| `.claude/commands/` | Slash-command behavior. | English |
| `pyRevit_Tools/` | pyRevit extension (`MCP_Tools.extension`, `MCP_Schedules.tab`) — ribbon-side utilities, separate from the MCP bridge. | `pyRevit_Tools/README.md` |
| `scripts/` | Ops: `install-addon.ps1` (deploy), `verify-qaqc.ps1` (QA gate), `release-port.ps1` (port 8964), `git-hooks/`. | one primary installer only |
| `docs/` | Human docs + `DOCUMENT_AUDIENCE_INVENTORY.md` (canonical doc classification). | audience policy in inventory |
| `log/` | Append-only monthly AI change logs (`YYYY-MM.md`). | never rewrite history |
| `slides/`, `index.html` | Teaching / presentation material. Not runtime. | — |

## Navigation Principles

1. Classify the request by workflow area first, then enter via the matching `tools/*.ts` ↔ `CommandExecutor.*.cs` pair.
2. Read the module-local rules referenced above before editing; read the matching `domain/*.md` before computing anything regulatory.
3. Find symbols by definition/reference, not blind text search — `createInvoice`-style grep hits mocks, docs, and dead code.
4. Keep this map current: adding, moving, or retiring a module updates this file in the same change.
