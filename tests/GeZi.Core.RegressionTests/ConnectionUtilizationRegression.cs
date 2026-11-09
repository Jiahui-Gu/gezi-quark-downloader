using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;
using GeZi.Core.Support;

internal static class ConnectionUtilizationRegression
{
    private const int PartSize = 768 * 1024;
    private const int BlockSize = 32 * 1024;

    internal static async Task<int> RunAsync(string[] args)
    {
        NetworkConfig.Apply(512);
        int workers = args.Contains("--512") ? 512 : 32;
        var modes = args.Contains("--header-waits") ? new[] { "mixed-headers" }
            : args.Contains("--headers-only") ? new[] { "headers" }
            : new[] { "headers", "paced", "slow-body", "429", "429-long" };
        int failures = 0;
        foreach (string mode in modes)
        {
            try
            {
                await MeasureAsync(mode, workers, args.Contains("--observe-only")).ConfigureAwait(false);
                Console.WriteLine("PASS utilization " + mode);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL utilization " + mode + ": " + ex);
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static async Task MeasureAsync(string mode, int workers, bool observeOnly)
    {
        int partSize = mode == "mixed-headers" ? 256 * 1024 : PartSize;
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "utilization-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string dest = Path.Combine(root, "synthetic.bin");
        Directory.CreateDirectory(dest + ".qparts");
        var meta = new List<string> { ((long)workers * partSize).ToString(), workers.ToString() };
        for (int i = 0; i < workers; i++)
        {
            long start = (long)i * partSize;
            meta.Add(start + "," + ((long)(i + 1) * partSize - 1) + ",1");
            File.WriteAllBytes(Path.Combine(dest + ".qparts", "p" + i.ToString("D6")),
                new[] { Value(start) });
        }
        File.WriteAllLines(dest + ".qmeta", meta);
        bool passed = false;
        try
        {
            using (var server = new Server(mode, workers, partSize))
            using (var downloader = new SegmentedDownloader(workers))
            using (var trace = mode == "mixed-headers" ? RequestStageTrace.Attach(downloader) : null)
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "mixed-headers" ? 280 : 110)))
            {
                var logs = new ConcurrentQueue<string>();
                var snapshots = new ConcurrentQueue<DiagnosticsSnapshot>();
                downloader.Log = logs.Enqueue;
                downloader.Diagnostics = snapshots.Enqueue;
                var clock = Stopwatch.StartNew();
                var download = downloader.DownloadAsync(server.Url, dest, cancel: timeout.Token);
                int requestsBeforeRelease = 0, pendingBeforeRelease = 0, bodiesBeforeRelease = 0;
                long windowBytes = 0;
                double windowSeconds = 0;
                if (mode == "mixed-headers")
                {
                    var ready = await Task.WhenAny(server.FirstWaveReady, download,
                        Task.Delay(15000)).ConfigureAwait(false);
                    Check(ready == server.FirstWaveReady, "all configured workers must send their initial request");
                    await Task.Delay(TimeSpan.FromSeconds(110)).ConfigureAwait(false);
                    var before = snapshots.LastOrDefault();
                    await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                    var after = snapshots.LastOrDefault();
                    requestsBeforeRelease = server.Requests;
                    pendingBeforeRelease = server.PendingHeaders;
                    bodiesBeforeRelease = after?.ActiveConnections ?? 0;
                    windowBytes = (after?.DoneBytes ?? 0) - (before?.DoneBytes ?? 0);
                    windowSeconds = before != null && after != null
                        ? (after.Timestamp - before.Timestamp).TotalSeconds : 0;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "HEADER-WAIT workers={0} requestsAt125s={1} pendingHeaders={2} bodyActive={3} "
                        + "windowBytes={4} windowSeconds={5:F3} windowKiBps={6:F3} completedBeforeRelease={7} "
                        + "openTcp={8} peakTcp={9}",
                        workers, requestsBeforeRelease, pendingBeforeRelease, bodiesBeforeRelease,
                        windowBytes, windowSeconds, windowSeconds > 0 ? windowBytes / 1024.0 / windowSeconds : 0,
                        download.IsCompleted, server.OpenTcp, server.PeakTcp));
                    Console.WriteLine("STAGES at125s " + trace.Describe());
                    if (observeOnly) server.ReleasePending();
                }
                var result = await download.ConfigureAwait(false);
                clock.Stop();
                bool identical = result.Ok && Verify(dest, (long)workers * partSize);
                int preemptions = logs.Where(l => l.StartsWith("慢连接抢占:", StringComparison.Ordinal))
                    .Sum(l => int.Parse(l.Split(' ')[2], CultureInfo.InvariantCulture));
                double bodySlots = snapshots.Where(s => s.DoneBytes < s.TotalBytes)
                    .Select(s => (double)s.ActiveConnections / workers).DefaultIfEmpty().Average();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "METRIC mode={0} workers={1} seconds={2:F3} MiBps={3:F3} requests={4} reqps={5:F3} "
                    + "bodySlotSamples={6:P2} preemptions={7} served={8} identical={9} peakBodies={10} "
                    + "throttled={11} minRetryMs={12:F1} firstWaveMs={13}",
                    mode, workers, clock.Elapsed.TotalSeconds,
                    workers * (double)partSize / 1048576 / clock.Elapsed.TotalSeconds,
                    server.Requests, server.Requests / clock.Elapsed.TotalSeconds,
                    bodySlots, preemptions, server.Served, identical, server.PeakBodies,
                    server.Throttled, server.MinRetryMs, server.FirstWaveMs));
                foreach (string log in logs.Where(l => l.Contains("抢占") || l.Contains("假死")))
                    Console.WriteLine("  " + log);
                Check(result.Ok, result.Message);
                Check(identical, "all synthetic bytes must match");
                Check(server.Errors.IsEmpty, string.Join("; ", server.Errors));
                Check(snapshots.All(s => s.WorkerCount == workers), "worker limit must remain at the setting");
                Check(mode == "slow-body" || !logs.Any(l => l.StartsWith("连接假死:", StringComparison.Ordinal)),
                    "waiting headers and disposed error responses must not be rescued as hung bodies");
                if (mode == "headers" || mode == "paced")
                {
                    Check(server.Requests == workers, "waiting for response headers must not restart healthy requests");
                    Check(preemptions == 0, "header latency is not a slow response body");
                    Check(mode != "headers" || snapshots.Any(s => s.ActiveConnections < workers && s.ActiveConnections > 0),
                        "body-only diagnostics must not pretend pending headers are active");
                }
                else if (mode == "slow-body")
                {
                    Check(preemptions > 0 && server.Requests > workers,
                        "genuinely slow response bodies must still be preempted and resumed");
                    Check(server.ResumedOffsetsOkay, "slow bodies must resume past the initial committed offset");
                }
                else if (mode == "429")
                {
                    Check(server.Throttled == workers, "one full wave of HTTP 429 must be exercised");
                    Check(server.Requests == 2 * workers, "429 must have exactly one retry per part");
                    Check(server.MinRetryMs >= 250, "per-request 429 backoff must remain");
                    Check(preemptions == 0, "retry waits must not be preempted");
                }
                else if (mode == "429-long")
                {
                    Check(server.Throttled >= 10, "long 429 episode must exhaust a full attempt round");
                    Check(logs.Any(l => l.Contains("整轮重试")),
                        "attempt exhaustion must still use the bounded part-round budget");
                    Check(preemptions == 0, "waiting/backoff must not bypass bounded error retries");
                }
                else
                {
                    Check(!logs.Any(l => l.Contains("下载停滞")),
                        "normal body progress must prevent the global no-progress watchdog");
                    Check(requestsBeforeRelease == workers + workers * 3 / 4,
                        "never-returning first headers must be retried before the 125s observation ends");
                    Check(pendingBeforeRelease == 0 && bodiesBeforeRelease == workers,
                        "bounded header waits must restore all body slots while normal streams keep progressing");
                    Check(preemptions == 0, "header deadlines must use failure/backoff, not body preemption");
                    Check(trace.CancelledHeaders == workers * 3 / 4,
                        "one cancelled header attempt per permanently silent first request");
                    Check(trace.MinRetryAfterCancelMs >= 250, "header timeouts must retain normal failure backoff");
                    Check(server.Requests == workers + workers * 3 / 4,
                        "80-second healthy headers and long-lived bodies must not be restarted");
                    Check(server.PeakTcp <= workers, "expired header requests must close before replacements open");
                    Check(!observeOnly, "the fixed client must finish without the server releasing silent requests");
                    Console.WriteLine("STAGES final " + trace.Describe());
                }
                passed = true;
            }
        }
        finally
        {
            if (passed) Directory.Delete(root, true);
            else Console.WriteLine("Retained synthetic fixture: " + root);
        }
    }

    private static bool Verify(string path, long total)
    {
        using (var stream = File.OpenRead(path))
        {
            if (stream.Length != total) return false;
            var buffer = new byte[BlockSize];
            long offset = 0;
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                for (int i = 0; i < n; i++)
                    if (buffer[i] != Value(offset + i)) return false;
                offset += n;
            }
        }
        return true;
    }

    private static byte Value(long offset) => (byte)(1 + offset % 251);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Server : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ConcurrentBag<Task> _clients = new ConcurrentBag<Task>();
        private readonly ConcurrentDictionary<int, int> _attempts = new ConcurrentDictionary<int, int>();
        private readonly ConcurrentDictionary<int, double> _throttleTimes = new ConcurrentDictionary<int, double>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Task _accept;
        private readonly TaskCompletionSource<bool> _firstWave = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _mode;
        private readonly int _workers;
        private readonly int _partSize;
        private int _requests, _bodies, _peakBodies, _throttled;
        private int _firstRequests, _pendingHeaders, _openTcp, _peakTcp;
        private long _served;
        private long _firstWaveMs;
        private double _minRetryMs = double.MaxValue;
        public bool ResumedOffsetsOkay = true;
        public readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
        public string Url { get; }
        public int Requests => _requests;
        public long Served => Interlocked.Read(ref _served);
        public int PeakBodies => _peakBodies;
        public int Throttled => _throttled;
        public int PendingHeaders => Volatile.Read(ref _pendingHeaders);
        public int OpenTcp => Volatile.Read(ref _openTcp);
        public int PeakTcp => Volatile.Read(ref _peakTcp);
        public Task FirstWaveReady => _firstWave.Task;
        public void ReleasePending() => _release.TrySetResult(true);
        public long FirstWaveMs => Interlocked.Read(ref _firstWaveMs);
        public double MinRetryMs => _minRetryMs == double.MaxValue ? 0 : _minRetryMs;

        public Server(string mode, int workers, int partSize)
        {
            _mode = mode;
            _workers = workers;
            _partSize = partSize;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/synthetic";
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        int current = Interlocked.Increment(ref _openTcp), peak;
                        do { peak = Volatile.Read(ref _peakTcp); }
                        while (current > peak && Interlocked.CompareExchange(ref _peakTcp, current, peak) != peak);
                        _clients.Add(ServeAsync(client));
                    }
                }
                catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
                catch (SocketException) when (_stop.IsCancellationRequested) { }
            });
        }

        private async Task ServeAsync(TcpClient client)
        {
            bool body = false;
            try
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string request = await reader.ReadLineAsync().ConfigureAwait(false);
                    long total = (long)_workers * _partSize, start = 0, end = total - 1;
                    bool range = false;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        if (!line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase)) continue;
                        var pieces = line.Substring("Range: bytes=".Length).Split('-');
                        start = long.Parse(pieces[0], CultureInfo.InvariantCulture);
                        if (pieces[1].Length > 0) end = long.Parse(pieces[1], CultureInfo.InvariantCulture);
                        range = true;
                    }
                    bool head = request.StartsWith("HEAD ", StringComparison.Ordinal);
                    bool probe = head || (start == 0 && end == 0);
                    int part = (int)(start / _partSize), attempt = 0;
                    if (!probe)
                    {
                        Interlocked.Increment(ref _requests);
                        attempt = _attempts.AddOrUpdate(part, 1, (_, old) => old + 1);
                        if (attempt == 1)
                        {
                            long now = _clock.ElapsedMilliseconds, previous;
                            do { previous = Interlocked.Read(ref _firstWaveMs); }
                            while (now > previous && Interlocked.CompareExchange(ref _firstWaveMs, now, previous) != previous);
                            if (Interlocked.Increment(ref _firstRequests) == _workers)
                                _firstWave.TrySetResult(true);
                        }
                        if (_mode == "mixed-headers" && attempt == 1 && part < _workers * 3 / 4)
                        {
                            Interlocked.Increment(ref _pendingHeaders);
                            try
                            {
                                // The first request never sends headers. Finish only on peer close
                                // or explicit test teardown, so cancellation is visible at the server.
                                var closed = stream.ReadAsync(new byte[1], 0, 1, _stop.Token);
                                if (await Task.WhenAny(_release.Task, closed).ConfigureAwait(false) == closed)
                                    return;
                            }
                            finally { Interlocked.Decrement(ref _pendingHeaders); }
                        }
                        if (_mode == "mixed-headers" && attempt == 1
                            && part >= _workers * 3 / 4 && part < _workers * 13 / 16)
                            await Task.Delay(80000, _stop.Token).ConfigureAwait(false);
                        if (_mode == "slow-body" && part < 2 && attempt > 1
                            && start <= (long)part * _partSize + 1)
                            ResumedOffsetsOkay = false;
                        if (_mode == "429-long" && part < 2)
                        {
                            await Task.Delay(4000, _stop.Token).ConfigureAwait(false);
                            if (_clock.Elapsed.TotalSeconds < 35)
                            {
                                Interlocked.Increment(ref _throttled);
                                await WriteHeaders(stream, "429 Too Many Requests", 0, null).ConfigureAwait(false);
                                return;
                            }
                        }
                        if (_mode == "429" && attempt == 1)
                        {
                            Interlocked.Increment(ref _throttled);
                            _throttleTimes[part] = _clock.Elapsed.TotalMilliseconds;
                            await WriteHeaders(stream, "429 Too Many Requests", 0, null).ConfigureAwait(false);
                            return;
                        }
                        if (_mode == "429" && attempt == 2)
                        {
                            double delay = _clock.Elapsed.TotalMilliseconds - _throttleTimes[part];
                            lock (_throttleTimes) _minRetryMs = Math.Min(_minRetryMs, delay);
                        }
                        // A bounded slow-header episode; cancellation/reconnect restarts its delay.
                        // After 45s the server recovers, so even the defective client can finish.
                        if (_mode == "headers" && part < Math.Max(2, _workers / 16)
                            && _clock.Elapsed.TotalSeconds < 45)
                            await Task.Delay(22000, _stop.Token).ConfigureAwait(false);
                    }
                    await WriteHeaders(stream, range ? "206 Partial Content" : "200 OK",
                        end - start + 1, range ? "bytes " + start + "-" + end + "/" + total : null)
                        .ConfigureAwait(false);
                    if (head) return;
                    if (!probe)
                    {
                        body = true;
                        int current = Interlocked.Increment(ref _bodies);
                        int peak;
                        do { peak = _peakBodies; }
                        while (current > peak && Interlocked.CompareExchange(ref _peakBodies, current, peak) != peak);
                    }
                    bool slow = !probe && _mode == "slow-body" && part < 2 && attempt == 1;
                    bool paced = !probe && (_mode == "paced"
                        || (_mode == "headers" && part >= Math.Max(2, _workers / 16))
                        || (_mode == "429-long" && part >= 2));
                    bool mixed = !probe && _mode == "mixed-headers";
                    var buffer = new byte[slow || mixed ? 1024 : BlockSize];
                    for (long offset = start; offset <= end;)
                    {
                        int count = (int)Math.Min(buffer.Length, end - offset + 1);
                        for (int i = 0; i < count; i++) buffer[i] = Value(offset + i);
                        await stream.WriteAsync(buffer, 0, count, _stop.Token).ConfigureAwait(false);
                        if (!probe) Interlocked.Add(ref _served, count);
                        offset += count;
                        if (offset <= end && (slow || paced))
                            await Task.Delay(slow ? 250 : 1000, _stop.Token).ConfigureAwait(false);
                        if (offset <= end && mixed && !_release.Task.IsCompleted)
                            await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { /* Expected when the client preempts a synthetic request. */ }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { Errors.Enqueue(ex.ToString()); }
            finally
            {
                if (body) Interlocked.Decrement(ref _bodies);
                Interlocked.Decrement(ref _openTcp);
            }
        }

        private static async Task WriteHeaders(NetworkStream stream, string status, long length, string range)
        {
            byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 " + status
                + "\r\nAccept-Ranges: bytes\r\nContent-Length: " + length
                + (range == null ? "" : "\r\nContent-Range: " + range)
                + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _release.TrySetResult(true);
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            Task.WhenAll(_clients.ToArray()).GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
