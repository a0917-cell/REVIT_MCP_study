# ⛔ DEPRECATED installers — read before deploying

**Date:** 2026-07-22
**Replacement:** `C:\Users\tkgcc\.gemini\scripts\revit-build-deploy.ps1`

```powershell
# Build + deploy RevitMCP for Revit 2023 (default), correctly:
pwsh -File C:\Users\tkgcc\.gemini\scripts\revit-build-deploy.ps1 -RevitVersion 2023
# Already built, just deploy after closing Revit:
pwsh -File C:\Users\tkgcc\.gemini\scripts\revit-build-deploy.ps1 -RevitVersion 2023 -SkipBuild
```

## Why these are deprecated

Verified against this machine's live state on 2026-07-22, the installers below all
have the **same three defects**:

1. **Wrong target — Roaming orphan.** They copy to `%APPDATA%\Autodesk\Revit\Addins\<ver>\`
   (Roaming). Revit on this machine actually loads the add-in from
   `%ProgramData%\Autodesk\Revit\Addins\<ver>\` — that is where the registered
   `RevitMCP.addin` manifest lives. The Roaming copy has **no manifest** and is never
   loaded. Deploying there does nothing.
2. **Partial DLL bundle.** They copy only `RevitMCP.dll` (+ `Newtonsoft.Json.dll`).
   The add-in needs the **full 13-DLL bundle** (ClosedXML, DocumentFormat.OpenXml,
   ExcelNumberFormat, Irony, SixLabors.Fonts, XLParser, System.*). Missing deps →
   runtime `FileNotFoundException` on the Excel-backed tools.
3. **No build.** They only copy; you had to `dotnet build` by hand first.

The replacement script fixes all three (ProgramData target, full bundle, unified
`dotnet build -c Release.R<yy>` → deploy), never kills a running Revit, backs up the
existing deploy, and preserves the deployed `.addin` AddInId (the deployed manifest uses
`8F3A4C8C-...`, diverged from this repo's source `090a4c8c-...`; a naive redeploy from
the repo `.addin` would regress that separation).

## Deprecated files

| File | Status |
|------|--------|
| `scripts/install-addon.ps1` | Roaming installer — guarded (exits unless `$env:ALLOW_DEPRECATED_INSTALL=1`) |
| `scripts/install-addon-bom.ps1` | Roaming installer (BOM/CJK variant) — do not use |
| `scripts/install-ascii.ps1` | Roaming installer (ASCII variant) — do not use |
| `scripts/install-addon.bat` | Roaming installer (batch) — do not use |
| `scripts/setup.ps1` | One-click orchestrator; its **deploy stage** targets Roaming. Other stages (Node/.NET check, MCP-Server build, client config, port 8964) still fine — just do not rely on its add-in deploy; use the replacement for that. |
| `.claude/skills/deploy-addon/SKILL.md` | Deploy skill — targets Roaming; banner added |

## Also wrong-path (diagnostics)

`verify-installation.ps1`, `preflight-check.ps1`, `verify-qaqc.ps1` **check** the Roaming
path, so they may report "not installed" even when the add-in is correctly deployed to
ProgramData (or report the orphan as installed). Trust
`%ProgramData%\Autodesk\Revit\Addins\<ver>\RevitMCP\` (should hold 13 DLLs + a manifest at
`Addins\<ver>\RevitMCP.addin`) over these checkers.

> Note: this repo (`origin = github.com/shuotao/REVIT_MCP_study`) is tracked upstream and
> pulled monthly. These deprecation marks are local; do not push them to shuotao.
