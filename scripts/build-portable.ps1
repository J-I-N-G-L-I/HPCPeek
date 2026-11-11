[CmdletBinding()]
param(
    [ValidatePattern('^v?\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version = '1.0.0',
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string]$RuntimeIdentifier = 'win-x64',
    [string]$DotNet = '', [switch]$AllPlatforms, [switch]$SkipTests,
    [switch]$SkipSmoke, [switch]$SkipBenchmark, [switch]$SignMac
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotNet) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $DotNet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
}
$versionNumber = $Version.TrimStart('v')
$releasePath = Join-Path $repoRoot "artifacts/v$versionNumber"
New-Item -ItemType Directory -Force -Path $releasePath | Out-Null
$targets = if ($AllPlatforms) { @('win-x64','osx-arm64','osx-x64') } else { @($RuntimeIdentifier) }
$nativeRid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant() } else { '' }
Push-Location $repoRoot
try {
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') -DotNet $DotNet }
    foreach ($rid in $targets) {
        $staging = Join-Path $releasePath ('staging-' + $rid + '-' + [Guid]::NewGuid().ToString('N'))
        $publishPath = Join-Path $staging 'publish'
        $packageRoot = Join-Path $staging 'package'
        New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
        & $DotNet publish (Join-Path $repoRoot 'src/AirePeek.Desktop/AirePeek.Desktop.csproj') -c Release -r $rid --self-contained true -p:HpcPeekPortable=true -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishAot=false -p:DebugType=None -p:DebugSymbols=false -p:Version=$versionNumber -o $publishPath
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid ($LASTEXITCODE)." }
        if (Test-Path -LiteralPath (Join-Path $publishPath 'portable-data')) { throw 'Refusing to package portable user data.' }
        & (Join-Path $PSScriptRoot 'prune-publish.ps1') -PublishDirectory $publishPath -OutputPath (Join-Path $releasePath "pruning-$rid.json")
        $isMacPackage = $rid.StartsWith('osx-')
        $signed = $false
        if ($isMacPackage) {
            $bundle = Join-Path $packageRoot 'HPCPeek.app'
            $binaryRoot = Join-Path $bundle 'Contents/MacOS'
            $resources = Join-Path $bundle 'Contents/Resources'
            New-Item -ItemType Directory -Force -Path $binaryRoot,$resources | Out-Null
            Copy-Item -Path (Join-Path $publishPath '*') -Destination $binaryRoot -Recurse
            $plist = (Get-Content -LiteralPath (Join-Path $repoRoot 'packaging/macos/Info.plist') -Raw).Replace('__VERSION__', $versionNumber.Split('-')[0])
            [IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'), $plist, [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $bundle 'Contents/PkgInfo'), 'APPL????')
            $iconPath = Join-Path $repoRoot 'src/AirePeek.Desktop/Assets/app.icns'
            if (Test-Path -LiteralPath $iconPath) { Copy-Item -LiteralPath $iconPath -Destination $resources }
            if ($IsMacOS) {
                & chmod 755 (Join-Path $binaryRoot 'HPCPeek')
                if ($LASTEXITCODE -ne 0) { throw 'Cannot set macOS executable permission.' }
            }
            if ($SignMac) {
                if (-not $IsMacOS) { throw 'Developer ID signing and notarization require a macOS host.' }
                & (Join-Path $PSScriptRoot 'sign-macos.ps1') -AppPath $bundle
                $signed = $true
            } else {
                @'
UNSIGNED TEST BUILD - not Developer ID signed or notarized.
This archive is for development verification, not a Gatekeeper-approved public release.
The .app contains its runtime and stores settings in ~/Library/Application Support/HPCPeek.
Do not disable system security protections. Public distribution requires the documented signed/notarized build.
'@ | Set-Content -LiteralPath (Join-Path $packageRoot 'UNSIGNED-TEST-BUILD.txt') -Encoding utf8NoBOM
            }
        } else {
            $bundle = $null
            $binaryRoot = $packageRoot
            Copy-Item -Path (Join-Path $publishPath '*') -Destination $packageRoot -Recurse
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $packageRoot
        $docsOutput = Join-Path $packageRoot 'docs'
        New-Item -ItemType Directory -Force -Path $docsOutput | Out-Null
        foreach ($name in @('QUICKSTART.zh-CN.md','REAL-AIRE-VALIDATION.zh-CN.md','SECURITY.md','THIRD-PARTY-NOTICES.md')) {
            $doc = Join-Path $repoRoot "docs/$name"
            if (Test-Path -LiteralPath $doc) { Copy-Item -LiteralPath $doc -Destination $docsOutput }
        }
        # Only the two end-user README images travel with the app; audit galleries remain in source/artifacts.
        $readmeImages = Join-Path $repoRoot 'docs/images'
        if (Test-Path -LiteralPath $readmeImages) {
            $imageOutput = Join-Path $docsOutput 'images'
            New-Item -ItemType Directory -Force -Path $imageOutput | Out-Null
            foreach ($image in @('compact.png','expanded.png')) {
                $imagePath = Join-Path $readmeImages $image
                if (Test-Path -LiteralPath $imagePath) { Copy-Item -LiteralPath $imagePath -Destination $imageOutput }
            }
        }
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/licenses') -Destination $docsOutput -Recurse
        & (Join-Path $PSScriptRoot 'collect-licenses.ps1') -ProjectAssets (Join-Path $repoRoot 'src/AirePeek.Desktop/obj/project.assets.json') -RuntimeIdentifier $rid -PublishDirectory $publishPath -Destination (Join-Path $docsOutput 'licenses')
        # No development images, test fixtures, user settings, or symbols enter the release.
        $suffix = if ($isMacPackage -and -not $signed) { '-unsigned-test' } else { '' }
        $zipPath = Join-Path $releasePath "HPCPeek-v$versionNumber-$rid-portable$suffix.zip"
        & (Join-Path $PSScriptRoot 'package-zip.ps1') -SourceDirectory $packageRoot -ZipPath $zipPath -MacBundle:$isMacPackage
        & (Join-Path $PSScriptRoot 'inspect-package.ps1') -ZipPath $zipPath -RuntimeIdentifier $rid -OutputPath (Join-Path $releasePath "structure-$rid.json") -RequireOptimized
        & (Join-Path $PSScriptRoot 'measure-package.ps1') -ZipPath $zipPath -RuntimeIdentifier $rid -OutputPath (Join-Path $releasePath "size-$rid.json")
        $execution = 'not-run-cross-compiled'; $smoke = $null; $benchmark = $null
        if ($rid -eq $nativeRid -and -not $SkipSmoke) {
            $smoke = & (Join-Path $PSScriptRoot 'smoke-portable.ps1') -ZipPath $zipPath -RuntimeIdentifier $rid -OutputDirectory (Join-Path $releasePath ('smoke-' + $rid + '-' + [Guid]::NewGuid().ToString('N')))
            $execution = 'native-smoke-passed'
            if (-not $SkipBenchmark) {
                $benchmark = Join-Path $releasePath ('benchmark-' + $rid + '-' + [Guid]::NewGuid().ToString('N'))
                & (Join-Path $PSScriptRoot 'benchmark-portable.ps1') -ExecutablePath $smoke.Executable -RuntimeIdentifier $rid -OutputDirectory $benchmark
            }
        } elseif ($rid -eq $nativeRid) { $execution = 'not-run-skipped' }
        [ordered]@{
            product='HPCPeek'; version=$versionNumber; runtimeIdentifier=$rid; zip=$zipPath; packageDirectory=$packageRoot; bundle=$bundle;
            signedAndNotarized=$signed; nativeExecution=$execution; smoke=$smoke; benchmarkDirectory=$benchmark;
            trimmed=$true; trimMode='partial'; preservedAssemblies=@('HPCPeek','AirePeek.Core','AirePeek.Ssh','Renci.SshNet','BouncyCastle.Cryptography'); nativeAot=$false; selfContained=$true; builtOn=[Runtime.InteropServices.RuntimeInformation]::OSDescription
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releasePath "manifest-$rid.json") -Encoding utf8NoBOM
        Write-Host "Portable ZIP: $zipPath ($execution)"
    }
    $checksums = Get-ChildItem -LiteralPath $releasePath -File -Filter "HPCPeek-v$versionNumber-*.zip" | Sort-Object Name | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    $checksums | Set-Content -LiteralPath (Join-Path $releasePath 'SHA256SUMS.txt') -Encoding utf8NoBOM
} finally { Pop-Location }
