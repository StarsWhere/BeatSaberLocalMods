[CmdletBinding()]
param(
    [string]$GameRoot = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory=$true)][string]$BuildDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GameRoot = [IO.Path]::GetFullPath($GameRoot)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$source = Join-Path $PSScriptRoot 'advanced_song_filters'
$compiler = 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$json = Join-Path $GameRoot 'Libs/Newtonsoft.Json.dll'
$output = Join-Path $BuildDirectory 'CoreTests.exe'
$arguments = @('/nologo','/target:exe','/codepage:65001',('/out:'+$output),('/r:'+$json),
    (Join-Path $source 'Core.cs'),(Join-Path $source 'Editing.cs'),(Join-Path $source 'CoreTests.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Core tests compilation failed' }
Copy-Item -LiteralPath $json -Destination (Join-Path $BuildDirectory 'Newtonsoft.Json.dll')
& $output
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
$output = Join-Path $BuildDirectory 'EditingTests.exe'
$arguments = @('/nologo','/target:exe','/codepage:65001',('/out:'+$output),('/r:'+$json),
    (Join-Path $source 'Core.cs'),(Join-Path $source 'Editing.cs'),(Join-Path $source 'EditingTests.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Editing tests compilation failed' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Editing tests failed' }
$output = Join-Path $BuildDirectory 'IntegrationTests.exe'
$arguments = @('/nologo','/target:exe','/codepage:65001',('/out:'+$output),('/r:'+$json),
    (Join-Path $source 'Core.cs'),(Join-Path $source 'Editing.cs'),(Join-Path $source 'Persistence.cs'),(Join-Path $source 'IntegrationTests.cs'))
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Persistence tests compilation failed' }
& $output $GameRoot (Join-Path $BuildDirectory 'integration-data')
if ($LASTEXITCODE -ne 0) { throw 'Persistence tests failed' }
$output = Join-Path $BuildDirectory 'OwnedParserTests.exe'
& $compiler /nologo /target:exe /codepage:65001 ('/out:'+$output) (Join-Path $source 'OwnedParser.cs') (Join-Path $source 'OwnedParserTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Parser lifecycle tests compilation failed' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Parser lifecycle tests failed' }
