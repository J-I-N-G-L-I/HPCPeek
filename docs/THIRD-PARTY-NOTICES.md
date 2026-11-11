# Third-party notices

HPCPeek bundles the .NET runtime and libraries listed below. For the exact versions in a release, see its `HPCPeek.deps.json` and `docs/licenses/package-index.json`. The index maps packages to their complete, unmodified license and notice files; identical texts are stored once.

## Runtime libraries and assets

| Component | Attribution and license |
| --- | --- |
| [.NET runtime](https://github.com/dotnet/runtime) | Microsoft and .NET contributors. MIT; additional third-party notices are included in the release. |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | AvaloniaUI OÜ. [MIT](licenses/Avalonia-LICENSE.txt). |
| [MicroCom](https://github.com/kekekeks/MicroCom) | Nikita Tsukanov. [MIT](licenses/MicroCom-LICENSE.txt). |
| [SkiaSharp / HarfBuzzSharp](https://github.com/mono/SkiaSharp) | .NET Foundation contributors. MIT; notices for native Skia, HarfBuzz and their dependencies are collected from the NuGet packages. |
| [ANGLE](https://github.com/google/angle) | Google and contributors. BSD-style; notices accompany the Windows native libraries. |
| [Lucide](https://github.com/lucide-icons/lucide) | Lucide contributors, with portions from Feather by Cole Bemis. [ISC and attribution](licenses/Lucide-LICENSE.txt). UI icons use SVG geometry from Lucide 0.468.0. |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | Renci, Oleg Kapeljushnik, Gert Driesen and contributors. [MIT](licenses/SSH.NET-LICENSE.txt). |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) | The Legion of the Bouncy Castle Inc. [MIT](licenses/BouncyCastle.Cryptography-LICENSE.md). |
| Microsoft.Extensions libraries | .NET Foundation and Contributors. [MIT](licenses/Microsoft.Extensions-LICENSE.txt). |

The H-shaped application and tray icons are original HPCPeek artwork. The app uses system fonts and does not distribute Apple fonts. License texts in `licenses/` are preserved as provided by their upstream authors.

## Development tools

[xUnit.net](https://github.com/xunit/xunit) is used for tests under Apache-2.0. It is not included in the application runtime. Build scripts collect additional dependency notices into each release's `docs/licenses/` directory.

## Cluster documentation

- University of Leeds: [Aire](https://arcdocs.leeds.ac.uk/aire/welcome.html) and [Calder](https://arcdocs.leeds.ac.uk/calder/welcome.html).
- Slurm commands: [squeue](https://slurm.schedmd.com/squeue.html), [scontrol](https://slurm.schedmd.com/scontrol.html) and [sacct](https://slurm.schedmd.com/sacct.html).

HPCPeek is independent software, not affiliated with or endorsed by the University of Leeds or Slurm.
