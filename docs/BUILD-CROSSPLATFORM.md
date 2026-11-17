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

./scripts/build-portable.ps1 -Version 1.1.0 -RuntimeIdentifier win-x64
./scripts/build-portable.ps1 -Version 1.1.0 -RuntimeIdentifier osx-arm64
./scripts/build-portable.ps1 -Version 1.1.0 -RuntimeIdentifier osx-x64
```

To produce all three packages on one development machine:

```powershell
./scripts/build-portable.ps1 -Version 1.1.0 -AllPlatforms
```

The build script runs unit tests, publishes the application, checks the package structure and creates ZIPs with SHA-256 checksums. It also extracts and launches the package matching the host OS and process architecture, runs the offline UI checks, and measures startup and idle memory. Cross-built packages receive structure checks only; they still need native execution tests.

`-SkipTests`, `-SkipSmoke` and `-SkipBenchmark` are available for development. Skipped checks are not evidence of a passing release. CI additionally runs the loopback SSH protocol tests; their local setup is documented in `tests/integration/README.md`.

## Output

For version `1.1.0`, output goes to `artifacts/v1.1.0/`:

| File | Purpose |
| --- | --- |
| `HPCPeek-v1.1.0-win-x64-portable.zip` | Windows application and runtime |
| `HPCPeek-v1.1.0-osx-arm64-portable-unsigned-test.zip` | Apple Silicon test build |
| `HPCPeek-v1.1.0-osx-x64-portable-unsigned-test.zip` | Intel Mac test build |
| `SHA256SUMS.txt` | Checksums for the generated ZIPs |
| `manifest-*.json`, `size-*.json`, `structure-*.json` | Build, size and package checks |
| `smoke-*/evidence/`, `benchmark-*/benchmark-result.json` | Native UI and performance results |

Signed and notarized Mac packages omit `-unsigned-test`. Native Mac packaging uses `ditto`; cross-built ZIPs preserve Unix executable permissions. Package structure alone does not establish that an app starts on another platform.

Portable builds use partial .NET trimming while preserving the application, core, SSH and cryptography assemblies. The runtime, graphics libraries and required resources remain bundled. Native AOT is not used. Debug symbols, compiler XML documentation, development screenshots, test fixtures and local settings are excluded.

Each ZIP includes the README, this build guide, third-party notices and license texts, plus the two README screenshots. Other development documents remain outside the package.

## Configuration and credentials

Windows stores settings in `portable-data` beside the executable. macOS uses `~/Library/Application Support/HPCPeek`, leaving the `.app` unchanged. Optional saved passwords use Windows Credential Manager or macOS Keychain; they are not portable files. Never include personal settings, logs, passwords, one-time codes or signing credentials in a commit or release archive.

The public application is named HPCPeek. Source directories and internal namespaces retain `AirePeek` for compatibility.

## Storage data and verification

Home and Scratch horizontal bars sit below Running and Queued respectively and follow the selected cluster. The Calder GPU matrix sits below Aire, with each cluster name above its matrix at the top left. Paths come from the remote account's `$HOME` and `$SCRATCH`, with cluster-specific fallbacks based on the authenticated username. For example, Aire commonly uses `/users/<username>` and `/mnt/scratch/<username>`; Calder uses `/scratch-calder/users/<username>` for Scratch. No example username is hardcoded.

The monitor reads user quotas with `quota` for Home and Lustre `lfs quota` for Scratch. Calder Scratch can have a project quota, which is queried using the directory's project ID. Limits use a positive soft quota when available, otherwise the hard limit. These are quota usage and limits; the app does not substitute filesystem free space or run a recursive directory scan. A shared project quota can cover more than the displayed account directory. See Leeds' [storage filesystem documentation](https://arcdocs.leeds.ac.uk/calder/system/storage_filesystem.html) and [filesystem quota documentation](https://arcdocs.leeds.ac.uk/calder/usage/file_management/filesystem_quotas.html).

Unsupported commands, missing quota limits or an unavailable project ID produce an unknown reading. Previously successful readings remain dated when a later query fails. Quota queries use the existing authenticated SSH session and refresh interval. Offline Demo exercises the display but does not validate the site's live command output; before publishing, connect a real account and compare the displayed paths, limits and usage with the cluster's quota report.

## GitHub Actions and releases

Ordinary pushes and pull requests run unit tests, simulated two-hop SSH authentication, and Windows package extraction and startup checks. Tag builds use native runners for all three architectures, capture UI and performance evidence, then upload the three ZIPs and `SHA256SUMS.txt` to **Releases → Assets**. The workflow verifies the uploaded asset names and sizes. Test reports and screenshots remain separate Actions artifacts.

After committing and pushing a tested change, create a new version tag. This update adds storage quotas and completed-job cleanup; the examples use `v1.1.0`:

```powershell
git tag -a v1.1.0 -m "HPCPeek v1.1.0"
git push origin v1.1.0
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
./scripts/build-portable.ps1 -Version 1.1.0 -RuntimeIdentifier osx-arm64 -SignMac
```

The signing script creates a temporary keychain, signs the nested binaries and `.app`, enables hardened runtime with the required .NET JIT entitlement, waits for notarization, staples the ticket and verifies Gatekeeper acceptance. It removes the temporary keychain afterward. The `unsigned-test` suffix is removed only after these steps succeed.

See [Apple's notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution) and [Avalonia's macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) for platform background.

## 中文：更新 GitHub 仓库并生成新 Release

以下以当前仓库 `J-I-N-G-L-I/HPCPeek`、`main` 分支和新版本 `v1.1.0` 为例。这次更新包含刷新时间文案、已结束任务清理，以及分别位于 Running / Queued 下方的 Home / Scratch 横向配额条；右侧 Aire 在上、Calder 在下，各自名称位于矩阵左上方。创建本文时，本地已有标签为 `v1.0.0`；正式操作前请先获取远端标签，确认 `v1.1.0` 尚未使用。这里的命令需要你执行，修改文档本身不会提交代码或发布 Release。

正式发布由 GitHub Actions 在 GitHub 的 runner 上完成，不要求先在本机生成三个平台的 ZIP。推荐顺序是：推送代码 → 普通提交检查通过 → 推送版本标签 → GitHub 自动打包并创建 Release → 在网页核对资源和编辑说明。

新版缩略窗口为 320 × 168 逻辑像素，右侧保持上下排列，左侧任务计数、容量条和底部摘要之间留出更多间距。文档截图使用离线 Demo；这不代表真实集群配额或 Mac 运行已在本机验证。

### 1. 查看本次修改

在 PowerShell 7 中进入项目目录：

```powershell
Set-Location D:\Projects\Aire_Peek
git branch --show-current
git remote -v
git status --short
git diff --stat
git diff --check
git fetch origin --tags
git tag --list v1.1.0
```

确认分支为 `main`，`origin` 指向 `https://github.com/J-I-N-G-L-I/HPCPeek.git`。最后一条命令应没有输出；如果该版本已经存在，请在下文所有命令中使用一个未发布的新版本号。`git diff --check` 没有输出表示未发现空白格式问题。

