[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $PublishDirectory).Path
$boundary=$root.TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
$removed=@()
foreach($file in Get-ChildItem -LiteralPath $root -File -Recurse){
    $remove=$file.Extension -ieq '.pdb'
    if($file.Extension -ieq '.xml' -and (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($file.FullName,'.dll')))){
        # Only compiler XML documentation beside the matching assembly, never arbitrary XML configuration.
        try{$xml=[xml](Get-Content -LiteralPath $file.FullName -Raw);$remove=$null -ne $xml.doc.assembly.name}catch{$remove=$false}
    }
    if(-not $remove){continue}
    $resolved=(Resolve-Path -LiteralPath $file.FullName).Path
    if(-not $resolved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Publish pruning escaped the intended directory.'}
    $removed += [ordered]@{path=[IO.Path]::GetRelativePath($root,$resolved);bytes=$file.Length;reason=if($file.Extension -ieq '.pdb'){'debug symbols; not loaded for execution'}else{'compiler XML documentation beside matching assembly'}}
    Remove-Item -LiteralPath $resolved -Force
}
[ordered]@{publishDirectory=$root;removed=$removed;removedBytes=[long](($removed | ForEach-Object {$_.bytes} | Measure-Object -Sum).Sum);
    retained='All linker-selected executable and managed/native runtime code, resource assemblies, localization and runtime configuration. This step removes symbols/documentation only; no speculative DLL deletion.'} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
