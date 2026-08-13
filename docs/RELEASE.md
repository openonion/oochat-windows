# Releasing

Pushing a `v*` tag builds, audits, and publishes one x64 self-contained portable ZIP. The current
release pipeline does **not** build or publish an MSIX, so it needs no signing certificate or
repository signing secrets.

| | |
|---|---|
| Workflow | `.github/workflows/release.yml` |
| Release audit | `scripts/Test-PackageContents.ps1` |
| Architectures | **x64 only** for now — see "Adding ARM64" |
| Distribution model | Unpackaged, self-contained portable ZIP with a root launcher and nested `app\` payload |

```powershell
git tag v1.2.3
git push origin v1.2.3
```

To rehearse without publishing, run the workflow manually with a version. Every gate runs, but no
GitHub Release is created.

## Versioning

`Directory.Build.props` holds `VersionPrefix`, the local-development default.
The release workflow overrides the application version from the `v*` tag:

```text
v1.2.3       -> 1.2.3
v1.2.3-rc.1  -> 1.2.3-rc.1 (GitHub prerelease)
```

The release asset keeps the complete tag in its filename:

```text
ConnectOnion.Desktop-v1.2.3-x64-portable.zip
```

## What the release contains

| Asset | Purpose |
|---|---|
| `ConnectOnion.Desktop-v<version>-x64-portable.zip` | Self-contained unpackaged app |
| `SHA256SUMS.txt` | SHA-256 checksum for the published ZIP |

The ZIP root contains only `ConnectOnion.WinUIClient.exe` and the `app\` folder. The root EXE is a
small dependency-free NativeAOT launcher with the product icon; it starts
`app\ConnectOnion.WinUIClient.exe`. The complete original self-contained publish — .NET runtime,
Windows App SDK runtime, application dependencies, assets, and third-party notices — stays
unchanged inside `app\`, where its DLL probing paths remain valid. Extract the complete archive,
keep the `app\` folder beside the launcher, and run the root EXE.

The portable build:

- does not require a separately installed .NET runtime or Windows App SDK runtime;
- does not install or register an entry in Windows Installed Apps;
- does not have MSIX package identity and does not participate in MSIX upgrades;
- stores unpackaged application data under `%AppData%\ConnectOnion`;
- can be replaced by extracting a newer release, while the shared data directory remains intact.

## Release gates

Every tag-driven release reruns the full headless validation suite before publishing:

- solution restore, dependency vulnerability audit, and Release/x64 build;
- protocol, Core, and SQLite integration tests;
- protocol conformance gate;
- trimmed serialization smoke harness;
- self-contained Release/x64 publish, trimmed and ReadyToRun;
- dependency-free NativeAOT root-launcher publish;
- real-window smoke test through the root launcher into the nested application;
- portable payload and size audit;
- SHA-256 checksum generation.

`scripts/Test-PackageContents.ps1` rejects PDBs, test assemblies, FlaUI, ArchUnit, logs, databases,
coverage output, source files, and other development-only payloads. It also requires exactly one
root file (the launcher), rejects root-level DLLs, and checks that the nested application contains
its executable, `coreclr.dll`, and the Windows App SDK runtime before the ZIP can ship.

Artifacts are uploaded even when a later gate fails, so a failed run remains diagnosable without
rebuilding it locally.

## Package size

The ratified x64 portable baseline is:

| Artifact | Compressed | Expanded | Budget |
|---|---:|---:|---:|
| Self-contained portable ZIP, **trimmed**, ratified 2026-08-05 | 71.29 MB | 170.71 MB | +10% |
| _(superseded)_ Self-contained portable ZIP, untrimmed, ratified 2026-07-27 | 118.91 MB | 309.21 MB | +10% |

The 2026-08-07 Windows App SDK 2.3.1 release rehearsal with the root launcher and nested `app\`
payload produced **73.22 MB compressed / 176.85 MB expanded** (466 entries), inside the ratified
budget at +2.7% / +3.6%. The small launcher/layout delta does not move the gate's baseline;
re-ratify only when a deliberate product change makes the current budget an inaccurate guard.

The size gate catches accidental payload growth. If legitimate product work exceeds the budget,
inspect the archive and deliberately re-ratify the baseline in both this document and
`scripts/Test-PackageContents.ps1`.

Trimming was enabled on 2026-08-05 and the baseline re-ratified with it — see
[TRIMMING.md](./TRIMMING.md) for the evidence. Re-ratifying was not bookkeeping: this gate is an
upper bound, so keeping the untrimmed 118.91 MB figure would have left ~60 MB of slack beneath the
limit and the accidental duplicate runtime it exists to catch would have passed unnoticed.

**Trimming is therefore no longer a lever for getting under budget.** A breach now means real new
payload. Find it, or re-ratify deliberately in both this document and
`scripts/Test-PackageContents.ps1`.

## Manual validation

Before every public release, test the extracted ZIP on a clean supported Windows profile:

1. Confirm the ZIP root contains only `ConnectOnion.WinUIClient.exe` and `app\`, then confirm the
   root launcher opens the app without installing .NET or Windows App SDK.
2. Create an identity and conversation, give an agent a custom icon, then close the app.
3. Extract the next build to a new directory and launch it.
4. Confirm the address, recovery material, conversations, agent icons, notifications, and tray
   behavior remain correct. The icon is worth checking explicitly: it is the one piece of user data
   held as a file next to the database rather than inside it, so a root that resolved differently
   would show the initial avatar instead of failing loudly.
5. Confirm deleting the extracted application directory does not delete `%AppData%\ConnectOnion`.

The headless `ReleaseUpgradeTests` still covers SQLite and DPAPI compatibility. The old
`scripts/Test-ReleaseUpgrade.ps1` remains available for future MSIX work, but it is not part of
the current portable release gate.

## MSIX status

MSIX project support remains configured for local development and future installer work, but the
release workflow intentionally does not invoke it. Therefore:

- `SIGNING_CERTIFICATE_BASE64`, `SIGNING_CERTIFICATE_PASSWORD`, and
  `SIGNING_PUBLISHER_DN` are not read by the current workflow;
- no `.msix` or `.appxsym` is staged or published;
- `scripts/Set-PackageIdentity.ps1` and `scripts/Test-ReleaseUpgrade.ps1` are dormant release tools.

If MSIX publishing is restored later, the signed package must use the existing
`ConnectOnion.Desktop` name and a deliberately chosen publisher DN. That `Name` + `Publisher`
pair becomes the permanent upgrade and application-data identity after the first installed
release. Restore signature verification and clean-machine install/upgrade/uninstall validation
in the same change.

## Adding ARM64

ARM64 is deliberately absent until there is hardware available to validate it. To add it:

1. Add an `ARM64` / `win-arm64` matrix entry to `release.yml`.
2. Publish the NativeAOT launcher for `win-arm64` as well as the nested WinUI application.
3. Give each portable ZIP an architecture-specific filename.
4. Audit each ZIP independently and include each one in `SHA256SUMS.txt`.
5. Launch and validate the ARM64 archive on real ARM64 Windows hardware before publishing.

## If a release goes wrong

A failed gate leaves the tag pushed and no GitHub Release published:

```powershell
git push --delete origin v1.2.3
git tag -d v1.2.3
# fix, then re-tag
```

If a broken archive is already published, delete the GitHub Release and its assets, but do not
reuse the version number. Publish the correction as the next patch version.
