# ⛔ DEPRECATED installers — read before deploying

**Date:** 2026-07-22

The legacy installers listed below share the same defects. Deploy the add-in with
`scripts/install-addon.ps1` **at or after upstream commit `b4384cb`** (the R1–R8 rewrite),
which supersedes every script in the table.

```powershell
# Pick the Revit version explicitly; -All covers every installed version with a build.
.\scripts\install-addon.ps1 -Version 2024
```

## Why these are deprecated

Three defects, found while debugging a deploy that appeared to succeed but changed nothing:

1. **Deploy target can be shadowed.** They copy to `%APPDATA%\Autodesk\Revit\Addins\<ver>\`
   (per-user). Revit also reads `%ProgramData%\Autodesk\Revit\Addins\<ver>\`, and when a
   machine-wide `RevitMCP.addin` manifest exists there, **that** copy is the one loaded —
   the per-user copy has no manifest pointing at it and is silently ignored. The deploy
   reports success and nothing changes. Which location is correct depends on where the
   registered manifest lives, so check before deploying; there is no universally right answer.
2. **Partial DLL bundle.** They copy only `RevitMCP.dll` (+ `Newtonsoft.Json.dll`). The
   add-in needs the **full** build output — 13 DLLs for `Release.R22`–`R24` (ClosedXML,
   DocumentFormat.OpenXml, ExcelNumberFormat, Irony, SixLabors.Fonts, XLParser, and the
   `System.*` compat shims), 8 for `Release.R25`–`R26`. Missing dependencies surface as a
   runtime `FileNotFoundException` on the Excel-backed tools, not as a deploy error.
3. **No build step.** They only copy; `dotnet build -c Release.R<yy>` had to be run by hand
   first, so a stale `bin/` would be deployed without warning.

Upstream `b4384cb` addresses #2 and #3 (it enumerates the matching config's build output
instead of a hardcoded file list, verifies each copied file by SHA256, checks the build
generation matches the target Revit, and adds `-Version` / `-All`). For #1 it adds
`-AddinsRoot` so the target directory is selectable rather than assumed.

### Manifest AddInId drift

If the deployed `RevitMCP.addin` has been edited in place, its `AddInId` may no longer match
the one in the repo's source manifest. Redeploying the repo copy over it silently reverts that
change — and a diverged `AddInId` is usually deliberate, added to stop a GUID collision with
another add-in. Compare both manifests before overwriting.

## Deprecated files

| File | Status |
|------|--------|
| `scripts/install-addon.ps1` (pre-`b4384cb`) | Guarded — exits unless `$env:ALLOW_DEPRECATED_INSTALL=1` |
| `scripts/install-addon-bom.ps1` | BOM/CJK variant — do not use |
| `scripts/install-ascii.ps1` | ASCII variant — do not use |
| `scripts/install-addon.bat` | Batch variant — do not use |
| `scripts/setup.ps1` | One-click orchestrator; only its **deploy stage** is affected. The other stages (Node/.NET check, MCP-Server build, client config, port 8964) are fine. |
| `.claude/skills/deploy-addon/SKILL.md` | Deploy skill — same target and bundle defects; banner added |

## Also affected (diagnostics)

`verify-installation.ps1` and `preflight-check.ps1` check the per-user path only, so they can
report "not installed" for an add-in that is correctly deployed machine-wide — or report a
shadowed per-user copy as installed. Confirm against the directory the live manifest actually
points at: it should hold the full DLL set plus `Addins\<ver>\RevitMCP.addin`.

`verify-qaqc.ps1` Phase 5 takes `-AddinsRoot` for the same reason; point it at the location
you actually deploy to.
