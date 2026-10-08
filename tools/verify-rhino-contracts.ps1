param(
    [ValidateSet('net48', 'net7.0', 'net7.0-windows')][string]$Framework = 'net48',
    [string]$RhinoPath = 'C:\Program Files\Rhino 8\System\Rhino.exe',
    [string]$Baseline = '',
    [switch]$Capture,
    [switch]$InjectFailure,
    [ValidateSet('contracts', 'pareto-selection', 'interlayer-time', 'settings')][string]$Probe = 'contracts',
    [string[]]$AllowedAdditions = @(),
    [string[]]$AllowedRemovals = @(),
    [string[]]$AllowedChanges = @(),
    [ValidateRange(10, 600)][int]$TimeoutSeconds = 120
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$Baseline) { $Baseline = Join-Path $repoRoot 'tests\grasshopper\component-contracts-v3.json' }
$Baseline = [IO.Path]::GetFullPath($Baseline)
if ($Capture -and (Test-Path -LiteralPath $Baseline)) { throw 'Capture refuses to overwrite a baseline.' }
if ($Capture -and $Probe -ne 'contracts') { throw 'Capture is only for the unchanged-source contract baseline.' }
if (!$Capture -and !(Test-Path -LiteralPath $Baseline)) { throw "Missing baseline: $Baseline" }
$pluginPath = Join-Path $repoRoot "Lugworm\bin\Release\$Framework\Lugworm.gha"
if (!(Test-Path -LiteralPath $pluginPath)) { throw "Build Release first: $pluginPath" }
if (!(Test-Path -LiteralPath $RhinoPath)) { throw "Rhino not found: $RhinoPath" }
$runId = [Guid]::NewGuid().ToString('N')
$resultDir = Join-Path $repoRoot "tests\grasshopper\bin\contracts-$Framework-$runId"
New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
$resultPath = Join-Path $resultDir 'result.json'
$scriptPath = Join-Path $PSScriptRoot 'verify-component-contracts.py'
$mode = if ($Framework -eq 'net48') { '/netfx' } else { '/netcore' }
$tfm = if ($Framework -eq 'net48') { '.NETFramework,Version=v4.8' } elseif ($Framework -eq 'net7.0') { '.NETCoreApp,Version=v7.0' } else { '.NETCoreApp,Version=v7.0' }
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $RhinoPath
# Rhino's macro parser uses parentheses around paths inside /runscript; nested
# escaped Windows quotes can prevent the Python command from executing.
$start.Arguments = "$mode /nosplash /notemplate /scheme=LugwormContractProbe " + '/runscript="_-RunPythonScript (' + $scriptPath + ') _-Exit"'
$start.WorkingDirectory = $repoRoot
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
# Only named task variables are read/written. Some managed environments disallow
# enumerating the entire parent environment via ProcessStartInfo.EnvironmentVariables.
$probeVariables = @{
    IAM_CONTRACT_ASSEMBLY = $pluginPath
    IAM_CONTRACT_BASELINE = $Baseline
    IAM_CONTRACT_RESULT = $resultPath
    IAM_CONTRACT_RUN_ID = $runId
    IAM_CONTRACT_TFM = $tfm
    IAM_CONTRACT_CAPTURE = [string][int]$Capture.IsPresent
    IAM_CONTRACT_INJECT_FAILURE = [string][int]$InjectFailure.IsPresent
    IAM_CONTRACT_ADDITIONS = $AllowedAdditions -join ','
    IAM_CONTRACT_REMOVALS = $AllowedRemovals -join ','
    IAM_CONTRACT_CHANGES = $AllowedChanges -join ';'
    IAM_CONTRACT_PROBE = $Probe
}
$oldVariables = @{}
foreach ($key in $probeVariables.Keys) {
    $oldVariables[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $probeVariables[$key], 'Process')
}
try {
$process = [Diagnostics.Process]::Start($start)
}
finally {
    foreach ($key in $probeVariables.Keys) { [Environment]::SetEnvironmentVariable($key, $oldVariables[$key], 'Process') }
}
Write-Output "Rhino contract probe PID $($process.Id), $Framework, run $runId"
try {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $resultPath)) {
        if ($process.HasExited) { throw "Rhino exited without assertion evidence. Result expected at $resultPath" }
        if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            if (Test-Path -LiteralPath "$resultPath.progress.json") { Get-Content -LiteralPath "$resultPath.progress.json" }
            else { Write-Output 'No script-started marker: startup/macro execution is unverified.' }
            throw "Rhino probe timed out without assertion evidence. Result expected at $resultPath"
        }
        Start-Sleep -Milliseconds 250
    }
    # The writer closes a tiny report before process shutdown. A brief bounded retry
    # handles the interval between file creation and complete JSON publication.
    $result = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$result -or $result.run_id -ne $runId) { throw "Missing or stale run identity: $resultPath" }
    Write-Output ($result | ConvertTo-Json -Depth 8)
    if ($result.passed -ne $true) { throw "Rhino assertions FAILED: $resultPath" }
    if (!$process.WaitForExit(10000)) { throw 'Assertions passed but Rhino did not shut down cleanly.' }
    Write-Output "PASS: $resultPath"
}
finally {
    # Only the process created by this invocation is ours. Never kill other Rhino
    # sessions; this probe has no user document and never solves or sends commands.
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
}
