param(
    [string]$DotnetPath = 'dotnet',
    [string]$PythonPath = 'python',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/ssh-protocol'),
    [string]$PublishedDirectory = ''
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$runId = [Guid]::NewGuid().ToString('N')
$state = Join-Path $output "$runId-state.json"
$stop = Join-Path $output "$runId-stop"
$result = Join-Path $output 'result.json'
$fixtureScript = Join-Path $PSScriptRoot 'ssh_fixture.py'
$arguments = @('"' + $fixtureScript + '"', '--state', '"' + $state + '"', '--stop', '"' + $stop + '"')
$fixture = Start-Process -FilePath $PythonPath -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $output 'fixture-stderr.txt') -RedirectStandardOutput (Join-Path $output 'fixture-stdout.txt')
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $state)) {
        if ($fixture.HasExited) { throw 'SSH fixture failed; install dev-only dependency: python -m pip install paramiko==4.0.0' }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'SSH fixture startup timed out.' }
        Start-Sleep -Milliseconds 100
    }
    $project = Join-Path $PSScriptRoot 'AirePeek.Ssh.ProtocolSmoke.csproj'
    if ($PublishedDirectory) {
        if (!$IsWindows) { throw 'Published-runtime protocol verification currently requires Windows.' }
        & $DotnetPath build $project -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Cannot build the isolated protocol entry point.' }
        $source = (Resolve-Path -LiteralPath $PublishedDirectory).Path
        $hostDirectory = Join-Path $output 'published-host'
        if (Test-Path -LiteralPath $hostDirectory) { throw 'Published protocol host directory must be new.' }
        New-Item -ItemType Directory -Path $hostDirectory | Out-Null
        Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $hostDirectory -Recurse
        # Replace only the desktop entry point in an isolated COPY. All published SSH,
        # crypto, runtime and native libraries, deps and runtimeconfig remain exact.
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net10.0/AirePeek.Ssh.ProtocolSmoke.dll') -Destination (Join-Path $hostDirectory 'HPCPeek.dll') -Force
        $verified = @()
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
            if ($relative -eq 'HPCPeek.dll') { continue }
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $hostDirectory $relative)).Hash) { throw "Published protocol library changed: $relative" }
            $verified += $relative
        }
        [ordered]@{source=$source;entryPoint='Isolated test copy: HPCPeek.dll replaced by protocol harness';unchangedFiles=$verified} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'published-runtime-verification.json') -Encoding utf8NoBOM
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = Join-Path $hostDirectory 'HPCPeek.exe'; $start.WorkingDirectory = $hostDirectory
        $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardError = $true; $start.RedirectStandardOutput = $true
        $start.Environment['DOTNET_ROOT'] = Join-Path $output 'no-external-runtime'; $start.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $start.ArgumentList.Add($state); $start.ArgumentList.Add($result)
        $hostProcess = [Diagnostics.Process]::Start($start)
        $stderr = $hostProcess.StandardError.ReadToEndAsync(); $stdout = $hostProcess.StandardOutput.ReadToEndAsync()
        try {
            if (!$hostProcess.WaitForExit(60000)) { $hostProcess.Kill($true); throw 'Published protocol host timed out.' }
            [IO.File]::WriteAllText((Join-Path $output 'published-host-stderr.txt'), $stderr.GetAwaiter().GetResult())
            [IO.File]::WriteAllText((Join-Path $output 'published-host-stdout.txt'), $stdout.GetAwaiter().GetResult())
            if ($hostProcess.ExitCode -ne 0) { throw "Published protocol host exited $($hostProcess.ExitCode)." }
        } finally { $hostProcess.Dispose() }
    } else {
        & $DotnetPath run --project $project -c Release -- $state $result
        if ($LASTEXITCODE -ne 0) { throw "SSH protocol smoke exited $LASTEXITCODE" }
    }
    $counters = (Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).counters
    if ($counters.gateway.authStarts -ne 6 -or $counters.target.authStarts -ne 4 -or $counters.password.authStarts -ne 1 -or $counters.password_then_duo.authStarts -ne 4) { throw 'Unexpected extra protocol authentication' }
    if ($counters.gateway.authRounds -ne 18 -or $counters.target.authRounds -ne 12 -or $counters.password.authRounds -ne 1 -or $counters.password_then_duo.authRounds -ne 6) { throw 'Unexpected protocol prompt count' }
    if ($counters.target.commands -ne 7 -or $counters.password.commands -ne 1 -or $counters.gateway.forwards -ne 6 -or $counters.password_then_duo.commands -ne 2) { throw 'Unexpected command / forwarding counts' }
    Copy-Item -LiteralPath $state -Destination (Join-Path $output 'server-counters.json') -Force
    Write-Output "Protocol evidence: $result"
}
finally {
    New-Item -ItemType File -Path $stop -Force | Out-Null
    if (!$fixture.WaitForExit(5000)) { Stop-Process -Id $fixture.Id -Force }
}
