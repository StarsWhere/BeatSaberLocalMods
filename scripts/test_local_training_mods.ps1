[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BuildDirectory,
    [string]$DotnetPath = 'C:/Program Files/dotnet/dotnet.exe',
    [switch]$NoteMotionOnly,
    [switch]$EnergyOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (!$BuildDirectory) { $BuildDirectory = Join-Path $GameRoot '.tmp/training-mods/core-tests' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
if (@($NoteMotionOnly,$EnergyOnly).Where({$_}).Count -gt 1) { throw 'Choose one training test selection.' }
if (!$NoteMotionOnly) { & (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot | Out-Null }
$projects = @(
    'training_mods/Tests/ProfileStore.Tests.csproj',
    'training_mods/NoteMotionControl/Tests/NoteMotionControl.Tests.csproj',
    'training_mods/NoteMotionControl/Reduction/Tests/NoteMotionControl.Reduction.Tests.csproj',
    'training_mods/ActivityEnergyCounter/Tests/ActivityEnergyCounter.Tests.csproj'
)
if ($NoteMotionOnly) { $projects = $projects | Where-Object { $_ -match 'ProfileStore|NoteMotionControl' } }
if ($EnergyOnly) { $projects = $projects | Where-Object { $_ -match 'ActivityEnergyCounter' } }
foreach ($relative in $projects) {
    $project = Join-Path $PSScriptRoot $relative
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    $output = Join-Path $BuildDirectory $name
    & $DotnetPath build $project -c Release --nologo -v quiet ('-p:GameRoot='+$GameRoot) ('-p:BeatSaberDir='+$GameRoot) ('-p:OutputPath='+$output+'/') '-p:AppendTargetFrameworkToOutputPath=false'
    if ($LASTEXITCODE -ne 0) { throw "$name compilation failed." }
    [xml]$xml = Get-Content -LiteralPath $project -Raw
    $framework = [string](@($xml.Project.PropertyGroup | Where-Object {$_.PSObject.Properties.Name -contains 'TargetFramework'})[0].TargetFramework)
    if ($framework -eq 'net472') {
        if ($name -eq 'ProfileStore.Tests') { & (Join-Path $output ($name+'.exe')) (Join-Path $BuildDirectory ('data-'+[guid]::NewGuid().ToString('N'))) }
        else { & (Join-Path $output ($name+'.exe')) }
    } else { & $DotnetPath (Join-Path $output ($name+'.dll')) }
    if ($LASTEXITCODE -ne 0) { throw "$name failed." }
}
& (Join-Path $PSScriptRoot 'test_mod_ui_chinese.ps1') -BuildDirectory (Join-Path $BuildDirectory 'localization')
Write-Output 'Training mods core and localization checks passed.'
