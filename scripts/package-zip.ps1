[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$ZipPath, [switch]$MacBundle)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$destination = [IO.Path]::GetFullPath($ZipPath)
foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/')
    if($relative -match '(^|/)portable-data/' -or $relative -match 'settings\.json$'){throw "Private settings detected in package: $relative"}
}
if($MacBundle -and $IsMacOS){
    # Native packaging preserves bundle metadata and stapled notarization information.
    # Without --keepParent the archive root contains the app and documentation directly.
    & ditto -c -k --sequesterRsrc $source $destination
    if($LASTEXITCODE -ne 0){throw 'Native macOS ZIP creation failed.'}
    return
}
$stream = [IO.File]::Open($destination, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\','/')
            if ($relative -match '(^|/)portable-data/' -or $relative -match 'settings\.json$') { throw "Private settings detected in package: $relative" }
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::SmallestSize)
            # Unix permission bits survive cross-building on Windows; downloaders need not run chmod.
            $executable = $MacBundle -and ($relative -match '\.app/Contents/MacOS/HPCPeek$' -or $file.Extension -eq '.dylib')
            $mode = if ($executable) { 33261 } else { 33188 }
            $entry.ExternalAttributes = [int]($mode -shl 16)
            $entry.LastWriteTime = [DateTimeOffset]$file.LastWriteTime
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
# ZipArchive creates Windows-hosted central-directory records on Windows. Mark the
# creator as Unix so Archive Utility/unzip interprets the permission bits above.
if ($MacBundle) {
    $zipStream = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $tailLength = [int][Math]::Min($zipStream.Length, 65557)
        $tail = [byte[]]::new($tailLength)
        $zipStream.Position = $zipStream.Length - $tailLength
        $zipStream.ReadExactly($tail)
        $eocd = -1
        for ($index = $tail.Length - 22; $index -ge 0; $index--) {
            if ([BitConverter]::ToUInt32($tail,$index) -eq 0x06054b50) { $eocd=$index; break }
        }
        if ($eocd -lt 0) { throw 'ZIP end-of-central-directory is missing.' }
        $count = [BitConverter]::ToUInt16($tail,$eocd+10)
        $centralOffset = [BitConverter]::ToUInt32($tail,$eocd+16)
        if ($count -eq 65535 -or $centralOffset -eq [uint32]::MaxValue) { throw 'ZIP64 packaging is not supported by this permission patch.' }
        $header = [byte[]]::new(46)
        $position = [long]$centralOffset
        for ($index=0; $index -lt $count; $index++) {
            $zipStream.Position=$position; $zipStream.ReadExactly($header)
            if ([BitConverter]::ToUInt32($header,0) -ne 0x02014b50) { throw 'Invalid ZIP central-directory entry.' }
            $zipStream.Position=$position+5; $zipStream.WriteByte(3)
            $position += 46 + [BitConverter]::ToUInt16($header,28) + [BitConverter]::ToUInt16($header,30) + [BitConverter]::ToUInt16($header,32)
        }
    } finally { $zipStream.Dispose() }
}