### 2. 验证并提交修改

如果在交付后继续修改了代码，使用仓库现有检查，并运行 Demo 检查界面：

```powershell
./scripts/test.ps1
$sdkPath = if (Test-Path ./.tools/dotnet/dotnet.exe) { './.tools/dotnet/dotnet.exe' } else { 'dotnet' }
& $sdkPath run --project ./src/AirePeek.Desktop -- --demo
```

重点查看 `Refresh` 时间、小菜单文案、Running / Queued 下方的两个横向容量条、GPU 矩阵左上方的集群名称，以及 `Clear completed`。Demo 使用模拟数据；真实配额需要在自己的 Aire / Calder 账户连接后查看。提交已经确认的项目代码和文档：

```powershell
git add -- README.md docs src tests
git diff --cached --stat
git diff --cached --check
git commit -m "Refine compact layout with storage quotas and job cleanup"
git push origin main
```

上述 `git add` 覆盖本次功能涉及的目录；提交前查看暂存列表，确保其中都是准备发布的修改。构建产物 `artifacts/` 与个人设置 `portable-data/` 已被 `.gitignore` 排除，无需把 ZIP 提交进源代码仓库。

如果推送提示远端 `main` 有新提交，先执行 `git fetch origin`，再执行 `git rebase origin/main`。如发生冲突，处理冲突并重新验证后再推送；不要强制覆盖远端分支。

### 3. 等待普通提交检查，然后推送版本标签

