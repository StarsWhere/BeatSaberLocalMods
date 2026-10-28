[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$FiniteWaitSeconds = 240,
    [string]$RuntimeCheckDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $GameRoot '.tmp/advanced-song-filters-v020/runtime-check'))
if ([string]::IsNullOrWhiteSpace($RuntimeCheckDirectory)) {
    [IO.Directory]::CreateDirectory($runtimeRoot) | Out-Null
    $runStamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff', [Globalization.CultureInfo]::InvariantCulture)
    $runtimeCheck = Join-Path $runtimeRoot ('run-' + $runStamp)
} else {
    $runtimeCheck = [IO.Path]::GetFullPath($RuntimeCheckDirectory)
    $runtimePrefix = $runtimeRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($runtimeCheck -ne $runtimeRoot -and -not $runtimeCheck.StartsWith($runtimePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "RuntimeCheckDirectory must stay under $runtimeRoot"
    }
}
$sourceDirectory = Join-Path $PSScriptRoot 'advanced_song_filters'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$helper = Join-Path $runtimeCheck 'AdvancedSongFiltersSmoke.dll'
$manifest = Join-Path $sourceDirectory 'GameSmoke.manifest.json'
$plugin = Join-Path $GameRoot 'Plugins/AdvancedSongFiltersSmoke.dll'
$gameExe = Join-Path $GameRoot 'Beat Saber.exe'
$report = Join-Path $runtimeCheck 'report.json'
$process = $null
$ownsPlugin = $false
$ownsHelper = $false
$runMutex = [Threading.Mutex]::new($false, 'Local\AdvancedSongFilters.RuntimeSmoke')
$ownsMutex = $false

function Assert-File([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Missing $Label`: $Path" }
}

try {
    try { $ownsMutex = $runMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another AdvancedSongFilters runtime check is already running.' }
    Assert-File $gameExe 'Beat Saber executable'
    Assert-File $compiler 'C# compiler'
    Assert-File $manifest 'smoke manifest'
    Assert-File (Join-Path $GameRoot 'Plugins/AdvancedSongFilters.dll') 'AdvancedSongFilters main plugin'

    $existing = @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 0) { throw 'A Beat Saber process is already running; refusing to attach or stop it.' }
    if (Test-Path -LiteralPath $plugin -PathType Leaf) { throw "Smoke plugin already exists: $plugin" }

    [IO.Directory]::CreateDirectory($runtimeCheck) | Out-Null
    foreach ($owned in @($helper, $report,
        (Join-Path $runtimeCheck 'selected-panel.png'),
        (Join-Path $runtimeCheck 'unselected-panel.png'),
        (Join-Path $runtimeCheck 'scene-baseline.png'),
        (Join-Path $runtimeCheck 'numeric-panel.png'))) {
        if (Test-Path -LiteralPath $owned -PathType Leaf) { Remove-Item -LiteralPath $owned -Force }
    }

    $managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
    $references = @(
        (Join-Path $managed 'IPA.Loader.dll'),
        (Join-Path $managed 'UnityEngine.CoreModule.dll'),
        (Join-Path $managed 'UnityEngine.UI.dll'),
        (Join-Path $managed 'UnityEngine.UIModule.dll'),
        (Join-Path $managed 'UnityEngine.TextRenderingModule.dll'),
        (Join-Path $managed 'UnityEngine.TextCoreFontEngineModule.dll'),
        (Join-Path $managed 'UnityEngine.UIElementsModule.dll'),
        (Join-Path $managed 'UnityEngine.ImageConversionModule.dll'),
        (Join-Path $managed 'Unity.RenderPipelines.Core.Runtime.dll'),
        (Join-Path $managed 'Unity.RenderPipelines.Universal.Runtime.dll'),
        (Join-Path $managed 'Unity.TextMeshPro.dll'),
        (Join-Path $managed 'HMUI.dll'),
        (Join-Path $managed 'SegmentedControl.dll'),
        (Join-Path $managed 'BeatSaber.ViewSystem.dll'),
        (Join-Path $managed 'Main.dll'),
        (Join-Path $managed 'DataModels.dll'),
        (Join-Path $managed 'BeatmapCore.dll'),
        (Join-Path $managed 'AdditionalContentModel.Interfaces.dll'),
        (Join-Path $managed 'Zenject.dll'),
        (Join-Path $managed 'Zenject-usage.dll'),
        (Join-Path $managed 'BGLib.UnityExtension.dll'),
        (Join-Path $managed 'VRUI.dll'),
        (Join-Path $managed 'VRUI.Interfaces.dll'),
        (Join-Path $managed 'netstandard.dll'),
        (Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'),
        (Join-Path $GameRoot 'Plugins/SongCore.dll'),
        (Join-Path $GameRoot 'Plugins/AdvancedSongFilters.dll')
    )
    foreach ($reference in $references) { Assert-File $reference 'compiler reference' }

    $arguments = @('/nologo', '/target:library', '/optimize+', '/langversion:5', '/codepage:65001',
        ('/out:' + $helper), ('/resource:' + $manifest + ',AdvancedSongFiltersSmoke.manifest.json'),
        (Join-Path $sourceDirectory 'GameSmoke.cs'))
    $arguments += @($references | ForEach-Object { '/r:' + $_ })
    $ownsHelper = $true
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw "Runtime smoke plugin compilation failed with exit code $LASTEXITCODE" }

    Copy-Item -LiteralPath $helper -Destination $plugin
    $ownsPlugin = $true
    # Existing local diagnostics record the launcher token as `fpfc`; keep
    # the requested `--fpfc` spelling too so either parser path enables FPFC.
    $launch = [Diagnostics.ProcessStartInfo]::new()
    $launch.FileName = $gameExe
    $launch.Arguments = '--fpfc fpfc'
    $launch.WorkingDirectory = $GameRoot
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    $launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $launch.EnvironmentVariables['ADVANCED_SONG_FILTERS_SMOKE_DIR'] = $runtimeCheck
    $process = [Diagnostics.Process]::Start($launch)
    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $FiniteWaitSeconds))
    while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "Runtime smoke exceeded finite wait of $FiniteWaitSeconds seconds; only the process started by this script was stopped."
    }
}
finally {
    try {
        if ($process -ne $null -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
        if ($ownsPlugin -and (Test-Path -LiteralPath $plugin -PathType Leaf)) { Remove-Item -LiteralPath $plugin -Force }
        if ($ownsHelper -and (Test-Path -LiteralPath $helper -PathType Leaf)) { Remove-Item -LiteralPath $helper -Force }
        $pdb = [IO.Path]::ChangeExtension($helper, '.pdb')
        if ($ownsHelper -and (Test-Path -LiteralPath $pdb -PathType Leaf)) { Remove-Item -LiteralPath $pdb -Force }
    } finally {
        if ($ownsMutex) { $runMutex.ReleaseMutex() }
        $runMutex.Dispose()
    }
}

Assert-File $report 'runtime smoke report'
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not [bool]$result.passed) {
    throw "Runtime smoke report failed. See $report and the retained screenshots under $runtimeCheck."
}
if ($process -eq $null -or -not $process.HasExited -or $process.ExitCode -ne 0) {
    throw "Runtime smoke process did not exit successfully; report passed=$($result.passed), exitCode=$($process.ExitCode)."
}
Write-Output "Runtime smoke passed. Report: $report"
