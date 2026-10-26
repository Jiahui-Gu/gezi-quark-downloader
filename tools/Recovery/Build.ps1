param([switch]$Test)
$ErrorActionPreference = 'Stop'
$base = $PSScriptRoot
$out = Join-Path $base 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /platform:x64 /target:exe /optimize+ "/out:$out\GeZi.Recovery.exe" "$base\Recovery.cs" "$base\..\..\src\GeZi.Core\Support\QuarkConstants.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Test) {
    & $compiler /nologo /platform:x64 /target:exe "/reference:$out\GeZi.Recovery.exe" "/out:$out\GeZi.Recovery.Tests.exe" "$base\Tests\Program.cs"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & "$out\GeZi.Recovery.Tests.exe"
    exit $LASTEXITCODE
}
