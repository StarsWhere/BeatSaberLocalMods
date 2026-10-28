[CmdletBinding()]
param(
    [string]$GameRoot=(Split-Path -Parent $PSScriptRoot),
    [int]$FiniteWaitSeconds=190,
    [switch]$VisualOnly,
    [switch]$StartupNotice,
    [switch]$StartupOnly,
    [switch]$CompileOnly,
    [ValidateSet('Disabled','Enabled','Unavailable')][string]$AutoCalibration='Disabled',
    [string]$CalibrationAssemblyPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
if(-not $CompileOnly -and @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count) { throw 'Beat Saber is already running; this check will not attach to or stop it.' }
$source=Join-Path $PSScriptRoot 'room_calibration/Tests'
$directory=Join-Path $GameRoot ('.tmp/room-calibration/runtime-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$managed=Join-Path $GameRoot 'Beat Saber_Data/Managed'
$references=@('IPA.Loader.dll','Main.dll','HMLib.dll','BeatSaber.GameSettings.dll','BeatSaber.Settings.dll',
    'BGLib.AppFlow.dll','BGLib.FileStorage.dll','BGLib.UnityExtension.dll','Unity.Mathematics.dll',
    'UnityEngine.CoreModule.dll','UnityEngine.SubsystemsModule.dll','UnityEngine.XRModule.dll','UnityEngine.InputLegacyModule.dll',
    'UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.ScreenCaptureModule.dll',
    'UnityEngine.ImageConversionModule.dll','Unity.RenderPipelines.Core.Runtime.dll','Unity.RenderPipelines.Universal.Runtime.dll',
    'Unity.TextMeshPro.dll','HMUI.dll','BeatSaber.ViewSystem.dll','Zenject.dll','Zenject-usage.dll','netstandard.dll') |
    ForEach-Object { Join-Path $managed $_ }
$references+=@('Plugins/BSML.dll','Libs/0Harmony.dll') | ForEach-Object { Join-Path $GameRoot $_ }
if(!$CalibrationAssemblyPath) { $CalibrationAssemblyPath=Join-Path $GameRoot 'Plugins/RoomCalibration.dll' }
$references+=[IO.Path]::GetFullPath($CalibrationAssemblyPath)
$helper=Join-Path $directory 'RoomCalibrationSmoke.dll'
$destination=Join-Path $GameRoot 'Plugins/RoomCalibrationSmoke.dll'
if(Test-Path -LiteralPath $destination) { throw 'Runtime helper already exists; refusing to replace it.' }
$arguments=@('/nologo','/target:library','/codepage:65001','/langversion:5',('/out:'+$helper),
    ('/resource:'+(Join-Path $source 'RuntimeSmoke.manifest.json')+',RoomCalibrationSmoke.manifest.json'),
    (Join-Path $source 'RuntimeSmoke.cs'))
$arguments+=$references | ForEach-Object { '/r:'+$_ }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @arguments
if($LASTEXITCODE -ne 0) { throw 'Runtime helper compilation failed' }
if($CompileOnly) { Write-Output "Runtime helper compiled: $helper"; return }
$process=$null
try {
    Copy-Item -LiteralPath $helper -Destination $destination
    $launch=[Diagnostics.ProcessStartInfo]::new()
    $launch.FileName=Join-Path $GameRoot 'Beat Saber.exe'
    $launch.Arguments='--fpfc fpfc'; $launch.WorkingDirectory=$GameRoot
    $launch.UseShellExecute=$false; $launch.CreateNoWindow=$true
    $launch.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $launch.EnvironmentVariables['ROOM_CALIBRATION_SMOKE_DIR']=$directory
    $launch.EnvironmentVariables['ROOM_CALIBRATION_SMOKE_AUTO']=$AutoCalibration
    if($VisualOnly) { $launch.EnvironmentVariables['ROOM_CALIBRATION_SMOKE_VISUAL']='1' }
    if($StartupNotice) { $launch.EnvironmentVariables['ROOM_CALIBRATION_SMOKE_NOTICE']='1' }
    if($StartupOnly) { $launch.EnvironmentVariables['ROOM_CALIBRATION_SMOKE_STARTUP_ONLY']='1' }
    $process=[Diagnostics.Process]::Start($launch)
    $deadline=[DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
    while(-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500; $process.Refresh()
    }
    if(-not $process.HasExited) { throw 'Runtime smoke exceeded its finite deadline.' }
}
finally {
    if($process -ne $null -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if(Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
}
$report=Join-Path $directory 'report.json'
if(-not (Test-Path -LiteralPath $report)) { throw "Runtime report missing: $report" }
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
Write-Output ($result | ConvertTo-Json -Depth 5)
if(-not $result.passed -or $process.ExitCode -ne 0) { throw "Runtime smoke failed: $report" }
Write-Output "Runtime smoke passed: $report"
