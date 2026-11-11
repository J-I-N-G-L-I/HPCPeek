[CmdletBinding()]
param([Parameter(Mandatory)][string]$ZipPath,
    [Parameter(Mandatory)][ValidateSet('win-x64','osx-arm64','osx-x64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$OutputPath,[switch]$RequireOptimized)
$ErrorActionPreference='Stop'
$zip=(Resolve-Path -LiteralPath $ZipPath).Path
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    $prefix=if($RuntimeIdentifier.StartsWith('osx-')){'HPCPeek.app/Contents/MacOS/'}else{''}
    $exeName=if($prefix){'HPCPeek'}else{'HPCPeek.exe'}
    $nativeRuntime=if($prefix){'libcoreclr.dylib'}else{'coreclr.dll'}
    foreach($name in @($exeName,$nativeRuntime,'HPCPeek.dll','HPCPeek.runtimeconfig.json','HPCPeek.deps.json','Renci.SshNet.dll')){
        if(-not $archive.GetEntry($prefix+$name)){throw "Missing self-contained package file: $prefix$name"}
    }
    foreach($entry in $archive.Entries){
        if($entry.FullName -match '(^|/)(portable-data|\.git|\.codex|\.agents)/' -or $entry.FullName -match '(^|/)settings\.json$'){
            throw "Private/development data entered the release: $($entry.FullName)"
        }
        if($entry.FullName.EndsWith('.pdb')){throw 'Debug symbols entered the release.'}
    }
    $configReader=[IO.StreamReader]::new($archive.GetEntry($prefix+'HPCPeek.runtimeconfig.json').Open())
    try{$config=$configReader.ReadToEnd() | ConvertFrom-Json}finally{$configReader.Dispose()}
    if(-not $config.runtimeOptions.includedFrameworks){throw 'The package requires an external .NET runtime.'}
    if($RequireOptimized){
        if($config.runtimeOptions.configProperties.'System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault' -ne $false){
            throw 'The portable serialization profile was not applied.'
        }
        foreach($designer in @('Avalonia.DesignerSupport.dll','Avalonia.Remote.Protocol.dll')){
            if($archive.GetEntry($prefix+$designer)){throw "Unused IDE component entered the optimized package: $designer"}
        }
    }
    $entry=$archive.GetEntry($prefix+$exeName)
    $exeStream=$entry.Open(); $memory=[IO.MemoryStream]::new()
    try{$exeStream.CopyTo($memory);$bytes=$memory.ToArray()}finally{$exeStream.Dispose();$memory.Dispose()}
    if($prefix){
        if([Convert]::ToHexString($bytes[0..3]) -ne 'CFFAEDFE'){throw 'Expected a 64-bit little-endian Mach-O apphost.'}
        $machine=[BitConverter]::ToUInt32($bytes,4)
        $expected=if($RuntimeIdentifier -eq 'osx-arm64'){0x0100000C}else{0x01000007}
        if($machine -ne $expected){throw "Wrong macOS architecture in ZIP: $machine"}
        $mode=($entry.ExternalAttributes -shr 16) -band 0xFFFF
        if(($mode -band 0x49) -ne 0x49){throw 'macOS apphost executable bits are missing from ZIP.'}
        $plistEntry=$archive.GetEntry('HPCPeek.app/Contents/Info.plist')
        if(-not $plistEntry){throw 'macOS Info.plist is missing.'}
        $plistReader=[IO.StreamReader]::new($plistEntry.Open())
        try{$plist=[xml]$plistReader.ReadToEnd()}finally{$plistReader.Dispose()}
        if($plist.OuterXml.Contains('__VERSION__')){throw 'Unresolved version in Info.plist.'}
        foreach($key in @('CFBundleExecutable','CFBundleName','CFBundleDisplayName')){
            $value=$plist.SelectSingleNode("/plist/dict/key[text()='$key']/following-sibling::*[1]")
            if($value.InnerText -ne 'HPCPeek'){throw "Wrong public product name in macOS $key."}
        }
        $bundleId=$plist.SelectSingleNode("/plist/dict/key[text()='CFBundleIdentifier']/following-sibling::*[1]")
        if($bundleId.InnerText -ne 'io.github.hpcpeek.app'){throw 'Wrong macOS bundle identifier.'}
        $architecture=if($machine -eq 0x0100000C){'arm64'}else{'x64'}
    }else{
        if([Convert]::ToHexString($bytes[0..1]) -ne '4D5A'){throw 'Expected a Windows PE apphost.'}
        $pe=[BitConverter]::ToInt32($bytes,0x3C)
        if([BitConverter]::ToUInt32($bytes,$pe) -ne 0x00004550 -or [BitConverter]::ToUInt16($bytes,$pe+4) -ne 0x8664){throw 'Expected a Windows x64 PE apphost.'}
        $architecture='x64';$mode=$null
    }
    [ordered]@{success=$true;product='HPCPeek';runtimeIdentifier=$RuntimeIdentifier;archive=[IO.Path]::GetFileName($zip);apphostArchitecture=$architecture;
        apphostUnixMode=$mode;selfContained=$true;containsSshNet=$true;symbolsAndPrivateSettingsAbsent=$true;optimizedProfileChecked=[bool]$RequireOptimized;
        scope='Static ZIP and executable-header validation only; does not assert target-OS launch, authentication or GUI behavior.'} |
        ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
}finally{$archive.Dispose()}
