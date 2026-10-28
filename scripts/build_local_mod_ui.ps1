[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$BuildDirectory,
    [string]$OutputDirectory,
    [switch]$Bundle,
    [switch]$CreateArchive
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if ((Get-Content -LiteralPath (Join-Path $GameRoot 'BeatSaberVersion.txt') -Raw).Trim() -ne '1.45.2_28848') { throw 'Unsupported game build.' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/local-mod-ui/bundle-build' }
$BuildDirectory = Resolve-RepoBuildPath $BuildDirectory
if ($Bundle -and !$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot 'releases/LocalModUI-0.1.0-bs1.45.2-win-x64' }
if ($CreateArchive -and !$Bundle) { throw 'CreateArchive requires Bundle.' }
if ($OutputDirectory) { $OutputDirectory = Resolve-RepoBuildPath $OutputDirectory }

$uiBuild = Join-Path $RepoRoot '.tmp/local-mod-ui/build'
[IO.Directory]::CreateDirectory($uiBuild) | Out-Null
$source = Join-Path $PSScriptRoot 'local_mod_ui'
$compiler = Get-ChildItem -LiteralPath 'C:/Program Files/dotnet/sdk' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'Roslyn/bincore/csc.dll' }
if (!$compiler -or !(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'A local .NET SDK with the Roslyn compiler is required.' }
$output = Join-Path $uiBuild 'LocalModUI.dll'
$arguments = @('/nologo','/target:library','/nostdlib+','/langversion:latest','/optimize+','/codepage:65001',('/out:'+$output))
$arguments += Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -File -Filter '*.dll' | ForEach-Object { '/r:'+$_.FullName }
$arguments += @('Plugins/BSML.dll','Plugins/SiraUtil.dll','Libs/0Harmony.dll') | ForEach-Object { '/r:'+(Join-Path $GameRoot $_) }
$arguments += Get-ChildItem -LiteralPath $source -File -Filter '*.cs' | ForEach-Object { $_.FullName }
$arguments += @(('/resource:'+(Join-Path $source 'manifest.json')+',LocalModUI.manifest.json'),('/resource:'+(Join-Path $source 'Center.bsml')+',LocalModUI.Center.bsml'))
& 'C:/Program Files/dotnet/dotnet.exe' $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'LocalModUI compilation failed.' }
if ($OutputDirectory) {
    $plugins = Join-Path $OutputDirectory 'Plugins'
    [IO.Directory]::CreateDirectory($plugins) | Out-Null
    Copy-Item -LiteralPath $output -Destination (Join-Path $plugins 'LocalModUI.dll') -Force
}
Write-Output $output

if ($Bundle) {
    & (Join-Path $PSScriptRoot 'build_local_training_mods.ps1') -GameRoot $GameRoot -UiPluginsOnly -BuildDirectory (Join-Path $BuildDirectory 'training') -OutputDirectory $OutputDirectory
    foreach ($name in @('room_calibration','advanced_song_filters','song_duplicate_cleaner','voice_input','beat_sage','camera2_urp_fix','unity_shutdown_fix')) {
        & (Join-Path $PSScriptRoot ('build_local_'+$name+'.ps1')) -GameRoot $GameRoot -BuildDirectory (Join-Path $BuildDirectory $name) -OutputDirectory $OutputDirectory
    }
    $pluginNames = @('LocalModUI','NoteMotionControl','ActivityEnergyCounter','RoomCalibration','ModUiChinese','AdvancedSongFilters','SongDuplicateCleaner','BeatSage','VoiceInput','Camera2UrpFix','UnityShutdownFix')
    $paths = @($pluginNames | ForEach-Object { 'Plugins/'+$_.ToString()+'.dll' }) + @('Libs/SongPackageIO.dll')
    $files = @(foreach ($relative in $paths) {
        $path = Join-Path $OutputDirectory $relative
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Bundle output is missing: $relative" }
        [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $manifest = [ordered]@{schemaVersion=1;name='LocalModUI';version='0.1.0';gameBuild='1.45.2_28848';files=$files}
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8NoBOM
    $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json') -Encoding utf8NoBOM
    $sums = @(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse | Where-Object { $_.Name -notin @('SHA256SUMS','SHA256SUMS.txt') } | Sort-Object FullName | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetRelativePath($OutputDirectory,$_.FullName).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),$sums,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS'),$sums,[Text.UTF8Encoding]::new($false))
    if ($CreateArchive) {
        $archive = $OutputDirectory.TrimEnd('\','/')+'.zip'
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
        [IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory,$archive,[IO.Compression.CompressionLevel]::Optimal,$false)
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($archive+'.sha256',$hash+'  '+[IO.Path]::GetFileName($archive)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
        Write-Output $archive
    }
}
