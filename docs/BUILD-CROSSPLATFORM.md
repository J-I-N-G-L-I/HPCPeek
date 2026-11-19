# Build and release HPCPeek

HPCPeek uses .NET 10, Avalonia and SSH.NET. It produces self-contained ZIPs for Windows 11 x64, Ubuntu 24.04 x86-64 desktop, and macOS 15+ on Apple Silicon and Intel. See the [README](../README.md) for installation and use.

## Requirements

- .NET 10 SDK, selected by `global.json`.
- PowerShell 7 (`pwsh`).
- Access to NuGet for dependency and target-runtime restore.
- On Linux, `unzip` and a graphical X11 or XWayland session for native verification. CI uses Xvfb, Openbox and xcompmgr to exercise the same executable on a virtual desktop.
- A Mac with Apple's command-line tools for Developer ID signing and notarization.

Run the commands below from the repository root in PowerShell. The build script looks for the platform's SDK executable in `.tools/dotnet`, then for `dotnet` on `PATH`. Use `-DotNet <path>` to select another SDK executable.

## Test and build

```powershell
./scripts/test.ps1
dotnet run --project ./src/AirePeek.Desktop -- --demo

./scripts/build-portable.ps1 -Version 1.2.0 -RuntimeIdentifier linux-x64
./scripts/build-portable.ps1 -Version 1.2.0 -RuntimeIdentifier win-x64
./scripts/build-portable.ps1 -Version 1.2.0 -RuntimeIdentifier osx-arm64
./scripts/build-portable.ps1 -Version 1.2.0 -RuntimeIdentifier osx-x64
```

To produce all four packages on one development machine, run

```powershell
./scripts/build-portable.ps1 -Version 1.2.0 -AllPlatforms
```

The build script runs unit tests, publishes the application, checks the package structure and creates ZIPs with SHA-256 checksums. It also extracts and launches the package matching the host OS and process architecture, runs the offline UI checks, and measures startup and idle memory. Cross-built packages receive structure checks only; they still need native execution tests.

`-SkipTests`, `-SkipSmoke` and `-SkipBenchmark` are available for development. Skipped checks are not evidence of a passing release. CI additionally runs the loopback SSH protocol tests; their local setup is documented in `tests/integration/README.md`.

## Output

For version `1.2.0`, output goes to `artifacts/v1.2.0/`.

| File | Purpose |
| --- | --- |
| `HPCPeek-v1.2.0-linux-x64-portable.zip` | Linux x86-64 application and runtime |
| `HPCPeek-v1.2.0-win-x64-portable.zip` | Windows application and runtime |
| `HPCPeek-v1.2.0-osx-arm64-portable-unsigned-test.zip` | Apple Silicon test build |
| `HPCPeek-v1.2.0-osx-x64-portable-unsigned-test.zip` | Intel Mac test build |
| `SHA256SUMS.txt` | Checksums for the generated ZIPs |
| `manifest-*.json`, `size-*.json`, `structure-*.json` | Build, size and package checks |
| `smoke-*/evidence/`, `benchmark-*/benchmark-result.json` | Native UI and performance results |

Signed and notarized Mac packages omit `-unsigned-test`. Native Mac packaging uses `ditto`; cross-built ZIPs preserve Unix executable permissions. Package structure alone does not establish that an app starts on another platform.

Portable builds use partial .NET trimming while preserving the application, core, SSH and cryptography assemblies. The runtime, graphics libraries and required resources remain bundled. Native AOT is not used. Debug symbols, compiler XML documentation, development screenshots, test fixtures and local settings are excluded.

Each ZIP includes the README, the project license, this build guide, third-party notices and license texts, plus the two README screenshots.

## Configuration and credentials

Windows stores settings in `portable-data` beside the executable. macOS uses `~/Library/Application Support/HPCPeek`. Linux uses `$XDG_CONFIG_HOME/HPCPeek` when the variable is an absolute path, otherwise `~/.config/HPCPeek`. Linux passwords are session-only. Optional saved passwords on Windows and macOS use the system credential store.

