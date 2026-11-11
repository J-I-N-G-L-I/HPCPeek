[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ZipPath,
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputDirectory = '', [ValidateRange(10,180)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$zipFullPath=(Resolve-Path -LiteralPath $ZipPath).Path
$nativeRid=if($IsWindows){'win-x64'}elseif($IsMacOS){'osx-'+[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()}else{''}
if($RuntimeIdentifier -ne $nativeRid){throw "Native startup cannot be tested here: package=$RuntimeIdentifier host=$nativeRid. Do not report this as passed."}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $repoRoot ('artifacts/smoke-'+[Guid]::NewGuid().ToString('N'))}
$outputPath=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $outputPath){throw "Smoke output must be new: $outputPath"}
New-Item -ItemType Directory -Path $outputPath | Out-Null
$extractPath=Join-Path $outputPath 'extracted'
if($IsMacOS){
    & ditto -x -k $zipFullPath $extractPath
    if($LASTEXITCODE -ne 0){throw 'macOS ZIP extraction failed.'}
    $binaryRoot=Join-Path $extractPath 'HPCPeek.app/Contents/MacOS'
    $exePath=Join-Path $binaryRoot 'HPCPeek'
    & test -x $exePath
    if($LASTEXITCODE -ne 0){throw 'Packaged macOS executable lost its executable permission.'}
    $runtimeLibrary='libcoreclr.dylib'
}else{
    [IO.Compression.ZipFile]::ExtractToDirectory($zipFullPath,$extractPath)
    $binaryRoot=$extractPath; $exePath=Join-Path $binaryRoot 'HPCPeek.exe'; $runtimeLibrary='coreclr.dll'
}
foreach($file in @($exePath,(Join-Path $binaryRoot 'HPCPeek.runtimeconfig.json'),(Join-Path $binaryRoot $runtimeLibrary))){
    if(-not(Test-Path -LiteralPath $file)){throw "Portable dependency missing: $file"}
}
$runtimeConfig=Get-Content -LiteralPath (Join-Path $binaryRoot 'HPCPeek.runtimeconfig.json') -Raw | ConvertFrom-Json
if(-not $runtimeConfig.runtimeOptions.includedFrameworks){throw 'Publish is not self-contained.'}
$evidencePath=Join-Path $outputPath 'evidence'; $emptyRuntime=Join-Path $outputPath 'empty-dotnet-root'
New-Item -ItemType Directory -Path $evidencePath,$emptyRuntime | Out-Null
$startInfo=[Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName=$exePath; $startInfo.WorkingDirectory=$binaryRoot; $startInfo.UseShellExecute=$false
$startInfo.CreateNoWindow=$true; $startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.ArgumentList.Add('--smoke-test'); $startInfo.ArgumentList.Add($evidencePath)
$startInfo.Environment['DOTNET_ROOT']=$emptyRuntime
$startInfo.Environment['DOTNET_ROOT_X64']=$emptyRuntime
$startInfo.Environment['DOTNET_ROOT_ARM64']=$emptyRuntime
$startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
$startInfo.Environment['PATH']=if($IsWindows){"$env:SystemRoot\System32;$env:SystemRoot"}else{'/usr/bin:/bin:/usr/sbin:/sbin'}
$process=[Diagnostics.Process]::Start($startInfo)
try{
    if(-not $process.WaitForExit($TimeoutSeconds*1000)){$process.Kill($true);throw "Extracted app exceeded $TimeoutSeconds seconds."}
    if($process.ExitCode -ne 0){throw "Extracted app failed ($($process.ExitCode)). See $evidencePath"}
}finally{$process.Dispose()}
$resultPath=Join-Path $evidencePath 'smoke-result.json'
if(-not(Test-Path -LiteralPath $resultPath)){throw 'App did not write smoke-result.json.'}
$result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if($result.success -ne $true){throw "Application checks failed. See $resultPath"}
$images=@('compact-125.png','compact-150.png','compact-200.png','expanded-125.png','expanded-150.png','expanded-200.png','compact-dark-150.png','expanded-dark-150.png','menu-light.png','menu-dark.png')
$images += @('compact-100.png','expanded-100.png','disconnected-150.png','empty-150.png','partial-150.png')
$images += @('job-elapsed-light-150.png','job-elapsed-dark-150.png')
$images += @('nodes-before-125.png','nodes-before-150.png','nodes-before-200.png','nodes-selected-125.png','nodes-selected-150.png','nodes-selected-200.png','selected-node-150.png')
$images += @('expanded-calder-light-150.png','expanded-calder-dark-150.png')
foreach($theme in @('light','dark')){
    foreach($dialog in @('connection-direct','connection-gateway','connection-calder','connection-remember','authentication-password','authentication-duo','host-fingerprint','settings-0','settings-1','settings-2','settings-calder','settings-refresh-custom','notification-completed','notification-failed','notification-timeout','message-signin','message-long')){
        $images += "$dialog-$theme.png"
    }
}
foreach($image in $images){
    $path=Join-Path $evidencePath $image
    if(-not(Test-Path -LiteralPath $path)){throw "Missing layout evidence: $path"}
    if((Get-Item -LiteralPath $path).Length -lt 1024){throw "Empty layout evidence: $path"}
}
[pscustomobject]@{Success=$true;RuntimeIdentifier=$RuntimeIdentifier;Zip=$zipFullPath;Extracted=$extractPath;Executable=$exePath;Evidence=$evidencePath}
