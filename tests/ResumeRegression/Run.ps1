$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\src\GeZi\MainWindow.xaml.cs') -Raw -Encoding UTF8

function Get-Method([string] $start, [string] $end) {
    $from = $source.IndexOf($start, [StringComparison]::Ordinal)
    $to = $source.IndexOf($end, $from, [StringComparison]::Ordinal)
    if ($from -lt 0 -or $to -lt 0) { throw "Cannot locate production method: $start" }
    $method = $source.Substring($from, $to - $from)
    return $method.Substring(0, $method.LastIndexOf('}') + 1).Trim()
}

# Compile the unmodified production orchestration with deterministic API/UI edges.
# Never construct MainWindow/App: their startup touches real configuration and UI.
$resume = Get-Method 'private async Task ResumePendingBatchAsync(' 'private async Task<string> RefreshLinkAsyncForPending('
$busy = Get-Method 'private void SetBusy(bool busy)' 'private readonly HashSet<Button> _busyButtons'
$generated = @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeZi;
using GeZi.Core.Download;
namespace ResumeRegression
{
    internal partial class ResumeHarness
    {
        $resume
        $busy
    }
}
"@
$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Path $obj -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $obj 'ResumeMethods.g.cs'), $generated)
dotnet build (Join-Path $PSScriptRoot 'ResumeRegression.csproj') -c Release --nologo -v:q
if ($LASTEXITCODE -ne 0) { throw 'Regression build failed' }
& (Join-Path $PSScriptRoot 'bin\Release\net48\ResumeRegression.exe') @args
if ($LASTEXITCODE -ne 0) { throw 'Resume regression failed' }