The public application is named HPCPeek. Source directories and internal namespaces retain `AirePeek` for compatibility.

## Storage data and verification

Home and Scratch horizontal bars sit below Running and Queued respectively and follow the selected cluster. The Calder GPU matrix sits below Aire, with each cluster name above its matrix at the top left. Paths come from the remote account's `$HOME` and `$SCRATCH`, with cluster-specific fallbacks based on the authenticated username. For example, Aire commonly uses `/users/<username>` and `/mnt/scratch/<username>`; Calder uses `/scratch-calder/users/<username>` for Scratch. No example username is hardcoded.

The monitor uses the documented commands: `quota -s` for Home; `lfs quota -h -u "$USER" /scratch` for Aire Scratch; and `lfs quota -h -p "$(id -u)" /scratch-calder` for Calder Scratch, where Leeds identifies the project ID as the account UID. The application discovers the authenticated username with `id -un`. Query roots are separate from the personal directory paths shown in tooltips. Limits use a positive soft quota when available, otherwise the hard limit. The parser accepts wrapped rows, human-readable binary units and raw space counts; it reads the filesystem's aggregate space row and does not add inode counts or individual Lustre targets to that total. The app does not substitute filesystem free space or run a recursive directory scan. A shared project quota can cover more than the displayed account directory. See Leeds' [storage filesystem documentation](https://arcdocs.leeds.ac.uk/calder/system/storage_filesystem.html) and [filesystem quota documentation](https://arcdocs.leeds.ac.uk/calder/usage/file_management/filesystem_quotas.html).

Remote shell scripts are normalized to LF before SSH execution, including when compiled from a Windows CRLF checkout. Unsupported commands, missing quota limits or an unavailable UID produce an unknown reading; hover the bar for the quota status. Previously successful readings remain dated when a later query fails. Quota queries use the existing authenticated SSH session and refresh interval. Offline Demo exercises the display but does not validate the site's live command output; before publishing, connect a real account and compare the displayed paths, limits and usage with the cluster's quota report.

## GitHub Actions and releases

Ordinary pushes and pull requests run unit tests and launch the extracted application on Windows and Linux. Both platforms also verify two-hop SSH authentication against loopback servers using the published runtime. Tag builds use native runners for all four targets, capture UI and performance evidence, then upload the four ZIPs and `SHA256SUMS.txt` to **Releases → Assets**. The workflow verifies uploaded asset names and sizes. Test reports and screenshots remain separate Actions artifacts.

After pushing the change and verifying the branch checks for that commit, create a new version tag. The Linux release example uses `v1.2.0`.

```powershell
git tag -a v1.2.0 -m "HPCPeek v1.2.0 with Linux x64 support"
git push origin v1.2.0
```

The release version comes from the tag. A manual workflow run on `main` builds all four packages for inspection in Actions. Running against a version tag also publishes to Releases using GitHub's temporary repository token.

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
./scripts/build-portable.ps1 -Version 1.2.0 -RuntimeIdentifier osx-arm64 -SignMac
```

The signing script creates a temporary keychain, signs the nested binaries and `.app`, enables hardened runtime with the required .NET JIT entitlement, waits for notarization, staples the ticket and verifies Gatekeeper acceptance. It removes the temporary keychain afterward. The `unsigned-test` suffix is removed only after these steps succeed.

See [Apple's notarization guide](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution) and [Avalonia's macOS deployment guide](https://docs.avaloniaui.net/docs/deployment/macos) for platform background.

## 中文：同步 Linux 支持并发布 v1.2.0

以下命令适用于 PowerShell 7。仓库为 `J-I-N-G-L-I/HPCPeek`，发布分支为 `main`。本次新增 Linux x86-64 桌面版，示例使用新标签 `v1.2.0`。维护者只需推送代码和标签，GitHub Actions 会生成并上传全部安装包。

### 1. 检查分支和版本号

```powershell
Set-Location D:\Projects\Aire_Peek
git remote -v
git status --short --branch
git fetch origin --tags
git tag --list v1.2.0
```

确认位于 `main`，且 `origin` 指向 `https://github.com/J-I-N-G-L-I/HPCPeek.git`。最后一条命令应无输出。如果标签已存在，后续命令请一致使用新的版本号。

### 2. 提交并同步源代码

```powershell
./scripts/test.ps1
git diff --check
git add -- .github/workflows/ci-release.yml README.md docs/BUILD-CROSSPLATFORM.md docs/THIRD-PARTY-NOTICES.md docs/licenses/Tmds.DBus-LICENSE.txt scripts src tests
git diff --cached --stat
git diff --cached --check
git commit -m "Add Linux x64 desktop support and release packaging"
git push origin main
```

逐步执行，遇到失败先处理再继续。如果远端已有新提交，提交本地修改后执行 `git fetch origin` 和 `git rebase origin/main`，解决可能的冲突并重新验证，然后再推送。构建产物位于 Git 忽略的 `artifacts/` 下。仓库使用受保护的 `main` 时，请通过拉取请求合并，然后切回 `main` 并执行 `git pull --ff-only origin main`。

### 3. 给通过检查的提交打标签

打开 **Actions → Test and portable desktop releases**，等本次 `main` 提交的 Windows 和 Linux 检查都通过。核对 `git log -1 --oneline` 与该次运行的提交一致，再执行以下命令。[仓库 Actions](https://github.com/J-I-N-G-L-I/HPCPeek/actions)

```powershell
git tag -a v1.2.0 -m "HPCPeek v1.2.0 with Linux x64 support"
git push origin v1.2.0
```

### 4. 核对发布包

在 Actions 打开与 `v1.2.0` 对应的运行，等待 `release` 作业成功。在 **HPCPeek v1.2.0 → Assets** 下核对以下文件。[仓库 Releases](https://github.com/J-I-N-G-L-I/HPCPeek/releases)

- `HPCPeek-v1.2.0-linux-x64-portable.zip`
- `HPCPeek-v1.2.0-win-x64-portable.zip`
- 两个 Mac ZIP，文件名分别包含 `osx-arm64` 和 `osx-x64`
- `SHA256SUMS.txt`

未配置上文六项 macOS 签名凭据时，两个 Mac 文件名带 `-unsigned-test`，整个 Release 显示为 **Pre-release**。Windows 与 Linux 包也在该页面下载。Linux 安装和 SHA-256 校验命令见 [README](../README.md#run-on-linux)。手动在 `main` 上选择 **Run workflow** 可以预演全平台打包，实际发布由版本标签触发。

下节是已发布 `v1.1.0` 的历史配额修复流程。本次 Linux 版本使用上方的新标签流程。

## 中文：保留 v1.1.0 替换配额修复包

旧 Windows 包中的多行远端命令带有 CRLF 换行，发送到 Linux shell 后可能无法按预期执行；旧配额查询还依赖挂载点推断和目录项目 ID，解析器只接受原始整数。修复版统一远端脚本为 LF，并采用学校文档中的查询方式、兼容带单位和换行的配额输出。离线回归与打包检查不能替代真实账户验证；先用修复包登录，确认 Home / Scratch 与终端查询一致，再替换公开下载。

保留 `v1.1.0` 的方式是：提交修复 → 普通 CI 通过 → 将同一个版本标签明确更新到修复提交 → 标签工作流重建三平台包并替换同名资源。这样下载页地址和文件名保持一致，但源码标签、ZIP 内容及 SHA-256 都会改变；已经下载旧包的用户需要重新下载。

### 1. 确认此 Release 允许修改

打开 [v1.1.0 Release](https://github.com/J-I-N-G-L-I/HPCPeek/releases/tag/v1.1.0)。如果标题下有 **Immutable** 标记，GitHub 不允许移动关联标签或替换资源，必须改用 `v1.1.1`；删除该 Release 也不能重新使用其标签名称。若仓库标签保护规则拒绝更新，也应使用新版本，不要关闭保护来强行覆盖。依据：[GitHub 不可变 Release 说明](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)。

以下步骤仅适用于允许修改的 Release。本次保留版本号是针对已发布配额故障的明确修订，不是日常发布时移动旧标签的建议。

### 2. 提交修复并推送 main

在 PowerShell 中逐段运行，任何命令失败都先停止：

```powershell
Set-Location D:\Projects\Aire_Peek
git status --short
git diff --check
git add -- README.md docs src tests
git diff --cached --stat
git commit -m "Fix quota collection and parsing on Leeds clusters"
git push origin main
```

到 [Actions](https://github.com/J-I-N-G-L-I/HPCPeek/actions) 等待此次 `main` 提交的 **Test and portable desktop releases** 检查通过。若远端有新提交，先正常同步并解决冲突，不要强推 `main`。

### 3. 有条件地更新 v1.1.0 标签

先确认 `git log -1 --oneline` 是刚才通过 CI 的修复提交。以下命令读取远端旧标签的对象 ID，再将本地标签指向当前提交。最后的推送只在远端仍是刚读到的旧标签时执行，避免覆盖别人在这期间更新的标签。

```powershell
git log -1 --oneline
$releaseTagRef = 'refs/tags/v1.1.0'
$releaseTagLines = @(git ls-remote --refs origin $releaseTagRef)
if ($LASTEXITCODE -ne 0 -or $releaseTagLines.Count -ne 1) {
    throw 'Cannot identify the existing remote v1.1.0 tag. Stop here.'
}
$previousReleaseTag = ($releaseTagLines[0] -split '\s+')[0]
git tag -f -a v1.1.0 -m "HPCPeek v1.1.0 quota hotfix" HEAD
if ($LASTEXITCODE -ne 0) { throw 'Local tag update failed. Stop here.' }
git push "--force-with-lease=${releaseTagRef}:$previousReleaseTag" origin "${releaseTagRef}:${releaseTagRef}"
```

这里更新的只有 `refs/tags/v1.1.0`，没有强推主分支。若推送显示 lease 不匹配，先检查远端为何变化，不要换成无条件 `--force`。此形式使用明确的旧对象 ID，参见 [Git push 文档](https://git-scm.com/docs/git-push)。

### 4. 等待新的标签构建替换资源

回到 Actions，打开这次标签更新新触发的运行记录，确认提交编号是修复提交。不要直接重跑之前失败版本的旧运行记录：旧记录仍可能指向旧提交。

等待 `test`、三个 `package` 和 `release` 全部成功。现有发布脚本会更新同一个 Release，并通过 `gh release upload --clobber` 替换同名 ZIP 和 `SHA256SUMS.txt`，最后核对资源名和大小。上传不是一次性事务，必须等发布作业完成后再通知用户下载。依据：[GitHub CLI 上传与覆盖资源](https://cli.github.com/manual/gh_release_upload)。

发布完成后，在 Release 的 **Edit** 页面为说明补充“v1.1.0 quota hotfix”、修复提交编号和替换日期。保留平台验证与 Mac 签名说明。发布脚本会重写说明，因此这一步放在上传结束之后。

### 5. 确认下载的是修复包

重新下载 Windows ZIP 和新的 `SHA256SUMS.txt`，在下载目录执行：

```powershell
Get-FileHash ./HPCPeek-v1.1.0-win-x64-portable.zip -Algorithm SHA256
```

结果应与新的校验文件一致。退出旧程序，将修复 ZIP 完整解压到新目录，再复制旧目录中的 `portable-data`；不要将新旧程序文件混放。启动新目录的 `HPCPeek.exe`，登录 Aire 后检查两个容量条。版本号仍为 `1.1.0`，因此区分旧包与修复包应使用本次 Release 的新 SHA-256，而不是只看文件名或版本号。

若维护本地仓库的其他人已经获取过旧标签，需要在知晓此修订后显式刷新本地 `v1.1.0`；普通 `git fetch --tags` 可能拒绝覆盖旧标签。这不影响只下载 ZIP 的用户。
