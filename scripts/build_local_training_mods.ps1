[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$OutputDirectory,
    [string]$BuildDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ReleaseVersion = '0.4.0',
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe',
    [switch]$UiPluginsOnly,
    [switch]$NoteMotionOnly,
    [switch]$SettingsFlowOnly,
    [switch]$EnergyOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'Unsupported Beat Saber build.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot ('releases/TrainingMods-'+$ReleaseVersion+'-bs1.45.2') }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/training-mods/package-build' }
$OutputDirectory = Resolve-RepoBuildPath $OutputDirectory
$BuildDirectory = Resolve-RepoBuildPath $BuildDirectory
if ($EnergyOnly -and $UiPluginsOnly) { throw 'EnergyOnly cannot be combined with UiPluginsOnly.' }
if (@($NoteMotionOnly,$SettingsFlowOnly,$EnergyOnly).Where({$_}).Count -gt 1) { throw 'Choose one plugin selection.' }
if ($NoteMotionOnly -or $SettingsFlowOnly -or $EnergyOnly) { $UiPluginsOnly = $true }
& (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory | Out-Null
$plugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($plugins) | Out-Null
$trainingPlugins = if ($EnergyOnly) { @('ActivityEnergyCounter') } elseif ($NoteMotionOnly) { @('NoteMotionControl') } else { @('NoteMotionControl','ActivityEnergyCounter') }
foreach ($name in $trainingPlugins) {
    $project = Join-Path $PSScriptRoot "training_mods/$name/$name.csproj"
    $build = Join-Path $BuildDirectory $name
    & $DotnetPath build $project -c Release --nologo -v quiet ('-p:GameRoot='+$GameRoot) ('-p:RepoRoot='+$RepoRoot) ('-p:OutputPath='+$build+'/') '-p:AppendTargetFrameworkToOutputPath=false'
    if ($LASTEXITCODE -ne 0) { throw "$name compilation failed." }
    Copy-Item -LiteralPath (Join-Path $build "$name.dll") -Destination $plugins -Force
    if (Test-Path -LiteralPath (Join-Path $build "$name.pdb")) { Copy-Item -LiteralPath (Join-Path $build "$name.pdb") -Destination $plugins -Force }
}
& (Join-Path $PSScriptRoot 'build_local_mod_ui_chinese.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory -BuildDirectory (Join-Path $BuildDirectory 'localization') -LocalizationDirectory (Join-Path $RepoRoot 'localization')
if (!$UiPluginsOnly -or $SettingsFlowOnly) {
    & (Join-Path $PSScriptRoot 'build_local_room_calibration.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory -BuildDirectory (Join-Path $BuildDirectory 'room')
}
if (!$UiPluginsOnly) {
    & (Join-Path $PSScriptRoot 'build_local_advanced_song_filters.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory -BuildDirectory (Join-Path $BuildDirectory 'filters')
    & (Join-Path $PSScriptRoot 'build_local_beat_sage.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory -BuildDirectory (Join-Path $BuildDirectory 'beat-sage') -DotnetPath $DotnetPath
}
Write-Output $OutputDirectory
