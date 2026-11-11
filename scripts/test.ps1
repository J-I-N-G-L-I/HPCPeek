[CmdletBinding()]
param([string]$DotNet = '')
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotNet) {
    $localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
    $DotNet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
}
Push-Location $repoRoot
try {
    & $DotNet test (Join-Path $repoRoot 'AirePeek.slnx') -c Release --logger 'trx' --results-directory (Join-Path $repoRoot 'artifacts/test-results')
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($LASTEXITCODE)." }
} finally { Pop-Location }
