[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$FiniteWaitSeconds = 300,
    [string]$FixtureSource = 'Beat Saber_Data/CustomLevels/BeatSage_Aiobahn_somunia_wish',
    [switch]$CompileOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (!$CompileOnly -and @(Get-Process -Name 'Beat Saber' -ErrorAction SilentlyContinue).Count) { throw 'Beat Saber is already running.' }
$runId = [Guid]::NewGuid().ToString('N')
$directory = Join-Path $GameRoot ('.tmp/song-duplicate-cleaner/runtime-' + $runId)
$levelsRoot = [IO.Path]::GetFullPath((Join-Path $GameRoot 'Beat Saber_Data/CustomLevels'))
$fixtureA = [IO.Path]::GetFullPath((Join-Path $levelsRoot ('SongDuplicateCleanerSmoke-' + $runId + '-A')))
$fixtureB = [IO.Path]::GetFullPath((Join-Path $levelsRoot ('SongDuplicateCleanerSmoke-' + $runId + '-B')))
$fixturePrefix = $levelsRoot.TrimEnd('\') + '\SongDuplicateCleanerSmoke-' + $runId + '-'
foreach ($path in @($fixtureA,$fixtureB)) {
    if (!$path.StartsWith($fixturePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture target' }
    if (Test-Path -LiteralPath $path) { throw 'Fixture path already exists' }
}
$source = Join-Path $PSScriptRoot 'song_duplicate_cleaner/Tests'
$playlistPath = Join-Path $GameRoot 'Playlists/BeatSage.bplist'
$playlistExisted = Test-Path -LiteralPath $playlistPath -PathType Leaf
$originalPlaylist = if ($playlistExisted) { [IO.File]::ReadAllBytes($playlistPath) } else { $null }
$destination = Join-Path $GameRoot 'Plugins/SongDuplicateCleanerSmoke.dll'
if (Test-Path -LiteralPath $destination) { throw 'Runtime check plugin already exists' }
[IO.Directory]::CreateDirectory($directory) | Out-Null
$references = @(Get-ChildItem -LiteralPath (Join-Path $GameRoot 'Beat Saber_Data/Managed') -Filter '*.dll' -File | ForEach-Object { $_.FullName })
$references += @('Plugins/SongDuplicateCleaner.dll','Plugins/SongCore.dll','Plugins/BSML.dll') | ForEach-Object { Join-Path $GameRoot $_ }
$helper = Join-Path $directory 'SongDuplicateCleanerSmoke.dll'
$arguments = @('/nologo','/noconfig','/target:library','/nostdlib+','/codepage:65001','/langversion:5',('/out:'+$helper),
    ('/resource:'+(Join-Path $source 'RuntimeSmoke.manifest.json')+',SongDuplicateCleanerSmoke.manifest.json'),
    (Join-Path $source 'RuntimeSmoke.cs'))
$arguments += $references | ForEach-Object { '/r:'+$_ }
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @arguments
if ($LASTEXITCODE -ne 0) { throw 'Runtime check compilation failed' }
if ($CompileOnly) { Write-Output $helper; return }
$process = $null
$ownsHelper = $false
try {
    $sourceSong = if ([IO.Path]::IsPathRooted($FixtureSource)) { $FixtureSource } else { Join-Path $GameRoot $FixtureSource }
    $infoPath = Join-Path $sourceSong 'Info.dat'
    $info = Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json
    if (!$info.PSObject.Properties['_songName']) { throw 'Fixture source must use v2 Info.dat' }
    Copy-Item -LiteralPath $sourceSong -Destination $fixtureA -Recurse
    $info._songName = '清理验证 ' + $runId
    $info._levelAuthorName = 'Duplicate Cleaner Test'
    $info | ConvertTo-Json -Depth 32 -Compress | Set-Content -LiteralPath (Join-Path $fixtureA 'Info.dat') -Encoding utf8NoBOM
    Copy-Item -LiteralPath $fixtureA -Destination $fixtureB -Recurse
    Copy-Item -LiteralPath $helper -Destination $destination
    $ownsHelper = $true
    $launch = [Diagnostics.ProcessStartInfo]::new()
    $launch.FileName = Join-Path $GameRoot 'Beat Saber.exe'
    $launch.Arguments = '--fpfc fpfc'
    $launch.WorkingDirectory = $GameRoot
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    $launch.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $launch.EnvironmentVariables['DUPLICATE_CLEANER_SMOKE_DIR'] = $directory
    $launch.EnvironmentVariables['DUPLICATE_CLEANER_SMOKE_A'] = $fixtureA
    $launch.EnvironmentVariables['DUPLICATE_CLEANER_SMOKE_B'] = $fixtureB
    $process = [Diagnostics.Process]::Start($launch)
    Write-Output ('Runtime process '+$process.Id+'; report '+(Join-Path $directory 'report.json'))
    $deadline = [DateTime]::UtcNow.AddSeconds($FiniteWaitSeconds)
    while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500; $process.Refresh() }
    if (!$process.HasExited) { throw 'Runtime check exceeded its finite deadline' }
    $report = Join-Path $directory 'report.json'
    if (!(Test-Path -LiteralPath $report)) { throw 'Runtime report missing' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Write-Output ($result | ConvertTo-Json -Depth 8)
    if (!$result.passed -or $process.ExitCode -ne 0) { throw "Runtime check failed: $report" }
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { Stop-Process -Id $process.Id }
        $process.Dispose()
    }
    if ($ownsHelper -and (Test-Path -LiteralPath $destination)) { Remove-Item -LiteralPath $destination }
    foreach ($fixture in @($fixtureA,$fixtureB)) {
        $resolvedFixture = [IO.Path]::GetFullPath($fixture)
        if (!$resolvedFixture.StartsWith($fixturePrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside test fixture prefix' }
        if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse }
    }
    if ($playlistExisted) { [IO.File]::WriteAllBytes($playlistPath,$originalPlaylist) }
    elseif (Test-Path -LiteralPath $playlistPath) { Remove-Item -LiteralPath $playlistPath }
}
