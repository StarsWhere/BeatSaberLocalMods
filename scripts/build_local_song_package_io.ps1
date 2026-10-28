[CmdletBinding()]
param(
    [string]$GameRoot = $env:BEAT_SABER_DIR,
    [string]$OutputDirectory = '',
    [string]$BuildDirectory = '',
    [string]$DotNetPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build_local_paths.ps1')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $RepoRoot '.tmp/beatsage-mod/build/shared' }
if (!$BuildDirectory) { $BuildDirectory = Join-Path $RepoRoot '.tmp/beatsage-mod/build/shared-obj' }
$OutputDirectory = (Resolve-RepoBuildPath $OutputDirectory)
$BuildDirectory = (Resolve-RepoBuildPath $BuildDirectory)
$project = Join-Path $PSScriptRoot 'shared_song_import/SongPackageIO.csproj'
if (!(Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project: $project" }

if($DotNetPath){$dotnet=[IO.Path]::GetFullPath($DotNetPath);if(!(Test-Path -LiteralPath $dotnet -PathType Leaf)){throw "dotnet executable is missing: $dotnet"}}
else {$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
& $dotnet build $project --configuration Release `
    "-p:GameRoot=$GameRoot" `
    "-p:OutputPath=$OutputDirectory\" `
    "-p:BaseIntermediateOutputPath=$BuildDirectory\"
if ($LASTEXITCODE -ne 0) { throw 'SongPackageIO compilation failed.' }

$output = Join-Path $OutputDirectory 'SongPackageIO.dll'
if (!(Test-Path -LiteralPath $output -PathType Leaf)) { throw "Build did not produce: $output" }
Write-Output $output
