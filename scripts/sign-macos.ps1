[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference='Stop'
if(-not $IsMacOS){throw 'Developer ID signing, notarization and stapling require macOS.'}
$required=@('MACOS_CERTIFICATE_BASE64','MACOS_CERTIFICATE_PASSWORD','MACOS_SIGNING_IDENTITY','APPLE_ID','APPLE_TEAM_ID','APPLE_APP_SPECIFIC_PASSWORD')
foreach($name in $required){if(-not [Environment]::GetEnvironmentVariable($name)){throw "Signing credential is missing: $name"}}
$app=(Resolve-Path -LiteralPath $AppPath).Path
if(-not $app.EndsWith('.app')){throw 'Expected a .app bundle.'}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('airepeek-sign-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$certificate=Join-Path $temporary 'certificate.p12'
$keychain=Join-Path $temporary 'signing.keychain-db'
$keychainPassword=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$entitlements=Join-Path $repoRoot 'packaging/macos/entitlements.plist'
$created=$false
function Assert-NativeSuccess([string]$Action){if($LASTEXITCODE -ne 0){throw "$Action failed ($LASTEXITCODE)."}}
try{
    [IO.File]::WriteAllBytes($certificate,[Convert]::FromBase64String($env:MACOS_CERTIFICATE_BASE64))
    & security create-keychain -p $keychainPassword $keychain | Out-Null
    Assert-NativeSuccess 'Temporary signing keychain creation'; $created=$true
    & security set-keychain-settings -lut 21600 $keychain | Out-Null
    Assert-NativeSuccess 'Signing keychain timeout configuration'
    & security unlock-keychain -p $keychainPassword $keychain | Out-Null
    Assert-NativeSuccess 'Signing keychain unlock'
    & security import $certificate -k $keychain -P $env:MACOS_CERTIFICATE_PASSWORD -T /usr/bin/codesign -T /usr/bin/security | Out-Null
    Assert-NativeSuccess 'Developer ID certificate import'
    & security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k $keychainPassword $keychain | Out-Null
    Assert-NativeSuccess 'Signing key access'
    # Sign each native Mach-O payload first. Managed DLLs are PE, not Mach-O.
    # Do not use --deep to sign: every nested code object must receive explicit options.
    foreach($file in Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object { $_.FullName.Length } -Descending){
        $stream=[IO.File]::OpenRead($file.FullName)
        try{
            if($stream.Length -lt 4){continue}
            $header=[byte[]]::new(4); $stream.ReadExactly($header)
            $magic=[Convert]::ToHexString($header)
        }finally{$stream.Dispose()}
        if($magic -notin @('CFFAEDFE','FEEDFACF','CEFAEDFE','FEEDFACE','CAFEBABE','BEBAFECA','CAFEBABF','BFBAFECA')){continue}
        & codesign --force --sign $env:MACOS_SIGNING_IDENTITY --keychain $keychain --options runtime --timestamp --entitlements $entitlements $file.FullName
        Assert-NativeSuccess 'Nested Mach-O signing'
    }
    & codesign --force --sign $env:MACOS_SIGNING_IDENTITY --keychain $keychain --options runtime --timestamp --entitlements $entitlements $app
    Assert-NativeSuccess 'App bundle signing'
    & codesign --verify --deep --strict --verbose=2 $app
    Assert-NativeSuccess 'App signature verification'
    $submission=Join-Path $temporary 'notarization.zip'
    & ditto -c -k --keepParent $app $submission
    Assert-NativeSuccess 'Notarization archive creation'
    # Credentials are read from CI secrets and stored only in the temporary keychain.
    & xcrun notarytool store-credentials AIREPeekNotary --keychain $keychain --apple-id $env:APPLE_ID --team-id $env:APPLE_TEAM_ID --password $env:APPLE_APP_SPECIFIC_PASSWORD | Out-Null
    Assert-NativeSuccess 'Notary credential validation'
    $response=& xcrun notarytool submit $submission --keychain-profile AIREPeekNotary --keychain $keychain --wait --output-format json
    Assert-NativeSuccess 'Apple notarization submission'
    $notary=$response | ConvertFrom-Json
    if($notary.status -ne 'Accepted'){throw "Apple notarization was not accepted: $($notary.status), submission $($notary.id)"}
    & xcrun stapler staple $app
    Assert-NativeSuccess 'Notarization ticket stapling'
    & xcrun stapler validate $app
    Assert-NativeSuccess 'Stapled ticket verification'
    & codesign --verify --deep --strict --verbose=2 $app
    Assert-NativeSuccess 'Final bundle verification'
    & spctl --assess --type execute --verbose=2 $app
    Assert-NativeSuccess 'Gatekeeper assessment'
    [ordered]@{status=$notary.status;submissionId=$notary.id;developerIdSigned=$true;hardenedRuntime=$true;stapled=$true;gatekeeperAssessment='passed'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($app)) 'signing-result.json') -Encoding utf8NoBOM
}finally{
    if($created){& security delete-keychain $keychain | Out-Null}
    # These exact paths were created above. No user keychain or credential is modified.
    if(Test-Path -LiteralPath $certificate){Remove-Item -LiteralPath $certificate -Force}
    if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Recurse -Force}
}
