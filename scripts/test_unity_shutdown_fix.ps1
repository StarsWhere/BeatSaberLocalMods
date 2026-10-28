[CmdletBinding()]
param([string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tmp/v4-load-exit/native-tests'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
[IO.Directory]::CreateDirectory($BuildDirectory) | Out-Null
$source = Join-Path $PSScriptRoot 'unity_shutdown_fix'
$output = Join-Path $BuildDirectory 'UnityShutdownFix.Tests.exe'
& 'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe' /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /warn:4 /langversion:5 ('/out:'+$output) (Join-Path $source 'GuardCode.cs') (Join-Path $source 'NativeMemory.cs') (Join-Path $source 'Tests/GuardTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native guard regression compilation failed.' }
& $output
if ($LASTEXITCODE -ne 0) { throw 'Native guard regression failed.' }
