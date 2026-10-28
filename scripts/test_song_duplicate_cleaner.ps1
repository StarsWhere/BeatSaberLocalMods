[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$testRoot = Join-Path $GameRoot '.tmp/song-duplicate-cleaner/tests'
$buildRoot = Join-Path $testRoot 'build'
[IO.Directory]::CreateDirectory($buildRoot) | Out-Null
$output = Join-Path $buildRoot 'SongDuplicateCleaner.CoreTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$arguments = @(
    '/nologo', '/target:exe', '/codepage:65001', '/langversion:5', ('/out:' + $output),
    (Join-Path $PSScriptRoot 'song_duplicate_cleaner/Core.cs'),
    (Join-Path $PSScriptRoot 'song_duplicate_cleaner/Tests/CoreTests.cs')
)
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'SongDuplicateCleaner core test compilation failed.' }
& $output $testRoot
if ($LASTEXITCODE -ne 0) { throw 'SongDuplicateCleaner core regression tests failed.' }
