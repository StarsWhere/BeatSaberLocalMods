[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$BuildDirectory, [string]$PythonPath='python')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$BuildDirectory=[IO.Path]::GetFullPath($BuildDirectory)
[IO.Directory]::CreateDirectory($BuildDirectory)|Out-Null
$source=Join-Path $PSScriptRoot 'mod_ui_chinese'
& $PythonPath (Join-Path $source 'generate_catalog.py') --localization-dir (Join-Path (Split-Path -Parent $PSScriptRoot) 'localization') --output-dir $BuildDirectory
if($LASTEXITCODE -ne 0){throw 'Translation catalog validation failed'}
$output=Join-Path $BuildDirectory 'TranslationEngineTests.exe'
$argsList=@('/nologo','/target:exe','/codepage:65001',('/out:'+$output),(Join-Path $source 'TranslationEngine.cs'),(Join-Path $source 'TranslationEngineTests.cs'),(Join-Path $BuildDirectory 'GeneratedCatalog.cs'))
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' @argsList
if($LASTEXITCODE -ne 0){throw 'Translation checks compilation failed'}
& $output
if($LASTEXITCODE -ne 0){throw 'Translation boundary checks failed'}
& $PythonPath (Join-Path $source 'UiAuditTests.py') --build-directory $BuildDirectory
if($LASTEXITCODE -ne 0){throw 'UI audit boundary checks failed'}
