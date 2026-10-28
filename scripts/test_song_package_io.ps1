[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputDirectory = '',
    [string]$BuildDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $GameRoot '.tmp/song-package-io-tests/build' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $GameRoot '.tmp/song-package-io-tests/obj' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$project = Join-Path $GameRoot 'scripts/shared_song_import/Tests/SongPackageIO.Tests.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project: $project" }

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
& $dotnet restore $project -r win7-x64 `
    "-p:GameRoot=$GameRoot" `
    "-p:OutputPath=$OutputDirectory\"
if ($LASTEXITCODE -ne 0) { throw 'SongPackageIO test restore failed.' }
& $dotnet build $project --configuration Release --no-restore `
    "-p:GameRoot=$GameRoot" `
    "-p:OutputPath=$OutputDirectory\"
if ($LASTEXITCODE -ne 0) { throw 'SongPackageIO test compilation failed.' }

$testAssembly = Join-Path $OutputDirectory 'SongPackageIO.Tests.exe'
if (!(Test-Path -LiteralPath $testAssembly -PathType Leaf)) { throw "Build did not produce: $testAssembly" }
& $testAssembly
if ($LASTEXITCODE -ne 0) { throw 'SongPackageIO tests failed.' }
