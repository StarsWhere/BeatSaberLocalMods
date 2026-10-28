#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BuildDirectory = '',
    [Parameter(Mandatory=$true)][string]$WishPath,
    [string]$LevelId = 'custom_level_AAE2BA21DAA4AD51401417199FDCFD0DFD42C798',
    [string]$ResubmitJobId = '',
    [ValidateRange(120,1800)][int]$FiniteWaitSeconds = 1320,
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe',
    [switch]$Generate,
    [switch]$UseInstalledPlugin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $GameRoot '.tmp/beatsage-mod/runtime-build'
} else {
    $BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
}
$runDirectory = Join-Path $GameRoot ('.tmp/beatsage-mod/runtime/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
$runtimeDirectory = Join-Path $runDirectory 'evidence'
$smokeAssembly = Join-Path $runDirectory 'BeatSageRuntimeSmoke.dll'
$smokePlugin = Join-Path $GameRoot 'Plugins/BeatSageRuntimeSmoke.dll'
$beatSagePlugin = Join-Path $GameRoot 'Plugins/BeatSage.dll'
$builtBeatSage = Join-Path $BuildDirectory 'bin/BeatSage.dll'
$responseFile = Join-Path $runDirectory 'smoke.rsp'
$report = Join-Path $runtimeDirectory 'report.json'
$evidence = Join-Path $runtimeDirectory 'evidence.json'
$process = $null
$ownsSmokePlugin = $false
$hadBeatSagePlugin = Test-Path -LiteralPath $beatSagePlugin -PathType Leaf
$beatSageBackup = Join-Path $runDirectory 'BeatSage.dll.before'
$gameExitCode = $null

if (@(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Beat Saber is already running.'
}
if (!(Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw "Missing dotnet executable: $DotnetPath"
}
if (Test-Path -LiteralPath $smokePlugin) {
    throw "A runtime check plugin already exists: $smokePlugin"
}
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
[IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null

try {
    if ($UseInstalledPlugin) {
        if (!$hadBeatSagePlugin) { throw 'BeatSage.dll is not installed.' }
        $builtBeatSage = $beatSagePlugin
    } else {
    $buildScript = Join-Path $PSScriptRoot 'build_local_beat_sage.ps1'
    if (!(Test-Path -LiteralPath $buildScript -PathType Leaf)) {
        throw "Missing Beat Sage build script: $buildScript"
    }
    & $buildScript -GameRoot $GameRoot -BuildDirectory $BuildDirectory -DotnetPath $DotnetPath
    if (!(Test-Path -LiteralPath $builtBeatSage -PathType Leaf)) {
        throw "Beat Sage build did not produce $builtBeatSage"
    }

    if ($hadBeatSagePlugin) {
        Copy-Item -LiteralPath $beatSagePlugin -Destination $beatSageBackup
    }
    Copy-Item -LiteralPath $builtBeatSage -Destination $beatSagePlugin
    }

    $source = Join-Path $PSScriptRoot 'beat_sage/RuntimeChecks'
    $manifest = Join-Path $source 'RuntimeSmoke.manifest.json'
    $sourceFile = Join-Path $source 'RuntimeSmoke.cs'
    foreach ($required in @($manifest, $sourceFile)) {
        if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Missing runtime check source: $required"
        }
    }

    $arguments = @(
        '/nologo', '/target:library', '/langversion:latest', '/nostdlib+',
        ('/out:"' + $smokeAssembly + '"'),
        ('/resource:"' + $manifest + '",BeatSageRuntimeSmoke.manifest.json'),
        ('"' + $sourceFile + '"')
    )
    $arguments += @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -File -Filter '*.dll' |
        ForEach-Object { '/r:"' + $_.FullName + '"' })
    $arguments += @(
        (Join-Path $GameRoot 'Libs/0Harmony.dll'),
        (Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'),
        (Join-Path $GameRoot 'Plugins/SongCore.dll'),
        (Join-Path $GameRoot 'Plugins/BSML.dll'),
        $builtBeatSage
    ) | ForEach-Object { '/r:"' + $_ + '"' }
    $arguments | Set-Content -LiteralPath $responseFile -Encoding utf8

    $compiler = Get-ChildItem -LiteralPath (Join-Path (Split-Path $DotnetPath -Parent) 'sdk') -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1 |
        ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
    if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Missing Roslyn compiler: $compiler"
    }
    & $DotnetPath $compiler ('@' + $responseFile)
    if ($LASTEXITCODE -ne 0) {
        throw 'Beat Sage runtime helper compilation failed.'
    }
    if (!(Test-Path -LiteralPath $smokeAssembly -PathType Leaf)) {
        throw "Runtime helper was not produced: $smokeAssembly"
    }

    Copy-Item -LiteralPath $smokeAssembly -Destination $smokePlugin
    $ownsSmokePlugin = $true

    $before = @{
        'BEATSAGE_SMOKE_DIR' = [Environment]::GetEnvironmentVariable('BEATSAGE_SMOKE_DIR', 'Process')
        'BEATSAGE_SMOKE_GENERATE' = [Environment]::GetEnvironmentVariable('BEATSAGE_SMOKE_GENERATE', 'Process')
        'BEATSAGE_SMOKE_WISH_PATH' = [Environment]::GetEnvironmentVariable('BEATSAGE_SMOKE_WISH_PATH', 'Process')
        'BEATSAGE_SMOKE_LEVEL_ID' = [Environment]::GetEnvironmentVariable('BEATSAGE_SMOKE_LEVEL_ID', 'Process')
        'BEATSAGE_SMOKE_RESUBMIT_ID' = [Environment]::GetEnvironmentVariable('BEATSAGE_SMOKE_RESUBMIT_ID', 'Process')
    }
    try {
        [Environment]::SetEnvironmentVariable('BEATSAGE_SMOKE_DIR', $runtimeDirectory, 'Process')
        [Environment]::SetEnvironmentVariable('BEATSAGE_SMOKE_GENERATE', $(if ($Generate) { '1' } else { '0' }), 'Process')
        [Environment]::SetEnvironmentVariable('BEATSAGE_SMOKE_WISH_PATH', $WishPath, 'Process')
        [Environment]::SetEnvironmentVariable('BEATSAGE_SMOKE_LEVEL_ID', $LevelId, 'Process')
        [Environment]::SetEnvironmentVariable('BEATSAGE_SMOKE_RESUBMIT_ID', $ResubmitJobId, 'Process')

        $launchArguments = @('--fpfc', 'fpfc', '--beat-sage-runtime-smoke')
        $logsDirectory = Join-Path $GameRoot 'Logs'
        $logsBefore = @(Get-ChildItem -LiteralPath $logsDirectory -File -Filter '*.log' | Select-Object -ExpandProperty Name)
        $process = Start-Process -FilePath (Join-Path $GameRoot 'Beat Saber.exe') -WorkingDirectory $GameRoot -ArgumentList $launchArguments -WindowStyle Hidden -PassThru
        Write-Output ('Beat Sage runtime process ' + $process.Id + '; evidence ' + $runtimeDirectory)
        $deadline = [DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
        while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Seconds 1
            $process.Refresh()
        }
        if (!$process.HasExited) {
            throw "Beat Sage runtime check exceeded finite wait of $FiniteWaitSeconds seconds."
        }
        $gameExitCode = $process.ExitCode
        if (!(Test-Path -LiteralPath $report -PathType Leaf)) {
            throw 'Beat Saber exited without Beat Sage runtime report.json.'
        }
        $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $sessionLogs = @(Get-ChildItem -LiteralPath $logsDirectory -File -Filter '*.log' |
            Where-Object { $_.Name -ne '_latest.log' -and $_.Name -notin $logsBefore })
        if ($sessionLogs.Count -ne 1) {
            throw "Expected one new game log, found $($sessionLogs.Count)."
        }
        $sessionLog = $sessionLogs[0].FullName
        $environmentLookupWarnings = @(Select-String -LiteralPath $sessionLog -Pattern 'AccessTools\.(Property|Field): Could not find .* for type EnvironmentInfoSO\b').Count
        $uiGeometryPath = Join-Path $runtimeDirectory 'ui-geometry.json'
        $uiGeometry = $null
        if (Test-Path -LiteralPath $uiGeometryPath -PathType Leaf) {
            $uiGeometry = Get-Content -LiteralPath $uiGeometryPath -Raw | ConvertFrom-Json
        }
        $evidenceObject = [ordered]@{
            runner = 'test_beat_sage_runtime.ps1'
            generated = [bool]$Generate
            gameExitCode = $gameExitCode
            finiteWaitSeconds = $FiniteWaitSeconds
            buildPlugin = $builtBeatSage
            wishPath = $WishPath
            levelId = $LevelId
            resubmitJobId = $ResubmitJobId
            sessionLog = $sessionLog
            environmentLookupWarnings = $environmentLookupWarnings
            reportPath = $report
            uiGeometryPath = $uiGeometryPath
            screenshotPath = (Join-Path $runtimeDirectory 'menu-bsml.png')
            uiGeometry = $uiGeometry
            report = $result
        }
        $evidenceObject | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $evidence -Encoding utf8NoBOM
        if (!$result.passed -or $gameExitCode -ne 0 -or $environmentLookupWarnings -ne 0) {
            $details = @($result.errors) -join "`n"
            throw ('Beat Sage runtime check failed: ' + $details + "; environment lookup warnings=" + $environmentLookupWarnings)
        }
        Write-Output ('Passed ' + @($result.checks).Count + ' checks in ' + $result.elapsed + ' seconds.')
        Write-Output $evidence
    } finally {
        foreach ($name in $before.Keys) {
            [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process')
        }
    }
} finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (!$process.HasExited) {
            Stop-Process -Id $process.Id
        }
        $process.Dispose()
    }
    if ($ownsSmokePlugin -and (Test-Path -LiteralPath $smokePlugin)) {
        Remove-Item -LiteralPath $smokePlugin
    }
    if (Test-Path -LiteralPath $beatSageBackup -PathType Leaf) {
        Copy-Item -LiteralPath $beatSageBackup -Destination $beatSagePlugin
        Remove-Item -LiteralPath $beatSageBackup
    } elseif (!$hadBeatSagePlugin -and (Test-Path -LiteralPath $beatSagePlugin)) {
        Remove-Item -LiteralPath $beatSagePlugin
    }
}
