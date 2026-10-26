# Candidate-only recovery (Windows / .NET Framework 4.8 x64)

Build and run the dependency-free synthetic suite:

```powershell
& .\tools\Recovery\Build.ps1 -Test
```

Executable: `tools\Recovery\bin\GeZi.Recovery.exe`. The build script uses the
installed Windows Framework compiler; the optional SDK project targets net48/x64.
No NuGet packages or application binaries are required.

## Explicit inputs

```powershell
$tool = '.\tools\Recovery\bin\GeZi.Recovery.exe'
$inputs = @(
  '--dest', 'D:\example\original.bin',
  '--meta', 'D:\example-cache\item\meta.json',
  '--parts', 'D:\example-cache\item\parts',
  '--outputDir', 'D:\recovery\new-attempt-001',
  '--expected-md5', '0123456789ABCDEF0123456789ABCDEF'
)
& $tool --plan @inputs
# Each patch is exactly the bytes in its inclusive, zero-based logical range.
& $tool --recover @inputs --patch '100:199:D:\patches\gap.bin'
# Or fetch only remaining logical gaps from an explicitly supplied fresh link:
& $tool --recover @inputs --link-file 'D:\private\fresh-link.txt' `
  --cookie-file 'D:\private\cookie.txt'
```

`--patch START:END:PATH` is repeatable. Windows drive colons in PATH are supported.
All five named inputs are required, including for `--plan`. Choose exactly one
of `--plan` and `--recover`. Source destination and metadata must already exist.
Link and cookie files contain only their UTF-8 values, not JSON or XML. They are
read-only inputs, never copied to output or printed. Protect them with local
filesystem ACLs. The cookie file is optional and contains a plaintext Cookie
header value, not a DPAPI value. No credential auto-discovery is performed.

## Safety and interpretation

* Pause the downloader before recovery. All present source files are opened
  read-only with write/delete sharing denied and held until the run ends.
* Metadata is text `total`, `count`, then exactly `count` rows of `S,E,W`.
  The original row index always identifies `pNNNNNN`. Only a sorted copy is
  checked for a contiguous, non-overlapping partition of the total.
* Coverage is the destination's presumed logical prefix plus each part's
  `min(E-S+1, W, physical length)`, plus explicitly mapped patches. Physical
  lengths are never summed to infer completeness.
* Every pair of overlapping logical spans is byte-compared before candidate
  creation. Inconsistent overlaps fail, even if another source could cover them.
* `--plan` does not write or make HTTP requests. It validates/compares local
  sources and reports inclusive gaps. It does not read link/cookie contents.
* Without a link file, unresolved gaps fail before an output directory is
  created. No padding, truncation or synthesized bytes are used.
* The output directory must **not exist**, its parent must exist, and it must
  be outside the entire parent directory of `--parts`. Existing paths,
  source-containing output ancestors, UNC/device paths, alternate streams,
  and reparse-point paths are rejected. A native exclusive directory creation
  prevents accidental reuse. Do not allow another process to mutate output
  ancestors while recovery runs; this is not an adversarial filesystem sandbox.
* Source destination, metadata, parts and patches are never overwritten,
  renamed or deleted. The tool never marks the downloader's original repaired.
* Candidate assembly uses `candidate.failed`; only a complete file matching the
  mandatory expected MD5 is renamed to `candidate.bin`. Exit code **0** means a
  successful plan or a verified candidate; **1** means failure. On a failed run,
  retain the uniquely named output for inspection; do not use it as repaired.
  Range files may also remain there. A retry needs another new output directory.

## Explicit-link HTTP contract and refresh integration

Only `--recover` with missing gaps makes requests. Requests use Core's `DlUa`,
Referer and Origin, optionally the explicitly supplied Cookie. System proxies,
redirects and decompression are disabled. HTTPS is required except loopback HTTP
for tests. Cookies are sent only to the supplied URL; do not supply untrusted
URLs with a cookie file. Each request is an inclusive Range for exactly one gap.
Responses must be 206 with the exact `Content-Range: bytes START-END/TOTAL`,
no Content-Encoding and exactly the expected number of body bytes.
200/full-file responses, redirects, short/extra bodies and wrong totals fail.
Errors print fixed codes or exception types, never URLs, credentials or server
response bodies. There is no automatic retry or stale-link refresh.

An authorized coordinator should obtain a fresh share download link separately:
read only explicit config/state paths (never SettingsStore), decode the stored
Cookie with `SecretProtector.Unprotect`, get a fresh stoken using the exact
share ID, enumerate the explicit original share folder, match the **exact
original Fid**, and pass its fresh Token to `QuarkClient.GetShareDownloadAsync`.
Do not select by filename or infer a folder from an identity. Keep the signed
URL private and supply it using `--link-file`; no `--url` argument is accepted.
This helper does not implement or validate that API flow or read application
config/state itself.

Tests use generated small fixtures under `tools\Recovery\bin` and loopback TCP
only, then remove their own fixtures. They never inspect real downloads or
credentials. Large-file throughput, live CDN authorization, and share refresh
are deliberately not exercised.
