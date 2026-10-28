#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')

$gameBuild = (Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim()
if ($gameBuild -ne '1.45.2_28848') { throw 'This release targets Beat Saber 1.45.2_28848.' }
$changes = @(& git -C $RepoRoot status --porcelain)
if ($LASTEXITCODE -ne 0 -or $changes.Count) { throw 'Commit the source changes before creating a release.' }
$revision = (& git -C $RepoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine the source commit.' }
$releaseName = 'BeatSaberLocalMods-' + $Version + '-bs1.45.2-win-x64'
$releaseRoot = Join-Path $RepoRoot ('releases/' + $releaseName)
if (Test-Path -LiteralPath $releaseRoot) { throw "Release already exists; choose a new version or move it before rebuilding: $releaseRoot" }
$buildRoot = Join-Path $RepoRoot ('.tmp/release-' + [Guid]::NewGuid().ToString('N'))
$staging = Join-Path $buildRoot 'bundle'
[IO.Directory]::CreateDirectory($staging) | Out-Null

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Copy-PackageFile([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Destination)) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination
}
function Complete-Package([string]$Directory, [string]$Archive, [object[]]$Mods) {
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-PackageFile (Join-Path $RepoRoot $name) (Join-Path $Directory $name)
    }
    Copy-Item -LiteralPath (Join-Path $RepoRoot 'licenses') -Destination $Directory -Recurse
    [ordered]@{
        source = 'https://github.com/StarsWhere/BeatSaberLocalMods'
        commit = $revision
        gameBuild = $gameBuild
        mods = @($Mods | ForEach-Object { [ordered]@{ id=$_.id; version=$_.version; dependsOn=$_.dependsOn } })
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory 'release.json') -Encoding utf8NoBOM
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName)
    $sums = @($files | ForEach-Object { (Get-Sha256 $_.FullName) + '  ' + [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\','/') })
    [IO.File]::WriteAllLines((Join-Path $Directory 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
    [IO.Compression.ZipFile]::CreateFromDirectory($Directory, $Archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            if (!$entry.Name) { continue }
            $original = Join-Path $Directory $entry.FullName
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() }
            finally { $sha.Dispose(); $stream.Dispose() }
            if ($actual -ne (Get-Sha256 $original)) { throw "Archive content mismatch: $($entry.FullName)" }
        }
    } finally { $zip.Dispose() }
}

$manifestPaths = @(
    'local_mod_ui/manifest.json',
    'training_mods/NoteMotionControl/manifest.json',
    'training_mods/ActivityEnergyCounter/manifest.json',
    'room_calibration/RoomCalibration.manifest.json',
    'mod_ui_chinese/ModUiChinese.manifest.json',
    'advanced_song_filters/AdvancedSongFilters.manifest.json',
    'song_duplicate_cleaner/SongDuplicateCleaner.manifest.json',
    'beat_sage/manifest.json',
    'voice_input/manifest.json',
    'camera2_urp_fix/Camera2UrpFix.manifest.json',
    'unity_shutdown_fix/UnityShutdownFix.manifest.json'
)
$mods = @($manifestPaths | ForEach-Object { Get-Content -LiteralPath (Join-Path $PSScriptRoot $_) -Raw | ConvertFrom-Json })
& (Join-Path $PSScriptRoot 'build_local_mod_ui.ps1') -GameRoot $GameRoot -BuildDirectory (Join-Path $buildRoot 'build') -OutputDirectory $staging -Bundle

$expectedDlls = @($mods | ForEach-Object { $_.id + '.dll' } | Sort-Object)
$actualDlls = @(Get-ChildItem -LiteralPath (Join-Path $staging 'Plugins') -File -Filter '*.dll' | Select-Object -ExpandProperty Name | Sort-Object)
if (@(Compare-Object $expectedDlls $actualDlls).Count) { throw 'The bundle must contain exactly the eleven declared plugins.' }
foreach ($mod in $mods) {
    $dll = Join-Path $staging ('Plugins/' + $mod.id + '.dll')
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($dll))
    $resources = @($assembly.GetManifestResourceNames() | Where-Object { $_ -like '*manifest.json' })
    if ($resources.Count -ne 1) { throw "Expected one embedded plugin manifest: $($mod.id)" }
    $reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream($resources[0]))
    try { $embedded = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($embedded.id -ne $mod.id -or $embedded.version -ne $mod.version -or $embedded.gameVersion -ne '1.45.2') {
        throw "Built plugin metadata differs from the source: $($mod.id)"
    }
}
$allowedLibraries = @('SongPackageIO.dll','NJsonSchema.dll','NJsonSchema.Annotations.dll','Namotion.Reflection.dll','Newtonsoft.Json.dll','TagLibSharp.dll','System.Text.Json.dll','System.Text.Encodings.Web.dll','System.Buffers.dll','System.Threading.Tasks.Extensions.dll','Microsoft.Bcl.AsyncInterfaces.dll','System.IO.Pipelines.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.ValueTuple.dll')
$libraries = @(Get-ChildItem -LiteralPath (Join-Path $staging 'Libs') -File -Filter '*.dll')
foreach ($library in $libraries) { if ($library.Name -notin $allowedLibraries) { throw "Undeclared runtime library: $($library.Name)" } }
if (!(Test-Path -LiteralPath (Join-Path $staging 'Libs/SongPackageIO.dll'))) { throw 'Missing shared SongPackageIO runtime library.' }

# Corresponding unmodified TagLibSharp source accompanies the LGPL runtime.
$thirdPartySource = Join-Path $RepoRoot '.tmp/publish-inputs/TagLibSharp-2.3.0-source.zip'
$sourceSha = 'ab6fab7e22c7423e428f591d7525a38ca283d217a49f9772eb9a29cd682c175d'
if (!(Test-Path -LiteralPath $thirdPartySource)) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $thirdPartySource)) | Out-Null
    Invoke-WebRequest -Uri 'https://codeload.github.com/mono/taglib-sharp/zip/b5ae84f2e84087bf160bb0471420200dd2b5d809' -OutFile $thirdPartySource -TimeoutSec 120
}
if ((Get-Sha256 $thirdPartySource) -ne $sourceSha) { throw 'TagLibSharp source archive checksum mismatch.' }
$taglibPackage = Join-Path $env:USERPROFILE '.nuget/packages/taglibsharp/2.3.0/lib/net462/TagLibSharp.dll'
if (!(Test-Path -LiteralPath $taglibPackage) -or (Get-Sha256 $taglibPackage) -ne (Get-Sha256 (Join-Path $staging 'Libs/TagLibSharp.dll'))) {
    throw 'TagLibSharp must be the unmodified NuGet 2.3.0 net462 runtime.'
}

