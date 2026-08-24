# Project cleanup audit

Updated: **2026-08-24**.

## Scope and safety rule

This audit is performed on the separate `cleanup/project-audit` branch above
`4592f6b9bc9e06ccb60f1bc54fb8a6fb4ba6cf58`. A file being absent from the current Avalonia compile
list is not enough reason to delete it: `NATIVE_SURFACE.tsv` still contains 454
`unported-blocker` rows, so much of the native tree remains required as parity evidence and source
for later porting.

The canonical application graph is `MissionPlanner.slnx`. The inherited `MissionPlanner.sln` is a
non-canonical upstream/reference graph: a diagnostic Linux build reports 62 errors and 5 warnings,
including WinForms/.NET Framework 4.7.2 requirements, an uninitialized Mono submodule and
case-sensitive paths that only resolve on Windows. It is retained until the remaining native and
mobile project decisions are complete; no active build or package may use it.

## Removed or replaced

- Replaced the old `build.bat` (net461 APPX signing and upload through a developer-specific SSH
  path) with a small Windows entry point for the Release `MissionPlanner.slnx` build and all tests.
- Removed obsolete root Mono launch/AOT files and the pre-Avalonia macOS bundle in `MAC/`; current
  self-contained app creation is `build/macos/make-app.sh`.
- Removed old debug/clean/project-generation/beta-tag batch wrappers. They targeted the legacy
  solution or mutated remote tags and no current documentation or workflow referenced them.
- Removed Azure Pipelines, AppVeyor and the manual WinForms GitHub workflow. They all built the
  legacy solution and expected `bin/Release/net461`; the supported gates are GitHub
  `ci.yml`, `codeql.yml` and `release.yml`.
- Removed the tracked `Updater/bin/Release` executable/config output. `bin/` is ignored, the old
  updater source remains available for comparison, and the active signed GitHub updater is
  `Services/Updater.cs`.
- Removed the unused/deprecated `Avalonia.Diagnostics` package; no diagnostics API or DevTools
  attachment existed in the application.
- Disabled inherited `GeneratePackageOnBuild` in the internal alglibnet/netDxf project references.
  Normal application publish no longer emits unused `.nupkg` files or missing-package-README
  notices; neither CI nor release automation consumes those library packages.
- Corrected the root Makefile's target-specific RIDs. The documented `make windows-zip` command no
  longer inherits the Linux default and fail-fast exits before publishing.
- Added narrow Git attributes for Windows batch files and inherited CRLF project/solution files,
  preventing CR line terminators from being misreported as trailing whitespace without performing
  a repository-wide line-ending rewrite.

## Findings fixed

- Enabled full transitive NuGet auditing and promoted `NU1901` through `NU1904` to errors for every
  active restore.
- Updated `System.Text.RegularExpressions` from vulnerable 4.3.0 to 4.3.1 and
  `System.Private.Uri` from vulnerable 4.3.0 to 4.3.2 in the inherited netstandard/RID graphs and
  the net472 legacy-plugin fixture. A forced solution/RID restore and
  `dotnet list MissionPlanner.slnx package --vulnerable --include-transitive` now report no known
  vulnerable package in any solution project.
- The active Release graph builds with zero warnings/errors, Roslyn analyzer verification is clean,
  CodeQL has zero open alerts, and 1149 tests pass.

## Intentionally retained

- `Scripts/` contains the 19 official Mission Planner IronPython examples, not build scripts. The
  same directory is present in [upstream Mission Planner](https://github.com/ArduPilot/MissionPlanner/tree/master/Scripts),
  and native `Script.cs` is still an unported blocker. The Avalonia UI currently hosts Lua, so the
  examples must be converted or explicitly retired only when script parity is decided.
- Project/solution/make metadata below `ExtLibs/` belongs to vendored libraries, protocol/code
  generators or preserved upstream projects. It is not part of the active solution merely because
  it is tracked, but blanket deletion would prevent later updates or parity work.
- `MissionPlanner.sln`, `MissionPlannerLib.csproj/.sln`, `.nuget/`, Xamarin/mobile projects and the
  manual Android/Apple workflows remain together pending an explicit mobile-support decision.
- `Msi/` and `wix/` remain until serial-driver installation parity is resolved. The new MSI does not
  yet install the official Windows driver certificate, as README documents.
- `Updater/` source, legacy `app.config`, old WinForms resources and every native manifest blocker
  remain as implementation/reference material. Only generated updater output was removed.
- NuGet reports xUnit v2 as legacy and lists newer dependency releases. xUnit v3 migration and
  bulk dependency upgrades are deliberately separate compatibility changes, not cleanup deletions.
- `dotnet format ... analyzers --verify-no-changes` is clean, but the whitespace gate reports
  393,920 diagnostics because the preserved native tree and imported Avalonia tree use different
  historical indentation/line-ending styles without scoped formatter policy. A repository-wide
  mechanical rewrite is deferred: it would obscure code provenance and ongoing parity reviews.
  Define directory-scoped `.editorconfig` rules or normalize in dedicated, reviewable slices before
  enabling whitespace formatting as CI.

## Generated local output

`bin/`, `obj/`, `TestResults/`, `out/`, `dist/`, `upload/` and `__pycache__/` are ignored and must
never be committed. Diagnostic builds can create more than a hundred such directories; they may be
deleted after verification because all are reproducible. Their presence in a developer worktree is
not evidence that the corresponding tracked source directory is unused.

## Reproduction gates

```bash
dotnet restore MissionPlanner.slnx --force --nologo
dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
dotnet format MissionPlanner.slnx analyzers --verify-no-changes --no-restore
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --no-build --nologo
dotnet list MissionPlanner.slnx package --vulnerable --include-transitive
make linux-packages
make windows-zip
```

Windows MSI install/uninstall and both native macOS architectures remain CI gates because their
toolchains must run on their host operating systems.
