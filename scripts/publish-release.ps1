[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Tag,
    [Parameter(Mandatory)][string]$AssetDirectory,[string]$Repository=$env:GH_REPO)
$ErrorActionPreference='Stop'
if(-not $Repository){throw 'GitHub repository is required.'}
$assetRoot=(Resolve-Path -LiteralPath $AssetDirectory).Path
$zips=@(Get-ChildItem -LiteralPath $assetRoot -File -Filter '*.zip')
if($zips.Count -ne 3){throw 'A release requires exactly three architecture ZIPs.'}
foreach($rid in @('win-x64','osx-arm64','osx-x64')){
    $matching=@($zips | Where-Object { $_.Name -match ('^HPCPeek-'+[Regex]::Escape($Tag)+'-'+$rid+'-portable(-unsigned-test)?\.zip$') })
    if($matching.Count -ne 1){throw "Expected exactly one package for $rid."}
}
$checksums=Join-Path $assetRoot 'SHA256SUMS.txt'
$zips | Sort-Object Name | ForEach-Object {'{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name} |
    Set-Content -LiteralPath $checksums -Encoding utf8NoBOM
$unsigned=@($zips | Where-Object Name -Like '*-unsigned-test.zip').Count -gt 0
$notes=Join-Path $assetRoot 'release-notes.md'
$releaseText=@"
HPCPeek $Tag — compact desktop Slurm monitor for Aire and Calder. English interface by default.

Download the ZIP for your architecture and extract the complete archive. Windows includes its .NET runtime; macOS includes a movable .app and stores settings in Application Support.

Each package was built, extracted, launched in offline Demo and benchmarked on its native GitHub runner. Separate workflow evidence contains layout captures, test results and performance measurements. These checks use simulated data and do not validate real Aire or Calder accounts or campus Duo policy.

Verify SHA256SUMS.txt before use. No HPC passwords or credentials are included.
"@
if($unsigned){$releaseText += "`n`nmacOS assets marked unsigned-test are development test builds, without Developer ID signatures or notarization. Gatekeeper-approved public macOS distribution is not claimed. This release is marked prerelease. Do not disable macOS security protections.`n"}
[IO.File]::WriteAllText($notes,$releaseText,[Text.UTF8Encoding]::new($false))
& gh release view $Tag --repo $Repository *> $null
$exists=$LASTEXITCODE -eq 0
if($exists){
    & gh release edit $Tag --repo $Repository --title "HPCPeek $Tag" --notes-file $notes --prerelease=$($unsigned.ToString().ToLowerInvariant())
    if($LASTEXITCODE -ne 0){throw 'Cannot update the release notes.'}
    & gh release upload $Tag @($zips.FullName) $checksums --repo $Repository --clobber
}else{
    $arguments=@('release','create',$Tag)+@($zips.FullName)+@($checksums,'--repo',$Repository,'--verify-tag','--title',"HPCPeek $Tag",'--notes-file',$notes)
    if($unsigned){$arguments += '--prerelease'}
    & gh @arguments
}
if($LASTEXITCODE -ne 0){throw 'GitHub Release asset upload failed.'}
$remote=& gh release view $Tag --repo $Repository --json assets
if($LASTEXITCODE -ne 0){throw 'Cannot verify uploaded Release assets.'}
$assets=($remote | ConvertFrom-Json).assets
foreach($file in @($zips)+@(Get-Item -LiteralPath $checksums)){
    $found=@($assets | Where-Object name -EQ $file.Name)
    if($found.Count -ne 1 -or $found[0].size -ne $file.Length){throw "Release asset missing or wrong size after upload: $($file.Name)"}
}
Write-Host "Verified three ZIPs and SHA256SUMS.txt in $Repository Release $Tag."
