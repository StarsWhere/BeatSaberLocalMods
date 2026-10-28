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
    throw 'This build targets Beat Saber 1.45.2_28848'
}
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sources = Join-Path $PSScriptRoot 'advanced_song_filters'
$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$plugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($plugins) | Out-Null
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$references = @('IPA.Loader.dll','UnityEngine.CoreModule.dll','UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','Unity.TextMeshPro.dll',
    'HMUI.dll','SegmentedControl.dll','BeatSaber.ViewSystem.dll','Main.dll','DataModels.dll','BeatmapCore.dll','AdditionalContentModel.Interfaces.dll',
    'Zenject.dll','Zenject-usage.dll','BGLib.UnityExtension.dll','VRUI.dll','VRUI.Interfaces.dll','netstandard.dll') |
    ForEach-Object { Join-Path $managed $_ }
$references += Join-Path $RepoRoot '.tmp/local-mod-ui/build/LocalModUI.dll'
$references += @('Plugins/BetterSongList.dll','Plugins/SongCore.dll','Plugins/SiraUtil.dll',
    'Plugins/BSML.dll','Libs/0Harmony.dll','Libs/Newtonsoft.Json.dll') | ForEach-Object { Join-Path $GameRoot $_ }
foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference: $reference" } }
$output = Join-Path $plugins 'AdvancedSongFilters.dll'
$arguments = @('/nologo','/target:library','/optimize+','/codepage:65001',('/out:'+$output),
    ('/resource:'+(Join-Path $sources 'AdvancedSongFilters.manifest.json')+',AdvancedSongFilters.manifest.json'),
    ('/resource:'+(Join-Path $sources 'Panel.bsml')+',AdvancedSongFilters.Panel.bsml'))
$arguments += @($references | ForEach-Object { '/r:'+$_ })
$arguments += @('Core.cs','Editing.cs','Persistence.cs','OwnedParser.cs','Plugin.cs','Runtime.cs','Panel.cs') | ForEach-Object { Join-Path $sources $_ }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'AdvancedSongFilters compilation failed' }
[xml](Get-Content -LiteralPath (Join-Path $sources 'Panel.bsml') -Raw) | Out-Null
$manifestInfo = Get-Content -LiteralPath (Join-Path $sources 'AdvancedSongFilters.manifest.json') -Raw | ConvertFrom-Json
$provenance = [ordered]@{
    plugin = 'AdvancedSongFilters'; version = $manifestInfo.version; gameBuild = '1.45.2_28848'
    sha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    references = @($references | ForEach-Object { [ordered]@{
        path=[IO.Path]::GetRelativePath($GameRoot,$_).Replace('\','/')
        sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    } })
}
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8NoBOM
$provenance.sha256+'  Plugins/AdvancedSongFilters.dll' | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output $output
