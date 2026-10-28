[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot=[IO.Path]::GetFullPath($GameRoot)
$BuildDirectory=[IO.Path]::GetFullPath($BuildDirectory)
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$managed=Join-Path $GameRoot 'Beat Saber_Data/Managed'
$references=@('BeatSaber.Settings.dll','Unity.Mathematics.dll','UnityEngine.CoreModule.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$output=Join-Path $BuildDirectory 'RoomCalibrationTests.exe'
$arguments=@('/nologo','/target:exe','/codepage:65001','/langversion:5',('/out:'+$output),
    (Join-Path $PSScriptRoot 'room_calibration/Core.cs'),(Join-Path $PSScriptRoot 'room_calibration/Tests/CoreTests.cs'))
$arguments+=$references | ForEach-Object { '/r:'+$_ }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @arguments
if($LASTEXITCODE -ne 0) { throw 'RoomCalibration tests compilation failed' }
foreach($reference in $references) { Copy-Item -LiteralPath $reference -Destination (Join-Path $BuildDirectory ([IO.Path]::GetFileName($reference))) }
& $output
if($LASTEXITCODE -ne 0) { throw 'RoomCalibration core tests failed' }
