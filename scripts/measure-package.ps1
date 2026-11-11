[CmdletBinding()]
param([Parameter(Mandatory)][string]$ZipPath,[Parameter(Mandatory)][string]$RuntimeIdentifier,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$zip = Get-Item -LiteralPath $ZipPath
$archive = [IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $files = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and -not $_.FullName.StartsWith('__MACOSX/') })
    $metadataBytes=($archive.Entries | Where-Object FullName -Like '__MACOSX/*' | Measure-Object Length -Sum).Sum
    $size = ($files | Measure-Object Length -Sum).Sum
    $largest = @($files | Sort-Object Length -Descending | Select-Object -First 3 | ForEach-Object {
        [ordered]@{path=$_.FullName;bytes=$_.Length;compressedBytes=$_.CompressedLength}
    })
    [ordered]@{
        runtimeIdentifier=$RuntimeIdentifier; archive=$zip.Name; zipBytes=$zip.Length; zipMiB=[Math]::Round($zip.Length/1MB,2);
        uncompressedBytes=[long]$size; uncompressedMiB=[Math]::Round($size/1MB,2); fileCount=$files.Count; largestFiles=$largest; macMetadataBytes=[long]$metadataBytes;
        method='ZIP length and sum of uncompressed logical file-entry lengths; excludes __MACOSX resource metadata, recorded separately. Not filesystem allocation.';
        optimization='SDK partial trimming with whole-assembly app/Core/SSH.NET/cryptography preservation and source-generated JSON. No Native AOT or speculative DLL deletion. End-user docs, legal notices and two README images only; no development galleries or symbols.'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
} finally { $archive.Dispose() }
