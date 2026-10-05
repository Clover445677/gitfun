#Requires -Version 5.1
param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework compiler was not found.' }
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'dist' }
$null = New-Item -ItemType Directory -Path $output -Force
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + (Join-Path $output 'ChatGPTBridge.exe')),'/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:Microsoft.CSharp.dll') + $sources
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Write-Output (Join-Path $output 'ChatGPTBridge.exe')
