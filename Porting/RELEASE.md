# Cross-platform packaging and releases

Updated: **2026-08-24**.

## Artifact matrix

| Runtime | Human-installable artifacts | In-app update payload | Build host |
| --- | --- | --- | --- |
| `linux-x64` | self-contained `.tar.gz`, amd64 `.deb` | root-relative ZIP | Ubuntu |
| `win-x64` | self-contained portable ZIP, x64 MSI | root-relative ZIP | Windows |
| `osx-x64` | complete `Mission Planner.app` ZIP | the same complete app ZIP | macOS |
| `osx-arm64` | complete `Mission Planner.app` ZIP | the same complete app ZIP | macOS |

Linux and Windows package entry points are `build/linux/package.sh` and
`build/windows/package.sh`; the root `Makefile` exposes their common targets. The macOS release
job wraps the self-contained publish with `build/macos/make-app.sh`, then signs and archives the
complete app bundle. Generated output belongs below `out/`, `dist/` or `upload/` and is never
committed.

All formats are built from the root `MissionPlanner.csproj`. The publish payload includes
`COPYING.txt`, `NOTICE.md` and every file below `LICENSES/`. Windows-only native DLLs are excluded
from Linux/macOS; the Windows package retains the x64 SimpleBLE runtime. macOS publish fetches the
pinned official VLC 3.0.23 and SimpleBLE 0.7.3 assets and rejects a size, SHA-256, architecture or
runtime-layout mismatch.

## Version contract

`build/version.sh` reads the official version from `Properties/AssemblyInfo.cs` and emits one
contract for assemblies, UI, archives, Debian metadata, MSI and updater manifests:

```text
informational: 1.3.83+20260824.0123abcd
artifact:      1.3.83-20260824.0123abcd
release tag:   v1.3.83-20260824.0123abcd
beta tag:      v1.3.83-20260824.0123abcd-beta[.N]
```

Developer packages made with tracked or untracked source changes append `.dirty`. Debian uses an
epoch and the monotonically ordered repository revision. Windows Installer accepts only three
numeric fields, so the MSI product version is `major.minor.repository-revision`; the complete
informational version remains visible in package metadata.

## Local commands

```bash
dotnet build MissionPlanner.slnx -c Release -m:1 --nologo
dotnet test MissionPlannerTests/Avalonia/MissionPlanner.Tests/MissionPlanner.Tests.csproj \
  -c Release -m:1 --no-build --nologo

make linux-packages
make windows-zip
```

`make windows-msi` and `make windows-packages` require Windows because WiX only supports producing
MSI packages there. `.github/workflows/ci.yml` runs that target on `windows-latest`, expands the
portable ZIP, performs an MSI administrative extraction with `msiexec`, and checks the installed
launcher. The same workflow validates Linux with `lintian` plus an Xvfb launch and builds both
macOS architectures on a native runner.

## GitHub release and updater

`.github/workflows/release.yml` builds all artifacts on a `v*` tag. A tag is rejected unless its
official version and eight-character hash match the tagged commit. It publishes flat release
assets plus `SHA256SUMS` and, for each RID, these updater assets:

```text
<rid>-manifest.json
<rid>-manifest.sig
MissionPlanner-<artifact>-<rid>-update.zip  # Linux and Windows
MissionPlanner-<artifact>-<rid>.zip         # complete macOS app
```

The application queries `Rouniy/MissionPlanner` GitHub Releases directly. Stable updates select a
non-prerelease; Beta Updates select a prerelease. Both require an Ed25519-signed manifest and a
SHA-256-pinned full bundle. Debian installs contain `.package-managed` and deliberately defer
updates to APT instead of overwriting package-owned files.

The required repository secret is `UPDATE_SIGNING_KEY`, containing an unencrypted PKCS#8 Ed25519
private key. Only its public half is committed in `build/update-public-key.txt` and embedded in the
updater. The release job derives the public half from the secret and refuses to publish if it does
not match. Never commit, print or upload the private key outside GitHub Secrets.

Optional platform signing uses these repository secrets:

- Windows: `WINDOWS_SIGNING_PFX` (base64 PFX) and `WINDOWS_SIGNING_PASSWORD`.
- macOS: `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY`, plus
  `MACOS_NOTARY_KEY`, `MACOS_NOTARY_KEY_ID` and `MACOS_NOTARY_ISSUER` for notarization.

Without those optional secrets, CI still produces a functional unsigned Windows package and an
ad-hoc-signed macOS preview. A public production release should be treated as unsigned until the
corresponding Authenticode/Developer ID identities are configured and verified.

## Verification record

On 2026-08-24, before publishing the packaging commit:

- solution Release build: `0 warnings / 0 errors`;
- tests: `1143 passed / 0 failed / 0 skipped`;
- `linux-x64`: `.tar.gz` and `.deb` produced; `lintian` clean; extracted DEB reached the normal
  Avalonia event loop under Xvfb; Windows SimpleBLE/libusb binaries absent;
- `win-x64`: portable ZIP passed CRC/extraction checks and contained x64 PE launcher/SimpleBLE
  binaries plus the complete license set;
- `osx-x64` and `osx-arm64`: both self-contained publishes passed; apphost, SimpleBLE and VLC
  binaries matched their requested Mach-O architecture; complete `.app` layouts were generated;
- GitHub workflow YAML passed PyYAML parsing and `actionlint` 1.7.12; packaging shell passed
  `bash -n`, ShellCheck and `git diff --check`.

The actual MSI creation/signature behavior and native macOS ad-hoc/Developer-ID signing remain
runner-side gates; record their exact GitHub Actions run IDs in `STATUS.md` after every release
workflow change.
