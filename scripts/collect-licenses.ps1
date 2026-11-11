[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectAssets,[Parameter(Mandatory)][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
$assets=Get-Content -LiteralPath $ProjectAssets -Raw | ConvertFrom-Json -AsHashtable
$folders=@($assets.packageFolders.Keys)
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$contentFiles=@{}
$licenseIndex=@()
foreach($bundled in Get-ChildItem -LiteralPath $Destination -File){
    $hash=(Get-FileHash -LiteralPath $bundled.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if(-not $contentFiles.ContainsKey($hash)){$contentFiles[$hash]=$bundled.Name}
}
$packagePaths=@($assets.libraries.Values | Where-Object type -EQ 'package' | ForEach-Object path)
$runtime=Get-Content -LiteralPath (Join-Path $PublishDirectory 'HPCPeek.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach($framework in $runtime.runtimeOptions.includedFrameworks){
    $packagePaths += $framework.name.ToLowerInvariant()+'.runtime.'+$RuntimeIdentifier+'/'+$framework.version
}
foreach($packagePath in $packagePaths | Sort-Object -Unique){
    foreach($folder in $folders){
        $package=Join-Path $folder $packagePath
        if(-not(Test-Path -LiteralPath $package)){continue}
        foreach($file in Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '^(LICENSE|LICENCE|COPYING|THIRD-PARTY[-_]NOTICES)(\.|$)'){
            # Several native-asset packages carry byte-identical notices. Keep the complete
            # text once and retain explicit package attribution, rather than repeating it.
            $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            if(-not $contentFiles.ContainsKey($hash)){
                $name=($packagePath -replace '[/\\]','-')+'-'+$file.Name
                Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $name)
                $contentFiles[$hash]=$name
            }
            $licenseIndex += [ordered]@{package=$packagePath;sourceFile=$file.Name;bundledAs=$contentFiles[$hash];sha256=$hash}
        }
        break
    }
}
[ordered]@{method='Byte-identical license and notice texts are stored once. Each package retains an explicit mapping to the complete, unmodified text.';packages=$licenseIndex} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'package-index.json') -Encoding utf8NoBOM
