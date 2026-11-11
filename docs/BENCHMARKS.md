# 体积、启动与空闲内存测量

指标必须关联实际 ZIP、架构、运行系统和原始 JSON。交叉编译成功不代表目标平台启动成功；Windows 不产生 macOS 启动时间或内存数字。未执行项写“未实测”，不可用估值或另一平台的数字代替。

## 比较基线

每次优化与前一版实际 ZIP 比较，保持架构和“文件逻辑长度”的口径一致。v1.0.0 开发的体积基线是 v0.5.0：Windows ZIP / 解压为 49.34 / 110.94 MiB，Mac ARM 为 46.97 / 117.66 MiB，Mac Intel 为 49.22 / 111.10 MiB。本机旧包和原始 JSON 保留在 `artifacts/v0.5.0/`，不加入 Git；新版本的结果以自身 `size-*.json` 为准。不能从体积变化推断启动或内存提升。

## 包体积

`measure-package.ps1` 直接读取每个发布 ZIP：下载大小用 ZIP 文件字节数；解压大小用逻辑文件 entry 的 uncompressed length 相加，不含文件系统分配单元、操作系统缓存或首次运行配置。原生 Mac `ditto` 的 `__MACOSX` 元数据另列，不算作解压后的普通文件。统一 1 MiB = 1,048,576 字节。`size-<rid>.json` 保留文件数、最大的三个文件及各自压缩前后字节数，便于估算具体瘦身空间。

```powershell
./scripts/measure-package.ps1 -ZipPath artifacts/v1.0.0/HPCPeek-v1.0.0-win-x64-portable.zip -RuntimeIdentifier win-x64 -OutputPath artifacts/v1.0.0/size-win-x64.json
```

发布包不带 WPF / Windows Forms、Python、测试框架、开发 SDK 或完整设计图册。v1.0.0 用 `HpcPeekPortable=true` 启用 .NET partial trimming，显式保留应用和认证 / 密码学程序集，并使用源生成 JSON 元数据；不使用 Native AOT。`prune-publish.ps1` 另外移除 PDB，以及同名 DLL 旁确认具有 `<doc><assembly>` 结构的编译器 XML 文档；`pruning-<rid>.json` 逐文件记录这些清理项。链接器缩减和额外文件清理应分别记录，不能把它们混成逐文件删除清单。CoreCLR、Skia、HarfBuzz、必要平台库、资源和配置保留；不靠猜测删 DLL。系统字体无需随包分发字体文件，用户文档仅附两张精选界面图。最终 ZIP 的原生 UI 与协议测试通过后，才将尺寸变化作为已验证结果报告。

## 解压后启动验收

`smoke-portable.ps1` 使用新目录解压真实发布 ZIP，把 `DOTNET_ROOT` 指向空目录，并清理 PATH 中的开发 SDK，检查包内 runtimeconfig 和 CoreCLR，再启动 apphost。Mac 使用 `ditto` 并检查 `test -x`，不偷偷补 `chmod`。应用 `--smoke-test <output>` 在同一真实桌面界面加载离线 Demo，输出 `smoke-result.json` 和以下截图；非零退出、缺图、空图或断言失败均使 CI 失败。

- `compact-{125,150,200}.png` 与 `expanded-{125,150,200}.png`
- `compact-dark-150.png`、`expanded-dark-150.png`
- `menu-light.png`、`menu-dark.png`
- `connection-direct-*`、`connection-gateway-*`、`authentication-password-*`、`authentication-duo-*`、`host-fingerprint-*`、`settings-{0,1,2}-*`：v0.3.0 的连接、认证、指纹与三页设置，`*` 分别为 `light.png`、`dark.png`，均为真实窗口配合模拟数据
- `compact-100.png`、`expanded-100.png`、`disconnected-150.png`、`empty-150.png`、`partial-150.png`
- v0.4.0 增加 `expanded-calder-{light,dark}-150.png`、`connection-calder-{light,dark}.png`、`settings-calder-{light,dark}.png` 和 `notification-{completed,failed,timeout}-{light,dark}.png`，覆盖第二集群与三个最终作业状态通知。

v0.5.0 新增 `connection-remember-{light,dark}.png`，显示用户主动选择的设备密码记住模式；向导切换集群不得把另一个集群自动设为记住。截图使用合成账号字段，不含真实密码。

这些图是应用布局按目标像素密度渲染的证据，不能单独证明换显示器时系统 DPI 事件、真实输入法、VoiceOver / Narrator 或托盘点击路径全部通过。最终报告应区分自动截图与人工交互检查。

