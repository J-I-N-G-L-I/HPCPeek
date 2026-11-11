# Build and release HPCPeek

HPCPeek uses .NET 10, Avalonia and SSH.NET. It produces self-contained ZIPs for Windows 11 x64, macOS 15+ Apple Silicon and macOS 15+ Intel. End users do not need the SDK or a separate runtime; see the [README](../README.md) for installation and use.

## Requirements

- .NET 10 SDK, selected by `global.json`.
- PowerShell 7 (`pwsh`).
- Access to NuGet for dependency and target-runtime restore.
- A Mac with Apple's command-line tools for Developer ID signing and notarization.

Run the commands below from the repository root in PowerShell. The build script uses `.tools/dotnet/dotnet.exe` when present, otherwise `dotnet` on `PATH`. Use `-DotNet <path>` to select another SDK executable.

## Test and build

```powershell
./scripts/test.ps1
dotnet run --project ./src/AirePeek.Desktop -- --demo

./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier win-x64
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-arm64
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-x64
```

To produce all three packages on one development machine:

```powershell
./scripts/build-portable.ps1 -Version 1.0.0 -AllPlatforms
```

The build script runs unit tests, publishes the application, checks the package structure and creates ZIPs with SHA-256 checksums. It also extracts and launches the package matching the host OS and process architecture, runs the offline UI checks, and measures startup and idle memory. Cross-built packages receive structure checks only; they still need native execution tests.

`-SkipTests`, `-SkipSmoke` and `-SkipBenchmark` are available for development. Skipped checks are not evidence of a passing release. CI additionally runs the loopback SSH protocol tests; their local setup is documented in `tests/integration/README.md`.

## Output

For version `1.0.0`, output goes to `artifacts/v1.0.0/`:

| File | Purpose |
| --- | --- |
| `HPCPeek-v1.0.0-win-x64-portable.zip` | Windows application and runtime |
| `HPCPeek-v1.0.0-osx-arm64-portable-unsigned-test.zip` | Apple Silicon test build |
| `HPCPeek-v1.0.0-osx-x64-portable-unsigned-test.zip` | Intel Mac test build |
| `SHA256SUMS.txt` | Checksums for the generated ZIPs |
| `manifest-*.json`, `size-*.json`, `structure-*.json` | Build, size and package checks |
| `smoke-*/evidence/`, `benchmark-*/benchmark-result.json` | Native UI and performance results |

Signed and notarized Mac packages omit `-unsigned-test`. Native Mac packaging uses `ditto`; cross-built ZIPs preserve Unix executable permissions. Package structure alone does not establish that an app starts on another platform.

Portable builds use partial .NET trimming while preserving the application, core, SSH and cryptography assemblies. The runtime, graphics libraries and required resources remain bundled. Native AOT is not used. Debug symbols, compiler XML documentation, development screenshots, test fixtures and local settings are excluded.

Each ZIP includes the README, this build guide, third-party notices and license texts, plus the two README screenshots. Other development documents remain outside the package.

## Configuration and credentials

Windows stores settings in `portable-data` beside the executable. macOS uses `~/Library/Application Support/HPCPeek`, leaving the `.app` unchanged. Optional saved passwords use Windows Credential Manager or macOS Keychain; they are not portable files. Never include personal settings, logs, passwords, one-time codes or signing credentials in a commit or release archive.

The public application is named HPCPeek. Source directories and internal namespaces retain `AirePeek` for compatibility.

## GitHub Actions and releases

Ordinary pushes and pull requests run unit tests, simulated two-hop SSH authentication, and Windows package extraction and startup checks. Tag builds use native runners for all three architectures, capture UI and performance evidence, then upload the three ZIPs and `SHA256SUMS.txt` to **Releases → Assets**. The workflow verifies the uploaded asset names and sizes. Test reports and screenshots remain separate Actions artifacts.

After committing and pushing a tested change, create a new version tag. For example, use `v1.0.1` for the next patch release:

```powershell
git tag -a v1.0.1 -m "HPCPeek v1.0.1"
git push origin v1.0.1
```

The release version comes from the tag. Do not move an existing published tag to a different commit. Manual workflow runs build packages but do not publish a Release unless run against a version tag. The release job uses GitHub's temporary repository token; no HPC credentials are required.

Automated checks use synthetic data. They do not establish that a real Aire or Calder account can authenticate, or that a particular university Duo policy works.

## macOS signing and notarization

Without signing credentials, Mac packages are marked `unsigned-test` and the Release is a pre-release. These packages are not Developer ID signed or notarized. Do not disable macOS security protections to distribute or test them.

For signed builds, configure all six repository secrets under **Settings → Secrets and variables → Actions**:

| Secret | Value |
| --- | --- |
| `MACOS_CERTIFICATE_BASE64` | Base64-encoded Developer ID Application `.p12` |
| `MACOS_CERTIFICATE_PASSWORD` | Password for the `.p12` |
| `MACOS_SIGNING_IDENTITY` | Full Developer ID Application signing identity |
| `APPLE_ID` | Apple account used for notarization |
| `APPLE_TEAM_ID` | Apple Developer team ID |
| `APPLE_APP_SPECIFIC_PASSWORD` | App-specific password for notarization |

Partial credentials cause the Mac build to fail. On a local Mac, provide the same values as environment variables and run:

```powershell
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-arm64 -SignMac
```

The signing script creates a temporary keychain, signs the nested binaries and `.app`, enables hardened runtime with the required .NET JIT entitlement, waits for notarization, staples the ticket and verifies Gatekeeper acceptance. It removes the temporary keychain afterward. The `unsigned-test` suffix is removed only after these steps succeed.

See [Apple's notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution) and [Avalonia's macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) for platform background.
