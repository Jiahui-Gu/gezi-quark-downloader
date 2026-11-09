using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;
using GeZi.Core.Support;

internal static class RequestLifecycleRegression
{
    private const int PartSize = 64 * 1024;
    private const int Parts = 512;
    private const int Workers = 64;

    internal static async Task<int> RunAsync(bool budgetOnly = false, bool progressOnly = false,
        bool tailWaitOnly = false, bool tinyTailOnly = false, bool retryPolicyOnly = false)
    {
        NetworkConfig.Apply(512);
        using (var defaults = new HttpClient())
            Console.WriteLine("Framework HttpClient default timeout seconds=" + defaults.Timeout.TotalSeconds);
        int failures = 0;
        foreach (var test in new Func<Task>[] { Reuse, CloseAndReplace, CancelPendingHeaders,
            RedactedFailureChain, RequeueBudgetBoundaries, DistinctTerminalErrors, BoundedSendFailures,
            AlreadyCommitted, FatalAfterIdle, CancelBackoffAfterIdle, SharedBudgetExhaustion,
            ProgressAcrossHistoricalFailures, HealthyTailHeaders, HealthyTinyTailHeaders,
            TinyTailRetryBackoff, TinyTailBodyStall, OwnPartProgress, LastBadPart, SubQuantumTrickle }
            .Where(test => (!budgetOnly || test.Method.Name == nameof(SharedBudgetExhaustion))
                && (!progressOnly || test.Method.Name == nameof(ProgressAcrossHistoricalFailures))
                && (!tailWaitOnly || test.Method.Name == nameof(HealthyTailHeaders)
                    || test.Method.Name == nameof(HealthyTinyTailHeaders)
                    || test.Method.Name == nameof(TinyTailRetryBackoff)
                    || test.Method.Name == nameof(TinyTailBodyStall))
                && (!tinyTailOnly || test.Method.Name == nameof(HealthyTinyTailHeaders))
                && (!retryPolicyOnly || test.Method.Name == nameof(OwnPartProgress)
                    || test.Method.Name == nameof(LastBadPart)
                    || test.Method.Name == nameof(SubQuantumTrickle))))
        {
            try
            {
                await test().ConfigureAwait(false);
                Console.WriteLine("PASS request lifecycle " + test.Method.Name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL request lifecycle " + test.Method.Name + ": " + ex);
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static Task Reuse() => DownloadAsync(true);
    private static Task CloseAndReplace() => DownloadAsync(false);

    private static Task RedactedFailureChain()
    {
        var method = typeof(SegmentedDownloader).GetMethod("DescribeFailure",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(method != null, "transport failures must preserve a redacted inner-exception chain");
        var error = new HttpRequestException("https://private.invalid/?token=secret Cookie=private",
            new WebException("Authorization=private", new SocketException((int)SocketError.ConnectionReset),
                WebExceptionStatus.ReceiveFailure, null));
        string text = (string)method.Invoke(null, new object[] { error });
        Check(text.Contains("HttpRequestException[HResult=0x")
            && text.Contains("WebException[HResult=0x") && text.Contains("Status=ReceiveFailure")
            && text.Contains("SocketException[HResult=0x") && text.Contains("SocketError=ConnectionReset")
            && text.Contains("NativeError=10054"), "structured deepest transport cause must survive: " + text);
        Check(!text.Contains("private") && !text.Contains("secret") && !text.Contains("token")
            && !text.Contains("Cookie") && !text.Contains("https:"),
            "exception messages/URLs/credentials must not enter safe diagnostics");
        Console.WriteLine("REDACTED " + text);
        return Task.CompletedTask;
    }

    private static Task RequeueBudgetBoundaries()
    {
        var type = typeof(SegmentedDownloader).GetNestedType("ProgressRetryBudget", BindingFlags.NonPublic);
        Check(type != null, "shared requeue budget must have atomic progress-aware reservation");
        object budget = Activator.CreateInstance(type, new object[] { 500, 0L });
        var method = type.GetMethod("TryReserve");
        int accepted = 0;
        Parallel.For(0, 100000, _ =>
        {
            if ((bool)method.Invoke(budget, new object[] { 0L })) Interlocked.Increment(ref accepted);
        });
        int used = (int)type.GetProperty("Used").GetValue(budget);
        Check(accepted == 500 && used == 500
            && !(bool)method.Invoke(budget, new object[] { 0L }),
            "concurrent reservations must stop at 500, never overspend or lose increments");
        Console.WriteLine("BUDGET reservations=100000 accepted=" + accepted + " used=" + used + " limit=500");
        return Task.CompletedTask;
    }

    private static Task DistinctTerminalErrors()
    {
        var method = typeof(SegmentedDownloader).GetMethod("SummarizeErrors",
            BindingFlags.Static | BindingFlags.NonPublic);
        Check(method != null, "duplicate worker errors must not conceal different terminal causes");
        string text = (string)method.Invoke(null, new object[] {
            new[] { "same", "same", "same", "different", "third", "fourth" } });
        Check(text == "same; different; third", "deduplicate before taking three causes: " + text);
        return Task.CompletedTask;
    }

    private static async Task BoundedSendFailures()
    {
        using (var fixture = new Fixture(2))
        using (var server = new Server(false, false, 2, reset: true))
        using (var downloader = new SegmentedDownloader(2))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log += logs.Enqueue;
            var trace = RequestStageTrace.Attach(downloader);
            var clock = Stopwatch.StartNew();
            Task<DownloadResult> download = downloader.DownloadAsync(server.Url, fixture.Dest);
            Check(await Task.WhenAny(download, Task.Delay(90000)).ConfigureAwait(false) == download,
                "permanent send failures must stop within existing finite attempts");
            var result = await download.ConfigureAwait(false);
            Check(!result.Ok && result.Message.Contains("stage=RequestHeaders")
                && result.Message.Contains("HttpRequestException[HResult=")
                && (result.Message.Contains("SocketError=") || result.Message.Contains("Status=")),
                "real reset must preserve deepest safe cause and request stage: " + result.Message);
            var causes = result.Message.Split(new[] { "; stage=" }, StringSplitOptions.None)
                .Select((cause, i) => i == 0 ? cause : "stage=" + cause).ToArray();
            Check(causes.Length <= 2 && causes.Distinct().Count() == causes.Length,
                "deduplicate identical worker failures but preserve different transport causes: " + result.Message);
            Check(trace.RequestsStarted == 40, "two parts, four rounds, five downloader attempts");
            Check(server.Requests >= 40 && server.Requests <= 80,
                "framework transport can transparently retry a closed connection, not spend another round");
            Check(logs.Count(l => l.Contains("整轮重试")) == 6, "only three requeues per failed part");
            Check(logs.Any(l => l.Contains("round=4/4") && l.Contains("requeues=6/500")),
                "terminal context must identify attempts/round/shared counter");
            Check(File.Exists(fixture.Dest + ".qmeta") && Directory.Exists(fixture.Dest + ".qparts"),
                "transport failure must retain resume artifacts");
            Check(!result.Message.Contains(server.Url), "failure output must not leak request URL");
            Console.WriteLine("SEND-FAILURE seconds=" + clock.Elapsed.TotalSeconds.ToString("F3",
                CultureInfo.InvariantCulture) + " sends=" + trace.RequestsStarted + " wireRequests="
                + server.Requests + " requeues=6/500 preserved=True");
            Console.WriteLine("SEND-CAUSE " + result.Message);
        }
    }

    private static async Task DownloadAsync(bool keepAlive)
    {
        using (var fixture = new Fixture())
        using (var server = new Server(keepAlive, false))
        using (var downloader = new SegmentedDownloader(Workers))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log += logs.Enqueue;
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: deadline.Token)
                .ConfigureAwait(false);
            Check(result.Ok, result.Message);
            using (var stream = File.OpenRead(fixture.Dest))
            {
                Check(stream.Length == (long)Parts * PartSize, "complete output length");
                var buffer = new byte[PartSize];
                long offset = 0;
                int n;
                while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    for (int i = 0; i < n; i++)
                        Check(buffer[i] == Value(offset + i), "byte mismatch at " + (offset + i));
                    offset += n;
                }
            }
            Check(server.Requests == Parts, "every missing part must be requested once");
            Check(logs.Any(l => l.StartsWith("分片规划:") && l.Contains("切为 " + Parts + " 片")
                && l.Contains("平均")), "plan log must describe the actual resumed plan, not a recalculated first part");
            string report = logs.Single(l => l.StartsWith("本次实测:"));
            string freshBytes = Format((long)Parts * (PartSize - 1));
            Check(report.Contains("本次新增 " + freshBytes), "resumed throughput must not count bytes already cached");
            Check(logs.Any(l => l.Contains("不是实测单连接速度")),
                "dividing by configured workers must not be reported as measured per-connection speed or CDN throttling");
            Check(server.Errors.IsEmpty, string.Join("; ", server.Errors));
            if (keepAlive)
                Check(server.Accepted <= Workers + 1, "completed responses must allow real TCP connection reuse");
            else
                Check(server.Accepted == Parts + 1, "closed connections must be replaced for all remaining parts");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "LIFECYCLE keepAlive={0} seconds={1:F3} requests={2} tcpAccepted={3} workers={4} identical=True",
                keepAlive, clock.Elapsed.TotalSeconds, server.Requests, server.Accepted, Workers));
        }
    }

    private static async Task AlreadyCommitted()
    {
        using (var fixture = new Fixture(8, complete: true))
        using (var server = new Server(false, false, 8))
        using (var downloader = new SegmentedDownloader(8))
        using (var deadline = new CancellationTokenSource(10000))
        {
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: deadline.Token).ConfigureAwait(false);
            Check(result.Ok && server.Requests == 0, "all committed cache must merge without requesting body work");
            byte[] data = File.ReadAllBytes(fixture.Dest);
            Check(data.Length == 8 * PartSize && data.Where((value, i) => value != Value(i)).Count() == 0,
                "all-cache completion must preserve every byte");
        }
    }

    private static async Task FatalAfterIdle()
    {
        using (var fixture = new Fixture(8))
        using (var server = new Server(false, false, 8, status: 404, firstOnly: true, statusDelay: 2000))
        using (var downloader = new SegmentedDownloader(8))
        using (var deadline = new CancellationTokenSource(15000))
        {
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: deadline.Token).ConfigureAwait(false);
            Check(!deadline.IsCancellationRequested && !result.Ok && result.Message.Contains("HTTP 404"),
                "one final fatal part must wake idle workers and return its error, not wait forever or claim success");
            Check(server.Requests == 8 && File.Exists(fixture.Dest + ".qmeta"),
                "non-retryable response must not requeue or delete resume state");
        }
    }

    private static async Task CancelBackoffAfterIdle()
    {
        using (var fixture = new Fixture(8))
        using (var server = new Server(false, false, 8, status: 429, firstOnly: true))
        using (var downloader = new SegmentedDownloader(8))
        using (var cancel = new CancellationTokenSource())
        {
            Task<DownloadResult> task = downloader.DownloadAsync(server.Url, fixture.Dest, cancel: cancel.Token);
            Check(await Task.WhenAny(server.FirstFailure, Task.Delay(10000)).ConfigureAwait(false) == server.FirstFailure,
                "controlled 429 must enter ordinary retry/backoff");
            await Task.Delay(200).ConfigureAwait(false);
            cancel.Cancel();
            Check(await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false) == task, "backoff and signal waits must both cancel");
            var result = await task.ConfigureAwait(false);
            Check(!result.Ok && result.Message == "已取消", "429 backoff cancellation must remain explicit user cancellation");
        }
    }

    private static async Task SharedBudgetExhaustion()
    {
        using (var fixture = new Fixture())
        using (var server = new Server(true, false, status: 429))
        using (var downloader = new SegmentedDownloader(512))
        using (var deadline = new CancellationTokenSource(90000))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log = logs.Enqueue;
            var trace = RequestStageTrace.Attach(downloader);
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest, cancel: deadline.Token).ConfigureAwait(false);
            Check(!deadline.IsCancellationRequested && !result.Ok && result.Message.Contains("500 次"),
                "real shared-budget exhaustion must terminate with the original error");
            Check(logs.Count(l => l.Contains("整轮重试 ")) == 500, "exactly 500 concurrent whole-round reservations, never 501");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "SHARED-OBSERVED applicationSends={0} wireRequests={1} terminal={2} serverErrors={3}",
                trace.RequestsStarted, server.Requests, result.Message, string.Join("; ", server.Errors)));
            Check(trace.RequestsStarted == (512 + 500) * 5,
                "existing per-round application attempts and shared requeues must remain bounded");
            Check(server.Requests > 0 && server.Errors.IsEmpty,
                "real loopback responses must be observed; framework wire retries are not application attempts");
            Check(File.Exists(fixture.Dest + ".qmeta"), "budget failure must retain original progress");
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "SHARED-BUDGET seconds={0:F3} workers=512 requeues=500/500 applicationSends={1} wireRequests={2} preserved=True",
                clock.Elapsed.TotalSeconds, trace.RequestsStarted, server.Requests));
        }
    }

    private static async Task ProgressAcrossHistoricalFailures()
    {
        const int count = 1040;
        using (var fixture = new Fixture(count))
        using (var server = new Server(true, false, count, progressFailures: true))
        using (var downloader = new SegmentedDownloader(512))
        using (var deadline = new CancellationTokenSource(120000))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log = logs.Enqueue;
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Console.WriteLine("PROGRESS-RECOVERY seconds=" + clock.Elapsed.TotalSeconds.ToString("F3",
                CultureInfo.InvariantCulture) + " ok=" + result.Ok + " requests=" + server.Requests
                + " requeueLines=" + logs.Count(l => l.Contains("整轮重试 "))
                + " error=" + result.Message);
            Check(!deadline.IsCancellationRequested && result.Ok,
                "ongoing real committed progress must not fail solely because 520 historical requeues exceed 500: "
                + result.Message);
            Check(logs.Count(l => l.Contains("整轮重试 ")) == 520,
                "all 520 finite transient rounds must be observed, not hidden or silently skipped");
            Check(server.Requests == count + 520 * 5, "no extra network requests beyond five failures and recovery");
            byte[] data = File.ReadAllBytes(fixture.Dest);
            Check(data.Length == count * PartSize
                && data.Where((value, i) => value != Value(i)).Count() == 0,
                "recovery must preserve each cached byte and all downloaded bytes");
            Check(server.Errors.IsEmpty, string.Join("; ", server.Errors));
        }
    }

    private static async Task OwnPartProgress()
    {
        const int size = 4 * 1024 * 1024;
        using (var fixture = new Fixture(1, pieceSize: size))
        using (var server = new Server(false, false, 1, pieceSize: size, partialFailures: 25))
        using (var downloader = new SegmentedDownloader(1))
        using (var deadline = new CancellationTokenSource(120000))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log = logs.Enqueue;
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Console.WriteLine("OWN-PROGRESS seconds=" + clock.Elapsed.TotalSeconds.ToString("F3",
                CultureInfo.InvariantCulture) + " ok=" + result.Ok + " requests=" + server.Requests
                + " requeues=" + logs.Count(l => l.Contains("整轮重试 ")) + " error=" + result.Message);
            Check(!deadline.IsCancellationRequested && result.Ok && server.Requests == 26,
                "own committed progress must recover beyond the historical 20-attempt cap");
            byte[] data = File.ReadAllBytes(fixture.Dest);
            Check(data.Length == size && data.Where((value, i) => value != Value(i)).Count() == 0,
                "partial successful writes and exact resumed Range must produce identical output");
        }
    }

    private static async Task SubQuantumTrickle()
    {
        using (var fixture = new Fixture(1, pieceSize: 4 * 1024 * 1024))
        using (var server = new Server(false, false, 1, pieceSize: 4 * 1024 * 1024,
            partialFailures: 25, partialBytes: 1024))
        using (var downloader = new SegmentedDownloader(1))
        using (var deadline = new CancellationTokenSource(90000))
        {
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Check(!deadline.IsCancellationRequested && !result.Ok && server.Requests == 20,
                "20 KiB of partial-write trickle must not refresh either no-progress retry window");
            string[] part = File.ReadAllLines(fixture.Dest + ".qmeta")[2].Split(',');
            Check(long.Parse(part[2], CultureInfo.InvariantCulture) == 1 + 20 * 1024,
                "failure preserves exactly the cached byte plus each newly committed drip");
            Console.WriteLine("TRICKLE requests=20 newBytes=20480 cachedBytes=1 preserved=True");
        }
    }

    private static async Task LastBadPart()
    {
        using (var fixture = new Fixture(64))
        using (var server = new Server(true, false, 64, status: 429, firstOnly: true, bodyDelay: 500))
        using (var downloader = new SegmentedDownloader(2))
        using (var deadline = new CancellationTokenSource(90000))
        {
            var trace = RequestStageTrace.Attach(downloader);
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Console.WriteLine("LAST-BAD seconds=" + clock.Elapsed.TotalSeconds.ToString("F3",
                CultureInfo.InvariantCulture) + " ok=" + result.Ok + " sends=" + trace.RequestsStarted
                + " error=" + result.Message);
            Check(!deadline.IsCancellationRequested && !result.Ok && result.Message.Contains("HTTP 429"),
                "other parts' continuous successful writes must not indefinitely renew the last bad part");
            Check(trace.RequestsStarted == 63 + 20 && File.Exists(fixture.Dest + ".qmeta"),
                "bad part keeps four rounds of five attempts and all recovery metadata");
        }
    }

    private static async Task HealthyTailHeaders()
    {
        using (var fixture = new Fixture(8))
        using (var server = new Server(true, false, 8, firstOnly: true, headerDelay: 80000))
        using (var downloader = new SegmentedDownloader(8))
        using (var deadline = new CancellationTokenSource(110000))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log = logs.Enqueue;
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Console.WriteLine("TAIL-HEADERS seconds=" + clock.Elapsed.TotalSeconds.ToString("F3",
                CultureInfo.InvariantCulture) + " ok=" + result.Ok + " requests=" + server.Requests
                + " error=" + result.Message);
            Check(!deadline.IsCancellationRequested && result.Ok,
                "last healthy 80s response headers must not be killed by the 45s global watchdog");
            Check(server.Requests == 8 && !logs.Any(l => l.Contains("下载停滞:")),
                "bounded header wait must finish without retry/preemption or false global stall");
            byte[] data = File.ReadAllBytes(fixture.Dest);
            Check(data.Length == 8 * PartSize && data.Where((value, i) => value != Value(i)).Count() == 0,
                "healthy tail output must remain byte-identical");
        }
    }

    private static Task HealthyTinyTailHeaders() => TinyTailAsync(80000, false, false);
    private static Task TinyTailRetryBackoff() => TinyTailAsync(0, true, false);
    private static Task TinyTailBodyStall() => TinyTailAsync(0, false, true);

    private static async Task TinyTailAsync(int headers, bool retries, bool bodyStall)
    {
        using (var fixture = new Fixture(8, tinyTail: true))
        using (var server = new Server(true, false, 8, firstOnly: true, headerDelay: headers,
            progressFailures: retries, statusDelay: retries ? 8000 : 0, bodyDelay: bodyStall ? 80000 : 0))
        using (var downloader = new SegmentedDownloader(8))
        using (var deadline = new CancellationTokenSource(115000))
        {
            var logs = new ConcurrentQueue<string>();
            downloader.Log = logs.Enqueue;
            var clock = Stopwatch.StartNew();
            var result = await downloader.DownloadAsync(server.Url, fixture.Dest,
                cancel: deadline.Token).ConfigureAwait(false);
            Console.WriteLine("TINY-TAIL headersMs=" + headers + " retries=" + retries + " bodyStall=" + bodyStall
                + " missing=1019 seconds=" + clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)
                + " ok=" + result.Ok + " requests=" + server.Requests + " error=" + result.Message);
            Check(!deadline.IsCancellationRequested, "tiny-tail test must not need external cancellation");
            if (bodyStall)
            {
                Check(!result.Ok && logs.Any(l => l.Contains("下载停滞:"))
                    && File.Exists(fixture.Dest + ".qmeta"),
                    "genuinely stuck final body still needs bounded termination with preserved cache");
                Check(!logs.Any(l => l.Contains("与你的网络无关") || l.Contains("多半是")),
                    "stall log must not present CDN or local-network attribution as fact");
            }
            else
            {
                Check(result.Ok && server.Requests == (retries ? 6 : 1),
                    "only 1019 missing bytes must recover through bounded headers/backoff without false global stall");
                byte[] data = File.ReadAllBytes(fixture.Dest);
                Check(data.Length == 8 * PartSize && data.Where((value, i) => value != Value(i)).Count() == 0,
                    "all cached logical prefixes and following parts must remain byte-identical");
            }
        }
    }

    private static async Task CancelPendingHeaders()
    {
        using (var fixture = new Fixture())
        using (var server = new Server(false, true))
        using (var downloader = new SegmentedDownloader(Workers))
        using (var cancel = new CancellationTokenSource())
        {
            Task<DownloadResult> download = downloader.DownloadAsync(server.Url, fixture.Dest, cancel: cancel.Token);
            Check(await Task.WhenAny(server.AllWorkersWaiting, Task.Delay(15000)).ConfigureAwait(false)
                == server.AllWorkersWaiting, "all workers must be waiting for headers");
            var clock = Stopwatch.StartNew();
            cancel.Cancel();
            Check(await Task.WhenAny(download, Task.Delay(5000)).ConfigureAwait(false) == download,
                "user cancellation must not wait for the header deadline");
            var result = await download.ConfigureAwait(false);
            Check(!result.Ok && result.Message == "已取消", "user cancellation must not become a timeout/preemption");
            Check(server.Requests == Workers, "cancelled requests must not be retried");
            Check(!File.Exists(fixture.Dest + ".qmeta") && !Directory.Exists(fixture.Dest + ".qparts"),
                "existing explicit user-cancel cleanup semantics must remain");
            Console.WriteLine("LIFECYCLE userCancelMs=" + clock.Elapsed.TotalMilliseconds.ToString("F1",
                CultureInfo.InvariantCulture) + " requests=" + server.Requests);
        }
    }

    private static byte Value(long offset) => (byte)(1 + offset % 251);
    private static string Format(long bytes) => (string)typeof(SegmentedDownloader).GetMethod("Fmt",
        BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { bytes });
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "lifecycle-fixture-" + Guid.NewGuid().ToString("N"));
        internal string Dest => Path.Combine(_root, "synthetic.bin");
        internal Fixture(int parts = Parts, bool complete = false, bool tinyTail = false, int pieceSize = PartSize)
        {
            Directory.CreateDirectory(Dest + ".qparts");
            var lines = new List<string> { ((long)parts * pieceSize).ToString(), parts.ToString() };
            for (int i = 0; i < parts; i++)
            {
                long start = (long)i * pieceSize;
                int written = tinyTail ? (i == 0 ? pieceSize - 1019 : pieceSize) : complete ? pieceSize : 1;
                lines.Add(start + "," + (start + pieceSize - 1) + "," + written);
                File.WriteAllBytes(Path.Combine(Dest + ".qparts", "p" + i.ToString("D6")),
                    Enumerable.Range(0, written).Select(offset => Value(start + offset)).ToArray());
            }
            File.WriteAllLines(Dest + ".qmeta", lines);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, TcpClient> _connections = new ConcurrentDictionary<int, TcpClient>();
        private readonly ConcurrentBag<Task> _tasks = new ConcurrentBag<Task>();
        private readonly TaskCompletionSource<bool> _allWorkers = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _accept;
        private readonly bool _keepAlive, _silent, _reset, _progressFailures;
        private readonly ConcurrentDictionary<long, int> _rangeAttempts = new ConcurrentDictionary<long, int>();
        private readonly int _parts, _pieceSize, _partialFailures, _partialBytes;
        private readonly int _status, _statusDelay, _headerDelay, _bodyDelay;
        private readonly bool _firstOnly;
        private readonly TaskCompletionSource<bool> _firstFailure = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accepted, _requests;
        internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
        internal string Url { get; }
        internal int Accepted => Volatile.Read(ref _accepted);
        internal int Requests => Volatile.Read(ref _requests);
        internal Task AllWorkersWaiting => _allWorkers.Task;
        internal Task FirstFailure => _firstFailure.Task;
        internal Server(bool keepAlive, bool silent, int parts = Parts, bool reset = false,
            int status = 206, bool firstOnly = false, int statusDelay = 0, bool progressFailures = false,
            int headerDelay = 0, int bodyDelay = 0, int pieceSize = PartSize, int partialFailures = 0,
            int partialBytes = 128 * 1024)
        {
            _keepAlive = keepAlive;
            _silent = silent;
            _parts = parts;
            _pieceSize = pieceSize;
            _partialFailures = partialFailures;
            _partialBytes = partialBytes;
            _reset = reset;
            _status = status;
            _firstOnly = firstOnly;
            _statusDelay = statusDelay;
            _progressFailures = progressFailures;
            _headerDelay = headerDelay;
            _bodyDelay = bodyDelay;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/synthetic";
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        int id = Interlocked.Increment(ref _accepted);
                        _connections[id] = client;
                        _tasks.Add(Serve(client, id));
                    }
                }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }
        private async Task Serve(TcpClient client, int id)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    bool first = true;
                    while (!_stop.IsCancellationRequested)
                    {
                        string request = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (request == null) return;
                        long start = 0, end = (long)_parts * _pieceSize - 1;
                        string line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                        {
                            if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                            string[] range = line.Substring("Range: bytes=".Length).Split('-');
                            start = long.Parse(range[0], CultureInfo.InvariantCulture);
                            end = long.Parse(range[1], CultureInfo.InvariantCulture);
                        }
                        bool probe = start == 0 && end == 0;
                        if (!probe && Interlocked.Increment(ref _requests) == Workers)
                            _allWorkers.TrySetResult(true);
                        if (_reset && !probe)
                        {
                            client.Client.LingerState = new LingerOption(true, 0);
                            return;
                        }
                        if (_silent && !probe)
                        {
                            await reader.ReadLineAsync().ConfigureAwait(false);
                            return;
                        }
                        if (first) await Task.Delay(100, _stop.Token).ConfigureAwait(false);
                        first = false;
                        if (!probe && _headerDelay > 0 && (!_firstOnly || start < _pieceSize))
                            await Task.Delay(_headerDelay, _stop.Token).ConfigureAwait(false);
                        bool transient = !probe && _progressFailures && (start / _pieceSize) % 2 == 0
                            && _rangeAttempts.AddOrUpdate(start / _pieceSize, 1, (_, n) => n + 1) <= 5;
                        if (transient || (!probe && _status != 206 && (!_firstOnly || start < _pieceSize)))
                        {
                            if (_statusDelay > 0) await Task.Delay(_statusDelay, _stop.Token).ConfigureAwait(false);
                            byte[] error = Encoding.ASCII.GetBytes("HTTP/1.1 " + (transient ? 503 : _status)
                                + " Synthetic\r\nContent-Length: 0\r\nConnection: "
                                + (_keepAlive ? "keep-alive" : "close") + "\r\n\r\n");
                            await stream.WriteAsync(error, 0, error.Length, _stop.Token).ConfigureAwait(false);
                            _firstFailure.TrySetResult(true);
                            if (!_keepAlive) return;
                            continue;
                        }
                        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\n"
                            + "Content-Length: " + (end - start + 1) + "\r\nContent-Range: bytes "
                            + start + "-" + end + "/" + ((long)_parts * _pieceSize)
                            + "\r\nAccept-Ranges: bytes\r\nConnection: " + (_keepAlive ? "keep-alive" : "close") + "\r\n\r\n");
                        await stream.WriteAsync(header, 0, header.Length, _stop.Token).ConfigureAwait(false);
                        if (!probe && _bodyDelay > 0) await Task.Delay(_bodyDelay, _stop.Token).ConfigureAwait(false);
                        bool partial = !probe && _partialFailures > 0
                            && _rangeAttempts.AddOrUpdate(start / _pieceSize, 1, (_, n) => n + 1) <= _partialFailures;
                        byte[] data = new byte[(int)Math.Min(end - start + 1, partial ? _partialBytes : int.MaxValue)];
                        for (int i = 0; i < data.Length; i++) data[i] = Value(start + i);
                        await stream.WriteAsync(data, 0, data.Length, _stop.Token).ConfigureAwait(false);
                        if (partial) return;
                        if (!_keepAlive) return;
                    }
                }
            }
            catch (IOException) { /* A completed probe or cancelled synthetic request can close the peer. */ }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { Errors.Enqueue(ex.ToString()); }
            finally { _connections.TryRemove(id, out _); }
        }
        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            foreach (var client in _connections.Values) client.Dispose();
            Task.WhenAll(_tasks.ToArray()).GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
