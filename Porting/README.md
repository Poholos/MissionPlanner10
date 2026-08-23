# In-place Avalonia migration

This directory is the auditable control plane for replacing the root Mission Planner WinForms
application with the existing cross-platform Avalonia port. It intentionally lives in the native
Mission Planner fork so the final implementation, backend fixes, plugins, documentation and future
upstream merges share one history.

## Non-negotiable result

- `MissionPlanner.csproj` becomes the net10 Avalonia application.
- The application/product/assembly identity is `MissionPlanner`.
- Ported views replace the corresponding native features in `GCSViews/`, `Controls/`, setup,
  wizard, plugin and other normal feature locations.
- No `external/MissionPlanner` gitlink, copied source tree, or build-time dependency is introduced.
- The existing unrelated `ExtLibs/mono` submodule is not removed accidentally.
- WinForms files are removed only after a mapped Avalonia replacement or an explicit, justified
  `remove` decision exists.
- Existing RESX cultures remain preserved and receive an explicit localization mapping.

## Manifests

`NATIVE_SURFACE.tsv` inventories the tracked native C# and RESX surface outside `ExtLibs` and
`MissionPlannerTests`. Its statuses are:

- `retain`: keep and compile/reuse the native artifact;
- `replace`: an Avalonia implementation replaces it;
- `merge`: native and port implementations must be combined;
- `remove`: deliberate removal with a documented reason;
- `unported-blocker`: classification or replacement is not complete, so full-port acceptance is
  blocked.

`PORT_SOURCE_IMPORT.tsv` inventories every tracked file at the pinned port commit and records a
planned native target plus an import action. It prevents accidental import of the old submodule,
generated output, or a second application layout.

Regenerate both inventories with:

```bash
./build/porting/generate-manifests.sh /home/alex/src/MP/MissionPlanner-Avalonia
```

The generator deliberately starts uncertain native C# entries as `unported-blocker`. Classification
is changed only after code-level comparison; this makes missing functionality visible instead of
silently excluding it from the new project.

## Execution order

1. Freeze and verify the two exact baselines.
2. Close every manifest blocker through retain/replace/merge/remove evidence.
3. Replace the root project graph and main assembly identity.
4. Import Avalonia sources into native feature locations and migrate namespaces.
5. Apply Settings, session-only MAVState parameters, Radio/Grid and CoreCLR compatibility directly
   to native source; remove build-generated patches.
6. Merge the legacy plugin ABI into the main `MissionPlanner` assembly and retain a distinctly named
   portable contract only where useful.
7. Preserve RESX localization and translation tooling.
8. Merge README, licensing, CI, CodeQL, updater, release and package workflows by meaning.
9. Require all port tests, relevant native backend tests, cross-RID publish, Linux smoke/package,
   plugin, localization and session-safety gates.
10. Commit/push the verified migration branch; merge/cut over only with user approval.

The detailed live progress and next step belong in `STATUS.md`; fixed source identities belong in
`BASELINE.md`.
