[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
& (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory | Out-Null

if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') {
    throw 'SongDuplicateCleaner targets Beat Saber 1.45.2_28848'
}
$source = Join-Path $PSScriptRoot 'song_duplicate_cleaner'
$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$plugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($plugins) | Out-Null
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$references = @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll',
    'IPA.Loader.dll','Main.dll','DataModels.dll','BeatmapCore.dll','AdditionalContentModel.Interfaces.dll',
    'BGLib.UnityExtension.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll',
    'UnityEngine.TextRenderingModule.dll','Unity.TextMeshPro.dll','HMUI.dll','BeatSaber.ViewSystem.dll',
    'Zenject.dll','Zenject-usage.dll','netstandard.dll') | ForEach-Object { Join-Path $managed $_ }
$references += Join-Path $RepoRoot '.tmp/local-mod-ui/build/LocalModUI.dll'
$references += @('Plugins/SongCore.dll','Plugins/BSML.dll','Plugins/SiraUtil.dll') | ForEach-Object { Join-Path $GameRoot $_ }
$dll = Join-Path $plugins 'SongDuplicateCleaner.dll'
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:library','/optimize+','/codepage:65001','/langversion:5',('/out:'+$dll),
    ('/resource:'+(Join-Path $source 'SongDuplicateCleaner.manifest.json')+',SongDuplicateCleaner.manifest.json'),
    ('/resource:'+(Join-Path $source 'CleanerView.bsml')+',SongDuplicateCleaner.CleanerView.bsml'))
$arguments += $references | ForEach-Object { '/r:'+$_ }
$arguments += @('Core.cs','Plugin.cs','CleanerView.cs') | ForEach-Object { Join-Path $source $_ }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @arguments
if ($LASTEXITCODE -ne 0) { throw 'SongDuplicateCleaner compilation failed' }
[xml](Get-Content -LiteralPath (Join-Path $source 'CleanerView.bsml') -Raw) | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $source 'SongDuplicateCleaner.manifest.json') -Raw | ConvertFrom-Json
[ordered]@{
    plugin='SongDuplicateCleaner'; version=$manifest.version; gameBuild='1.45.2_28848'
    sha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    references=@($references | ForEach-Object { [ordered]@{
        path=[IO.Path]::GetRelativePath($GameRoot,$_).Replace('\','/')
        sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8NoBOM
(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()+'  Plugins/SongDuplicateCleaner.dll' |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output $dll