打开仓库的 [Actions](https://github.com/J-I-N-G-L-I/HPCPeek/actions)，确认本次 `main` 提交的 **Test and portable desktop releases** 工作流通过。普通分支推送只运行检查；要自动创建 Release，需要推送版本标签：

```powershell
git tag -a v1.1.0 -m "HPCPeek v1.1.0"
git push origin v1.1.0
```

标签指向当前提交，推送前应确认 `git log -1 --oneline` 是刚才验证和推送的版本。不要把已经发布的旧标签移动到新提交。

发布版本由标签传入构建脚本，并覆盖项目默认版本。仅为发布 `v1.1.0`，不必修改 `Directory.Build.props`、构建脚本或普通 CI 检查里使用的 `1.0.0` 默认值。

### 4. 下载自动生成的 Release 包

推送标签后，回到 GitHub 仓库的 **Actions → Test and portable desktop releases**，打开关联 `v1.1.0` 标签的新运行记录，确认它对应刚才推送的提交。该次运行依次执行 `test`、三个平台的 `package`，最后执行 `release`。Windows、Apple Silicon Mac 和 Intel Mac 分别在对应的原生 runner 上打包及执行 Demo 检查；`release` 负责创建或更新 Release 并上传资源。

全部成功后，进入仓库首页右侧 **Releases**，或直接打开 [Releases 页面](https://github.com/J-I-N-G-L-I/HPCPeek/releases)，点击 `HPCPeek v1.1.0` 并展开 **Assets**，应看到：

- `HPCPeek-v1.1.0-win-x64-portable.zip`
- `HPCPeek-v1.1.0-osx-arm64-portable-unsigned-test.zip`
- `HPCPeek-v1.1.0-osx-x64-portable-unsigned-test.zip`
- `SHA256SUMS.txt`

如果已配置上文六项 macOS 签名、公证 Secrets，两个 Mac 文件不带 `-unsigned-test`。未配置时，现有流程会将整个 Release 标记为 **Pre-release**，Windows 包仍在同一页下载。没有必要手动上传 ZIP；现有 `release` 作业会上传并核对文件名与大小。GitHub 自动附带的 **Source code** 压缩包是源码，不是应用程序。

下载 Windows ZIP 后完整解压，运行 `HPCPeek.exe`。如需保留旧设置，先退出旧程序，再把旧目录里的 `portable-data` 复制到新解压目录。在 ZIP 的下载目录中，可用以下命令核对文件与 `SHA256SUMS.txt` 中的哈希：

```powershell
Get-FileHash ./HPCPeek-v1.1.0-win-x64-portable.zip -Algorithm SHA256
```

现有工作流的正式发布入口是推送版本标签，不需要预先在 **Draft a new release** 中手动创建发布页。等标签工作流完成后，再在自动创建的 Release 页面点击 **Edit**（铅笔图标），保留现有下载、验证范围和 Mac 签名说明，在说明开头补充下面的更新内容，最后点击 **Update release** 保存。发布脚本重新运行时会重写说明，因此网页编辑应安排在本次构建和上传结束后。

```text
Changes in v1.1.0
- Simplified refresh age and menu labels.
- Added Clear completed to remove confirmed finished jobs from local history.
- Placed horizontal Home and Scratch quota bars below Running and Queued.
- Placed cluster labels above the vertically stacked Aire and Calder GPU matrices.
```

若某个作业失败，在 Actions 中打开红色步骤查看日志。临时网络或 runner 故障可以使用 **Re-run failed jobs**；若必须修改代码，提交修复并使用新的版本标签。不要将失败的跨平台构建描述为三个平台均已通过。工作流的 Demo 和协议检查也不代表真实学校账号已验证。

### 可选：先在本机生成 Windows ZIP

如果希望推送前自己安装体验，运行：

```powershell
./scripts/build-portable.ps1 -Version 1.1.0 -RuntimeIdentifier win-x64
```

该命令自动运行现有测试、发布、打包、解压启动检查和性能采样。成功后，安装包位于 `artifacts/v1.1.0/HPCPeek-v1.1.0-win-x64-portable.zip`，校验文件位于同目录 `SHA256SUMS.txt`。此步骤不上传 GitHub，也不创建 Release。

需要在本机同时交叉生成三个 ZIP 时，可改用 `-AllPlatforms`。Windows 上生成的 Mac 包仍需要 Mac 原生验证；正式发布优先使用上述标签工作流，让三个架构分别在原生系统上构建。GitHub 页面手动 **Run workflow** 选择 `main` 只构建并保留 Actions artifacts，不会发布 Release；版本发布使用标签推送即可。