[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
$bundlePackage = Join-Path $buildRoot 'packages/bundle'
foreach ($mod in $mods) {
    Copy-PackageFile (Join-Path $staging ('Plugins/'+$mod.id+'.dll')) (Join-Path $bundlePackage ('Plugins/'+$mod.id+'.dll'))
}
foreach ($library in $libraries) { Copy-PackageFile $library.FullName (Join-Path $bundlePackage ('Libs/'+$library.Name)) }
Complete-Package $bundlePackage (Join-Path $releaseRoot ($releaseName+'.zip')) $mods
foreach ($mod in $mods) {
    $package = Join-Path $buildRoot ('packages/'+$mod.id)
    $includedMods = @($mod)
    Copy-PackageFile (Join-Path $staging ('Plugins/'+$mod.id+'.dll')) (Join-Path $package ('Plugins/'+$mod.id+'.dll'))
    if ($mod.dependsOn.PSObject.Properties.Name -contains 'LocalModUI') {
        Copy-PackageFile (Join-Path $staging 'Plugins/LocalModUI.dll') (Join-Path $package 'Plugins/LocalModUI.dll')
        $includedMods += @($mods | Where-Object id -eq 'LocalModUI')
    }
    if ($mod.id -in @('BeatSage','VoiceInput')) {
        foreach ($library in $libraries) {
            if ($mod.id -eq 'VoiceInput' -and $library.Name -in @('TagLibSharp.dll','SongPackageIO.dll')) { continue }
            Copy-PackageFile $library.FullName (Join-Path $package ('Libs/'+$library.Name))
        }
    }
    $archive = Join-Path $releaseRoot ($mod.id+'-'+$mod.version+'-bs1.45.2-win-x64.zip')
    Complete-Package $package $archive $includedMods
}
Copy-PackageFile $thirdPartySource (Join-Path $releaseRoot 'TagLibSharp-2.3.0-source.zip')
$sourceArchive = Join-Path $releaseRoot ($releaseName+'-source.zip')
& git -C $RepoRoot archive --format=zip ('--output='+$sourceArchive) $revision
if ($LASTEXITCODE -ne 0) { throw 'Source archive creation failed.' }
$releaseFiles = @(Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name)
$checksums = @($releaseFiles | ForEach-Object { (Get-Sha256 $_.FullName)+'  '+$_.Name })
[IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Output ('Validated eleven plugin manifests and all packaged file hashes. Release: '+$releaseRoot)
Write-Output ('Temporary build directory: '+$buildRoot)
