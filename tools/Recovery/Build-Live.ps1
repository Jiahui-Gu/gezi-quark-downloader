param([Parameter(Mandatory=$true)][string]$CoreDll)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\Build.ps1" -Test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$out = Join-Path $PSScriptRoot 'bin'
Copy-Item -LiteralPath $CoreDll -Destination (Join-Path $out 'GeZi.Core.dll')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /platform:x64 /target:exe /optimize+ "/out:$out\GeZi.LiveGaps.exe" "/reference:$out\GeZi.Core.dll" "/reference:$out\GeZi.Recovery.exe" /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\LiveGaps.cs"
exit $LASTEXITCODE
