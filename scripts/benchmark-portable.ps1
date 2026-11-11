[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [ValidateSet('win-x64','osx-arm64','osx-x64')][string]$RuntimeIdentifier='win-x64',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,10)][int]$Runs=3,
    [ValidateRange(15,300)][int]$IdleSeconds=30
)
$ErrorActionPreference='Stop'
$nativeRid=if($IsWindows){'win-x64'}elseif($IsMacOS){'osx-'+[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()}else{''}
if($RuntimeIdentifier -ne $nativeRid){throw "Cannot benchmark $RuntimeIdentifier on $nativeRid."}
# Query resident pages directly: PrivateMemorySize64 is committed private memory, not
# Task Manager's private working set. No working-set trimming or forced collection is used.
# https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-psapi_working_set_block
if($IsWindows -and -not ('HpcPeekBenchmark.PrivateWorkingSetReader' -as [type])){
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HpcPeekBenchmark
{
    public sealed class PrivateWorkingSetReader : IDisposable
    {
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryWorkingSet(IntPtr process, IntPtr buffer, uint size);
        private IntPtr buffer;
        private int bufferSize;

        public long ReadBytes(IntPtr process)
        {
            if (buffer == IntPtr.Zero) Resize(65536);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (QueryWorkingSet(process, buffer, (uint)bufferSize))
                {
                    long entries = Marshal.ReadIntPtr(buffer).ToInt64();
                    if (entries < 0 || entries > bufferSize / IntPtr.Size - 1)
                        throw new InvalidOperationException("Invalid working-set snapshot length.");
                    long privatePages = 0;
                    for (int i = 0; i < entries; i++)
                    {
                        ulong flags = unchecked((ulong)Marshal.ReadIntPtr(buffer, (i + 1) * IntPtr.Size).ToInt64());
                        // PSAPI_WORKING_SET_BLOCK.Shared is bit 8. Clear means not shareable.
                        if ((flags & 0x100UL) == 0) privatePages++;
                    }
                    return checked(privatePages * Environment.SystemPageSize);
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 24) throw new Win32Exception(error, "Cannot read private working set.");
                // ERROR_BAD_LENGTH returns the required entry count. Leave headroom for growth.
                long required = Marshal.ReadIntPtr(buffer).ToInt64();
                Resize(Math.Max(required + 4096, bufferSize / IntPtr.Size * 2L));
            }
            throw new InvalidOperationException("Working set kept growing while sampling.");
        }

        private void Resize(long entries)
        {
            int bytes = checked((int)((entries + 1) * IntPtr.Size));
            if (bytes > 256 * 1024 * 1024) throw new InvalidOperationException("Working-set query buffer is too large.");
            IntPtr replacement = Marshal.AllocHGlobal(bytes);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = replacement;
            bufferSize = bytes;
        }

        public void Dispose()
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            bufferSize = 0;
        }
    }
}
'@
}
$exe=(Resolve-Path -LiteralPath $ExecutablePath).Path
$runtimeConfig=[IO.Path]::ChangeExtension($exe, '.runtimeconfig.json')
$runtimeConfigHash=if(Test-Path -LiteralPath $runtimeConfig){(Get-FileHash -LiteralPath $runtimeConfig -Algorithm SHA256).Hash}else{$null}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$measurements=@()
for($run=1;$run -le $Runs;$run++){
    $runPath=Join-Path $output "run-$run"
    if(Test-Path -LiteralPath $runPath){throw "Benchmark run directory must be new: $runPath"}
    New-Item -ItemType Directory -Path $runPath | Out-Null
    $ready=Join-Path $runPath 'benchmark-ready.json'
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$exe; $startInfo.WorkingDirectory=[IO.Path]::GetDirectoryName($exe); $startInfo.UseShellExecute=$false
    $startInfo.CreateNoWindow=$true; $startInfo.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.ArgumentList.Add('--benchmark'); $startInfo.ArgumentList.Add($runPath)
    $startInfo.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $process=[Diagnostics.Process]::Start($startInfo)
    $privateWorkingSetReader=if($IsWindows){[HpcPeekBenchmark.PrivateWorkingSetReader]::new()}else{$null}
    try{
        while(-not(Test-Path -LiteralPath $ready)){
            if($process.HasExited){throw 'Benchmark application exited before first render.'}
            if($watch.Elapsed.TotalSeconds -gt 60){throw 'Benchmark first-render signal timed out.'}
            Start-Sleep -Milliseconds 10
        }
        $startup=$watch.Elapsed.TotalMilliseconds
        $readyData=Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
        $samples=@();$cpuBefore=$process.TotalProcessorTime.TotalMilliseconds
        for($second=1;$second -le $IdleSeconds;$second++){
            Start-Sleep -Seconds 1
            $process.Refresh()
            if($process.HasExited){throw 'Application exited during idle measurement.'}
            $rss=[long]$process.WorkingSet64
            if($IsMacOS){
                $psRss=& /bin/ps -o rss= -p $process.Id
                if($LASTEXITCODE -ne 0){throw 'Cannot read macOS RSS.'}
                $rss=[long]$psRss.Trim()*1024
            }
            $samples += [ordered]@{second=$second;workingSetBytes=$rss;privateBytes=if($IsWindows){$process.PrivateMemorySize64}else{$null};privateWorkingSetBytes=if($IsWindows){$privateWorkingSetReader.ReadBytes($process.Handle)}else{$null};cpuMs=$process.TotalProcessorTime.TotalMilliseconds}
        }
        $stable=@($samples | Select-Object -Last ([Math]::Min(10,$IdleSeconds)))
        $measurements += [ordered]@{
            run=$run;startupMs=[Math]::Round($startup,2);ready=$readyData;idleSeconds=$IdleSeconds;
            workingSetBytes=[long](($stable | ForEach-Object {$_.workingSetBytes} | Measure-Object -Average).Average);
            privateBytes=if($IsWindows){[long](($stable | ForEach-Object {$_.privateBytes} | Measure-Object -Average).Average)}else{$null};
            privateWorkingSetBytes=if($IsWindows){[long](($stable | ForEach-Object {$_.privateWorkingSetBytes} | Measure-Object -Average).Average)}else{$null};
            cpuMsDuringIdle=[Math]::Round($process.TotalProcessorTime.TotalMilliseconds-$cpuBefore,2);samples=$samples
        }
    }finally{
        if($null -ne $privateWorkingSetReader){$privateWorkingSetReader.Dispose()}
        # Only terminate the child this script created. This does not affect other HPCPeek instances.
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit(5000)|Out-Null}
        $process.Dispose()
    }
}
$startupValues=@($measurements | ForEach-Object {$_.startupMs} | Sort-Object)
$memoryValues=@($measurements | ForEach-Object {$_.workingSetBytes} | Sort-Object)
$privateValues=@($measurements | Where-Object {$null -ne $_.privateBytes} | ForEach-Object {$_.privateBytes} | Sort-Object)
$privateWorkingSetValues=@($measurements | Where-Object {$null -ne $_.privateWorkingSetBytes} | ForEach-Object {$_.privateWorkingSetBytes} | Sort-Object)
function Median($values){$middle=[int][Math]::Floor($values.Count/2);if($values.Count % 2){$values[$middle]}else{($values[$middle-1]+$values[$middle])/2}}
$medianStartup=Median $startupValues; $medianMemory=Median $memoryValues
$medianPrivate=if($privateValues.Count){Median $privateValues}else{$null}
$medianPrivateWorkingSet=if($privateWorkingSetValues.Count){Median $privateWorkingSetValues}else{$null}
$cpuName=if($IsWindows){(Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)}else{(& /usr/sbin/sysctl -n machdep.cpu.brand_string 2>$null)}
$physicalMemory=if($IsWindows){[long](Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory}else{[long](& /usr/sbin/sysctl -n hw.memsize)}
[ordered]@{
    runtimeIdentifier=$RuntimeIdentifier;executable=$exe;os=[Runtime.InteropServices.RuntimeInformation]::OSDescription;
    executableSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash;runtimeConfigSha256=$runtimeConfigHash;
    applicationAssemblySha256=(Get-FileHash -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($exe)) 'HPCPeek.dll') -Algorithm SHA256).Hash;
    inheritedGcConserveMemory=if($env:DOTNET_GCConserveMemory){$env:DOTNET_GCConserveMemory}else{$null};
    architecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString();cpu=$cpuName;physicalMemoryBytes=$physicalMemory;
    mode='offline demo, visible compact window';runs=$Runs;idleSeconds=$IdleSeconds;
    firstProcessLaunchMs=$measurements[0].startupMs;medianProcessLaunchMs=$medianStartup;
    medianIdleWorkingSetBytes=$medianMemory;medianIdleWorkingSetMiB=[Math]::Round($medianMemory/1MB,2);
    medianIdlePrivateBytes=$medianPrivate;medianIdlePrivateMiB=if($null -ne $medianPrivate){[Math]::Round($medianPrivate/1MB,2)}else{$null};
    medianIdlePrivateWorkingSetBytes=$medianPrivateWorkingSet;medianIdlePrivateWorkingSetMiB=if($null -ne $medianPrivateWorkingSet){[Math]::Round($medianPrivateWorkingSet/1MB,2)}else{$null};
    startupMethod='External process-start stopwatch to app signal after first demo render, polling every 10 ms. Fresh process and config for each run. OS filesystem cache is not purged; not a reboot-cold disk measurement.';
    memoryMethod="Average of final 10 one-second samples after $IdleSeconds seconds idle per process; Windows total working set, private committed bytes and private working set; macOS RSS via ps. Median of run averages. Private working set counts resident non-shareable pages using PSAPI QueryWorkingSet; closest to Task Manager Memory for an active process, but sampling time and Task Manager's active-process accounting may differ. No forced GC or working-set trimming.";
    measurements=$measurements
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'benchmark-result.json') -Encoding utf8NoBOM
Write-Host "Benchmark evidence: $(Join-Path $output 'benchmark-result.json')"
