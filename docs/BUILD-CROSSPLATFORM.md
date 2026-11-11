# 构建与三平台便携包

HPCPeek 使用 .NET 10、Avalonia 和原有 SSH.NET / Slurm 逻辑。Windows x64、macOS Apple Silicon（osx-arm64）与 Intel（osx-x64）分别发布，包内包含运行时。Windows 用户无需管理员权限或另装 .NET；macOS 使用可移动的 `HPCPeek.app`。默认界面语言为英语。

开发机需要 .NET 10 SDK 和 PowerShell 7；下载包的普通用户不需要。支持的 macOS 最低版本是 15，依据 [.NET 10 支持的系统](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。SDK 版本由仓库 `global.json` 选择。

```powershell
./scripts/test.ps1
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier win-x64
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-arm64
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-x64
# 一台开发机交叉生成全部包；只有本机架构会运行启动与性能验收
./scripts/build-portable.ps1 -Version 1.0.0 -AllPlatforms
```

脚本优先使用仓库 `.tools/dotnet/dotnet.exe`，也可传 `-DotNet <SDK executable>`。网络受限环境需先完成 NuGet 包和对应 RID 运行时的恢复。`-SkipTests`、`-SkipSmoke`、`-SkipBenchmark` 适合重复调试，但任何跳过都会写入证据，不可据此声称验收通过。

构建产物位于 `artifacts/v1.0.0/`：

- `HPCPeek-v1.0.0-win-x64-portable.zip`
- `HPCPeek-v1.0.0-osx-arm64-portable-unsigned-test.zip`
- `HPCPeek-v1.0.0-osx-x64-portable-unsigned-test.zip`
- `SHA256SUMS.txt`、`size-*.json`、`manifest-*.json`、本机 `smoke-*/evidence/` 和 `benchmark-*/benchmark-result.json`

macOS 包含 `.app/Contents/MacOS` 的可执行文件、运行时和依赖，`Info.plist` 指定应用名、版本、后台菜单栏模式及系统要求。Windows 交叉生成 ZIP 时编写明确的 Unix 权限和 Unix ZIP 创建者字段，保证 apphost 的可执行位随 ZIP 保存；原生 Mac 用 `ditto` 保留 bundle 元数据与装订信息。结构校验不等于在 macOS 上实际运行；原生 runner 还会用 `ditto` 解压并检查可执行位，然后运行 Demo。

Windows 保留便携目录的 `portable-data`。macOS 默认写 `~/Library/Application Support/HPCPeek`，保持签名 `.app` 不变；通过设置导入导出非敏感配置。可选记住密码仅使用 Windows Credential Manager / macOS Keychain，不把凭据写入上述目录或迁移文件。应用日志、用户配置、密码、验证码和任何真实账号信息都不得进入发布 ZIP。包内只附用户 README、快速上手、安全与真实账号检查说明、必要许可证及两张精简界面图；完整设计图册、测试夹具、构建报告、PDB 与编译器 XML 开发文档不进入包。

v1.0.0 的便携发布使用 `HpcPeekPortable=true` 专用配置，启用 .NET 的 `PublishTrimmed=true` / `TrimMode=partial`。应用、Core、SSH 封装、SSH.NET 与密码学程序集显式完整保留；便携设置使用源生成 JSON 元数据，关闭 JSON 反射默认回退。链接器分析支持裁剪的框架路径，而不是根据 DLL 名称猜测删除依赖。仍保留运行时、Skia / HarfBuzz、必要平台库与资源，不使用 Native AOT。开发和普通测试默认不裁剪，因此最终便携包还必须完成解压后的原生界面和 SSH 协议检查，不能用普通测试结果替代发布配置验证。

macOS 新配置不存在时依次检查旧 `~/Library/Application Support/AirePeek/settings.json`、`~/Library/Application Support/AIRE Peek/settings.json`，复制有效配置并保留旧文件和主机指纹。新目录已有配置始终优先，损坏文件也不以旧文件静默覆盖。Windows `portable-data` 位置不变。历史 `AIRE-Peek-...zip`、`AirePeek-v...zip` 与版本记录保持原名；从 v0.4.0 起使用 `HPCPeek-v...zip`、`HPCPeek.exe` 和 `HPCPeek.app`。源码目录、解决方案和内部命名空间仍沿用 AirePeek，以保持现有逻辑和测试引用。

## CI 与实际上传 Release

普通提交 / PR 运行单元测试、双跳 SSH.NET 回环协议测试，并在 Windows 上构建 ZIP、解压和启动离线 Demo。`vX.Y.Z` 标签触发三个原生任务：Windows `windows-latest`、Apple Silicon `macos-15`、Intel `macos-15-intel`。这些架构标签依据 [GitHub 官方 runner 列表](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)。每个任务保留浅色、深色、缩放与菜单图、包体积及性能 JSON；全部成功后才执行 `publish-release.ps1`。

Release 任务使用仓库短期 `GITHUB_TOKEN`，把三个 ZIP 和重新计算的 `SHA256SUMS.txt` 实际上传到对应 GitHub Release，并通过 API 核对每个 asset 名称与字节数。仅上传 Actions artifact 不算完成发布。未配置仓库 remote 或无 GitHub runner 的本地环境不会伪称已上传。需要签名凭据的任务不在 PR 上运行。

## macOS 正式签名与公证

未提供凭据时，文件名和包内说明都标为 `unsigned-test`；包含这样的包的 GitHub Release 自动标为 prerelease。这是开发测试构建，不能声称下载后通过 Gatekeeper 的公开发行体验。不得要求用户关闭系统安全机制。

要发布正式 macOS 包，在 GitHub 仓库设置六个 Secrets：`MACOS_CERTIFICATE_BASE64`（Developer ID Application 的 p12，Base64）、`MACOS_CERTIFICATE_PASSWORD`、`MACOS_SIGNING_IDENTITY`、`APPLE_ID`、`APPLE_TEAM_ID` 和 `APPLE_APP_SPECIFIC_PASSWORD`。六个全部存在才启用签名；只填部分则失败，避免意外退回未签名发布。也可在自己的 Mac 上提供这些环境变量并执行：

```powershell
./scripts/build-portable.ps1 -Version 1.0.0 -RuntimeIdentifier osx-arm64 -SignMac
```

`sign-macos.ps1` 建立临时 keychain，分别给所有嵌套 Mach-O 与外层 `.app` 做 Developer ID 签名，启用 hardened runtime、时间戳和 .NET JIT entitlement；验证签名后用 `notarytool --wait` 等待 Accepted，再 staple、validate 并做 Gatekeeper assessment。只在这些步骤全部通过后去除 ZIP 名的 `unsigned-test` 后缀。凭据与临时 keychain 在 finally 中清理，不更换用户默认 keychain。

实现参考 [Avalonia macOS 部署](https://docs.avaloniaui.net/docs/deployment/macos) 和 [Apple 公证指南](https://developer.apple.com/documentation/security/notarizing-macos-software-before-distribution)。本地 Windows 环境无法执行这些 Mac 专用检查；没有签名凭据时，签名流程代码只经过静态审查，需原生签名构建确认。