## 启动与空闲性能

```powershell
./scripts/benchmark-portable.ps1 -ExecutablePath '<extracted apphost>' -RuntimeIdentifier win-x64 -OutputDirectory artifacts/benchmark-local
# 在对应 Mac 上分别使用 osx-arm64 或 osx-x64 和 .app/Contents/MacOS/HPCPeek
```

默认 3 次独立进程，每次独立 Demo 配置。计时从外部 `Process.Start` 前开始，至应用第一个包含 Demo 快照的紧凑窗完成渲染后写出 `benchmark-ready.json`，每 10 ms 检查一次；这包括进程、运行时和界面初始化。记录首次启动与三次中位数。文件系统缓存未清空，机器未重启，因此准确名称为“新进程启动”，不是严格的重启冷缓存启动；不要把第二次暖缓存的数字改称冷启动。

窗口保持可见且为紧凑 Demo，不发 SSH 请求。每个进程就绪后每秒采样，默认持续 30 秒（`-IdleSeconds` 可设为 15–300 秒），使用最后 10 个样本的均值，再取三次均值的中位数。另记录空闲期 CPU 时间。测量完成只结束脚本自己启动的子进程。

Windows 保留总工作集（`workingSetBytes`，含可共享驻留页）和私有提交量（`privateBytes`，不一定全部驻留在 RAM），并增加私有工作集（`privateWorkingSetBytes`）。后者通过原生 PSAPI `QueryWorkingSet` 统计 `Shared` 位为 0 的驻留页，再乘以系统页大小；它更接近活动进程在任务管理器中的内存值，但采样时刻和任务管理器的活动进程口径仍可能造成差异。这三项不能互相替代，尤其不能把私有工作集与旧报告的总工作集直接比较后声称优化幅度。macOS 继续用 `/bin/ps -o rss=` 记录 RSS，两项 Windows 私有内存指标均为 `null`，不以 RSS 冒充。脚本不主动回收 GC，也不强制清空工作集。

原始 `benchmark-result.json` 包含系统版本、进程架构、CPU、物理内存、每次启动毫秒数及所有采样值，同时记录 apphost / runtimeconfig 的 SHA-256 和继承的 `DOTNET_GCConserveMemory` 环境变量。后台任务、病毒扫描、桌面合成器和 runner 负载会影响结果；不同机器上的数字只能按各自环境阅读。原生 Mac runner 可生成其自己的证据；本地 Windows 交叉编译只能测 ZIP 大小与包结构。

## v1.0.0 内存复核（2026-11-11）

同一 Windows 11 x64 / i5-12600KF 机器，紧凑双集群离线 Demo；优化前后各三个新进程，每次空闲 30 秒，按上述最后 10 个样本均值的中位数比较。优化仅复用同一 GPU 快照的汇总、避免每秒重新绑定登录状态颜色并减少重复状态更新；秒级作业计时和采样策略保留。

| 指标 | 优化前 | 优化后 |
| --- | ---: | ---: |
| 私有驻留工作集 | 44.83 MiB | 44.76 MiB |
| 总工作集 | 134.15 MiB | 133.75 MiB |
| 私有提交内存 | 108.24 MiB | 107.40 MiB |
| 新进程启动中位数 | 1,485 ms | 1,467 ms |

常驻内存差异在正常波动范围内，不能宣称显著下降。真实登录后的约 63 MB 用户观察值未在同一条件下复现，不与 Demo 做前后比较；预计仍在相近水平。另以原程序做一次进程级 `DOTNET_GCConserveMemory=5` 试验，私有工作集为 45.01 MiB，无明确收益，因此没有纳入运行时配置。没有强制 GC 或清空工作集。

原始证据保留在本地 `artifacts/memory-v100/{baseline,optimized,conserve5-experiment}/benchmark-result.json`（不提交 Git）；`artifacts/v1.0.0/memory-optimization.json` 关联最终 ZIP 的应用程序集与测量样本。Mac 包可交叉构建，内存仍需在真实 Mac 测量。

## 验证范围

离线 Demo 和回环 SSH 协议测试不依赖 Leeds 账号，能够验证数据处理、复用连接和模拟认证。真实 Aire / Calder 双重 Duo 策略、用户可见队列、GPU 字段、系统通知权限和实际 VPN 中断仍需账号持有人检查；操作步骤见 `REAL-AIRE-VALIDATION.zh-CN.md`。正式 macOS Gatekeeper 下载体验还需要 Developer ID、公证、装订后的下载包与真实 Mac。
