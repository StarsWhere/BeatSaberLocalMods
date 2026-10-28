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
$gameRootPrefix = $GameRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$sourceDirectory = Join-Path $PSScriptRoot 'camera2_urp_fix'
$managed = Join-Path $GameRoot 'Beat Saber_Data/Managed'
$camera2 = Join-Path $GameRoot 'Plugins/Camera2.dll'
$ipaLoader = Join-Path $managed 'IPA.Loader.dll'
$harmony = Join-Path $GameRoot 'Libs/0Harmony.dll'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$adapterManifest = Get-Content -LiteralPath (Join-Path $sourceDirectory 'Camera2UrpFix.manifest.json') -Raw | ConvertFrom-Json

if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') {
    throw 'This build targets Beat Saber 1.45.2_28848.'
}
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Missing compiler: $compiler" }

$cameraVersion = [Reflection.AssemblyName]::GetAssemblyName($camera2).Version.ToString()
if ($cameraVersion -ne '0.6.119.0') { throw "Expected Camera2 0.6.119.0, found $cameraVersion" }
$ipaVersion = [Reflection.AssemblyName]::GetAssemblyName($ipaLoader).Version.ToString()
if ($ipaVersion -ne '2.0.0.0') { throw "Unexpected IPA.Loader assembly version: $ipaVersion" }

$references = @(
    $ipaLoader,
    $camera2,
    $harmony,
    (Join-Path $GameRoot 'Plugins/BSML.dll'),
    (Join-Path $managed 'Newtonsoft.Json.dll'),
    (Join-Path $managed 'HMUI.dll'),
    (Join-Path $managed 'BeatSaber.ViewSystem.dll'),
    (Join-Path $managed 'BGLib.UnityExtension.dll'),
    (Join-Path $managed 'Unity.TextMeshPro.dll'),
    (Join-Path $managed 'Zenject.dll'),
    (Join-Path $managed 'Zenject-usage.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.UIModule.dll'),
    (Join-Path $managed 'UnityEngine.UI.dll'),
    (Join-Path $managed 'Unity.RenderPipelines.Core.Runtime.dll'),
    (Join-Path $managed 'Unity.RenderPipelines.Universal.Runtime.dll'),
    (Join-Path $managed 'netstandard.dll')
)
foreach ($reference in $references) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing reference: $reference" }
}

[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$outputPlugins = Join-Path $OutputDirectory 'Plugins'
[IO.Directory]::CreateDirectory($outputPlugins) | Out-Null
$buildDll = Join-Path $BuildDirectory 'Camera2UrpFix.dll'
$releaseDll = Join-Path $outputPlugins 'Camera2UrpFix.dll'

$arguments = @(
    '/nologo', '/target:library', '/optimize+', '/codepage:65001', '/warn:4', '/langversion:5', '/r:System.Xml.Linq.dll',
    ('/out:' + $buildDll),
    ('/resource:' + (Join-Path $sourceDirectory 'Camera2UrpFix.manifest.json') + ',Camera2UrpFix.manifest.json')
)
$arguments += @($references | ForEach-Object { '/r:' + $_ })
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Camera2UrpFix compilation failed.' }

[Reflection.AssemblyName]::GetAssemblyName($buildDll) | Out-Null
Copy-Item -LiteralPath $buildDll -Destination $releaseDll

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-GameRelativePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -eq $GameRoot) {
        throw "Reference must be a file below GameRoot: $full"
    }
    if (-not $full.StartsWith($gameRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reference is outside GameRoot: $full"
    }
    $full.Substring($gameRootPrefix.Length).Replace('\', '/')
}

$provenance = [ordered]@{
    plugin = 'Camera2UrpFix'
    version = $adapterManifest.version
    gameBuild = '1.45.2_28848'
    camera2Version = $cameraVersion
    camera2Sha256 = Get-Sha256 $camera2
    pluginSha256 = Get-Sha256 $releaseDll
    references = @($references | ForEach-Object {
        [ordered]@{
            path = Get-GameRelativePath $_
            sha256 = Get-Sha256 $_
        }
    })
}
$provenancePath = Join-Path $OutputDirectory 'provenance.json'
[IO.File]::WriteAllText($provenancePath, ($provenance | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$checksumPath = Join-Path $OutputDirectory 'SHA256SUMS.txt'
[IO.File]::WriteAllText($checksumPath, ((Get-Sha256 $releaseDll) + "  Plugins/Camera2UrpFix.dll`n"), [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Output = $releaseDll
    SHA256 = Get-Sha256 $releaseDll
    Provenance = $provenancePath
    Checksums = $checksumPath
} | ConvertTo-Json
