# 第三方来源

发布目录包含 .NET 运行时及 NuGet 依赖。精确包版本以项目文件、生成的 `HPCPeek.deps.json` 和包元数据为准。

- [.NET runtime](https://github.com/dotnet/runtime)：Microsoft 与 .NET contributors，MIT；运行时依赖的许可和第三方通知随发布包 `docs/licenses` 一起收集。v0.2.0 不再发布 WPF / Windows Forms 运行时。
- [Avalonia](https://github.com/AvaloniaUI/Avalonia) 12.1.4：AvaloniaUI OÜ，MIT；[随包许可证](licenses/Avalonia-LICENSE.txt)。
- [MicroCom](https://github.com/kekekeks/MicroCom) 0.11.6：Nikita Tsukanov，MIT；[随包许可证](licenses/MicroCom-LICENSE.txt)。
- [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp)：.NET Foundation contributors，MIT；其原生 Skia、HarfBuzz 和传递依赖的 LICENSE 与 THIRD-PARTY-NOTICES 从实际 NuGet 包收集到发布目录 `docs/licenses`。
- [ANGLE](https://github.com/google/angle)：Google 与 contributors，BSD-style；Windows 原生资源的许可从实际 NuGet 包收集。
- [Lucide](https://github.com/lucide-icons/lucide) 0.468.0：Lucide contributors，ISC；[随包许可证](licenses/Lucide-LICENSE.txt)。界面操作图标来自该版本的原始 SVG 几何；H 节点应用 / 托盘图标为项目原创，由 `scripts/generate-brand-icons.py` 生成。使用系统字体，不分发 Apple 字体。
- [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.0：Renci、Oleg Kapeljushnik、Gert Driesen 与 contributors，MIT；[随包许可证](licenses/SSH.NET-LICENSE.txt)。
- [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) 2.7.0：The Legion of the Bouncy Castle Inc.，MIT；[随包许可证](licenses/BouncyCastle.Cryptography-LICENSE.md)。
- Microsoft.Extensions.Logging.Abstractions 8.0.3（及其 Microsoft.Extensions 依赖）：.NET Foundation and Contributors，MIT；[随包许可证](licenses/Microsoft.Extensions-LICENSE.txt)。
- [xUnit.net](https://github.com/xunit/xunit)：测试工具，Apache-2.0，不是应用运行时依赖。

上游说明：

- [Leeds AIRE 用户文档](https://arcdocs.leeds.ac.uk/aire/welcome.html)
- [Leeds HPC / Calder 用户文档](https://arcdocs.leeds.ac.uk/calder/welcome.html)
- [Slurm squeue](https://slurm.schedmd.com/squeue.html)、[scontrol](https://slurm.schedmd.com/scontrol.html)、[sacct](https://slurm.schedmd.com/sacct.html)
- [.NET self-contained 发布](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [GitHub Release 创建与上传](https://cli.github.com/manual/gh_release_create)

HPCPeek 是独立工具，未声明得到 University of Leeds 或 Slurm 官方背书。
