[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$OutputDirectory,
    [string]$BuildDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot 'releases/UnityShutdownFix-0.1.0-bs1.45.2' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/v4-load-exit/build' }
$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'Unsupported game build.' }
$playerHash = (Get-FileHash -LiteralPath (Join-Path $GameRoot 'UnityPlayer.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
if ($playerHash -ne '826f0d671c0d3d7c852c32051a6ebd6a3d715d27e5323a5e34dbb33eef407fdd') { throw 'Unverified Unity player.' }
$source = Join-Path $PSScriptRoot 'unity_shutdown_fix'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$plugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
[IO.Directory]::CreateDirectory($plugins) | Out-Null
$dll = Join-Path $BuildDirectory 'UnityShutdownFix.dll'
$arguments = @('/nologo','/target:library','/platform:x64','/optimize+','/codepage:65001','/warn:4','/langversion:5',('/out:'+$dll),
    ('/r:'+(Join-Path $GameRoot 'Beat Saber_Data/Managed/IPA.Loader.dll')),
    ('/resource:'+(Join-Path $source 'UnityShutdownFix.manifest.json')+',UnityShutdownFix.manifest.json'))
$arguments += @(Get-ChildItem -LiteralPath $source -File -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'UnityShutdownFix compilation failed.' }
Copy-Item -LiteralPath $dll -Destination $plugins
$sourceOutput = Join-Path $OutputDirectory 'source/scripts/unity_shutdown_fix'
[IO.Directory]::CreateDirectory($sourceOutput) | Out-Null
Get-ChildItem -LiteralPath $source -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $sourceOutput }
foreach ($name in @('build_local_unity_shutdown_fix.ps1','build_local_paths.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $OutputDirectory 'source/scripts')
}
$pluginHash = (Get-FileHash -LiteralPath (Join-Path $plugins 'UnityShutdownFix.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{plugin='UnityShutdownFix';version='0.1.0';gameBuild='1.45.2_28848';unityPlayerSha256=$playerHash;pluginSha256=$pluginHash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8NoBOM
($pluginHash+'  Plugins/UnityShutdownFix.dll') | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output $OutputDirectory
