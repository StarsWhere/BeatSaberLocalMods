[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$LocalizationDirectory,
    [string]$PythonPath = 'python'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
$OutputDirectory=(Resolve-RepoBuildPath $OutputDirectory)
$BuildDirectory=(Resolve-RepoBuildPath $BuildDirectory)
& (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot -OutputDirectory $OutputDirectory | Out-Null

$dotnet = 'C:/Program Files/dotnet/dotnet.exe'
$compiler = Get-ChildItem -LiteralPath 'C:/Program Files/dotnet/sdk' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
if (!$compiler -or !(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'A local .NET SDK with the Roslyn compiler is required.' }
if(-not $LocalizationDirectory){$LocalizationDirectory=Join-Path $RepoRoot 'localization'}
if((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848'){throw 'Unsupported game build'}
$sources=Join-Path $PSScriptRoot 'mod_ui_chinese'
& $PythonPath (Join-Path $sources 'generate_catalog.py') --localization-dir $LocalizationDirectory --output-dir $BuildDirectory
if($LASTEXITCODE -ne 0){throw 'Translation catalog validation failed'}
$plugins=Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($plugins)|Out-Null
$output=Join-Path $plugins 'ModUiChinese.dll'
$managed=Join-Path $GameRoot 'Beat Saber_Data/Managed'
$refs=@('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Xml.Linq.dll','IPA.Loader.dll','UnityEngine.CoreModule.dll','Unity.TextMeshPro.dll','UnityEngine.UI.dll','UnityEngine.UIModule.dll','UnityEngine.TextRenderingModule.dll','UnityEngine.TextCoreFontEngineModule.dll','HMUI.dll','BeatSaber.ViewSystem.dll','BGLib.UnityExtension.dll','Zenject.dll','Zenject-usage.dll','netstandard.dll') | ForEach-Object {Join-Path $managed $_}
$refs += Join-Path $RepoRoot '.tmp/local-mod-ui/build/LocalModUI.dll'
$refs+=@('Plugins/BSML.dll','Plugins/SiraUtil.dll','Libs/0Harmony.dll','Libs/Newtonsoft.Json.dll','Beat Saber_Data/Managed/DataModels.dll','Beat Saber_Data/Managed/BeatmapCore.dll','Beat Saber_Data/Managed/Main.dll','Beat Saber_Data/Managed/BGLib.AppFlow.dll','Beat Saber_Data/Managed/BGLib.Polyglot.dll') | ForEach-Object {Join-Path $GameRoot $_}
$argsList=@('/nologo','/noconfig','/nostdlib+','/target:library','/langversion:latest','/optimize+','/codepage:65001',('/out:'+$output),('/resource:'+(Join-Path $sources 'ModUiChinese.manifest.json')+',ModUiChinese.manifest.json'))
$argsList+=@($refs | ForEach-Object {'/r:'+$_})
$argsList+=@((Join-Path $sources 'TranslationEngine.cs'),(Join-Path $sources 'Settings.cs'),(Join-Path $sources 'ModUiChinese.cs'),(Join-Path $sources 'SymbolFontFallback.cs'),(Join-Path $sources 'GameplayFeedback.cs'),(Join-Path $PSScriptRoot 'training_mods/Shared/ProfileStore.cs'),(Join-Path $BuildDirectory 'GeneratedCatalog.cs'))
$argsList+=('/resource:'+(Join-Path $sources 'Settings.bsml')+',ModUiChinese.Settings.bsml')
& $dotnet $compiler @argsList
if($LASTEXITCODE -ne 0){throw 'Chinese UI plugin compilation failed'}
$provenance=Get-Content -LiteralPath (Join-Path $BuildDirectory 'catalog-provenance.json') -Raw | ConvertFrom-Json -AsHashtable
$provenance.gameVersion='1.45.2_28848'
$provenance.pluginVersion=(Get-Content -LiteralPath (Join-Path $sources 'ModUiChinese.manifest.json') -Raw | ConvertFrom-Json).version
$symbolFont=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Fonts)) 'seguisym.ttf'
$provenance.symbolFont=@{family='Segoe UI Symbol';file='seguisym.ttf';sha256=(Get-FileHash -LiteralPath $symbolFont -Algorithm SHA256).Hash.ToLowerInvariant()}
$provenance.pluginSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$provenance.referenceHashes=@($refs | ForEach-Object {@{path=[IO.Path]::GetRelativePath($GameRoot,$_).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant()}})
$provenance | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8NoBOM
Write-Output $output
