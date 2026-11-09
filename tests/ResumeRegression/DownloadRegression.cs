using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;

namespace ResumeRegression
{
    internal static class DownloadRegression
    {
        private const int FileCount = 11;
        private const int PartCount = 1024;
        private const int PartSize = 4096;
        private const int MergedParts = 230;
        private const int CachedPrefix = 1024;
        private const int Workers = 256;
        private const long Total = PartCount * PartSize;

        internal static async Task RunDynamicSplitAsync()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "dynamic-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string dest = Path.Combine(root, "dynamic.bin");
            string parts = dest + ".qparts";
            Directory.CreateDirectory(parts);
            File.WriteAllBytes(dest, Data(0, 0, 65536));
            long[] starts = { 0, 65536, 131072, 1441792, Total };
            var lines = new List<string> { Total.ToString(), "4" };
            for (int i = 0; i < 4; i++)
            {
                int written = i == 0 ? 65536 : 1;
                lines.Add(starts[i] + "," + (starts[i + 1] - 1) + "," + written);
                if (i > 0)
                    File.WriteAllBytes(Path.Combine(parts, "p" + i.ToString("D6")),
                        Data(0, starts[i], written));
            }
            File.WriteAllLines(dest + ".qmeta", lines);
            bool passed = false;
            try
            {
                using (var failing = new RangeServer(false, true))
                using (var dl = new SegmentedDownloader(threads: 4))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var result = await dl.DownloadAsync(failing.Url + "0", dest, null, timeout.Token)
                        .ConfigureAwait(false);
                    Program.Assert(!result.Ok, "injected HTTP 404 must preserve the partial task");
                }
                var saved = File.ReadAllLines(dest + ".qmeta");
                var offsets = saved.Skip(2).Select(l => long.Parse(l.Split(',')[0])).ToArray();
                Program.Assert(offsets.Length > 4, "real workers must append a split");
                Program.Assert(!offsets.SequenceEqual(offsets.OrderBy(x => x)),
                    "the worker-generated plan must be logically out of index order");
                Console.WriteLine("Real worker split plan: " + string.Join(",", offsets));
                using (var server = new RangeServer(false))
                using (var dl = new SegmentedDownloader(threads: 4))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var result = await dl.DownloadAsync(server.Url + "0", dest, null, timeout.Token)
                        .ConfigureAwait(false);
                    Program.Assert(result.Ok, result.Message);
                    Program.Assert(File.ReadAllBytes(dest).SequenceEqual(Data(0, 0, (int)Total)),
                        "restart after a real dynamic split must match all source bytes");
                }
                passed = true;
            }
            finally
            {
                if (passed) Directory.Delete(root, true);
                else Console.WriteLine("Synthetic dynamic fixture retained: " + root);
            }
        }

        internal static async Task RunSplitPlanAsync(bool missingHead)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "split-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string dest = Path.Combine(root, "split.bin");
            string parts = dest + ".qparts";
            Directory.CreateDirectory(parts);
            const int headStart = 262144;
            const int have = headStart + 1024;
            byte[] prefix = Data(0, 0, have);
            File.WriteAllBytes(dest, prefix);
            File.WriteAllText(dest + ".qmeta", Total + "\n4\n0,65535,65536\n"
                + "131072,262143,131072\n262144," + (Total - 1) + "," + (Total - headStart)
                + "\n65536,131071,65536\n");
            if (!missingHead)
                File.WriteAllBytes(Path.Combine(parts, "p000002"), Data(0, headStart, (int)Total - headStart));
            bool passed = false;
            try
            {
                using (var server = new RangeServer(false))
                using (var dl = new SegmentedDownloader(threads: 4))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    var result = await dl.DownloadAsync(server.Url + "0", dest, null, timeout.Token)
                        .ConfigureAwait(false);
                    if (missingHead)
                    {
                        Program.Assert(!result.Ok, "missing partial-head cache must not be silently accepted");
                        Program.Assert(File.ReadAllBytes(dest).SequenceEqual(prefix),
                            "reconciliation must not truncate the already-merged destination");
                        Program.Assert(File.Exists(dest + ".qmeta"), "failure must preserve metadata");
                        Program.Assert(server.Served == 0, "ambiguous resume must stop before network download");
                    }
                    else
                    {
                        Program.Assert(result.Ok, result.Message);
                        Program.Assert(File.ReadAllBytes(dest).SequenceEqual(Data(0, 0, (int)Total)),
                            "logical split-plan resume must preserve all bytes");
                        Program.Assert(server.Served == 0, "already merged split must not be downloaded again");
                    }
                }
                passed = true;
            }
            finally
            {
                if (passed) Directory.Delete(root, true);
                else Console.WriteLine("Synthetic split fixture retained: " + root);
            }
        }

        internal static async Task RunAsync(int fileCount = FileCount, int workers = Workers,
            int concurrent = FileCount, bool fresh = false, bool throttle = false)
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "download-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            bool passed = false;
            try
            {
                using (var server = new RangeServer(fileCount: fileCount, workers: workers,
                    concurrent: concurrent, fresh: fresh, throttle: throttle))
                using (var scheduler = new DownloadScheduler(concurrent))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    var jobs = new List<DownloadJob>();
                    var metadata = new Dictionary<string, string>();
                    var logs = new ConcurrentQueue<string>();
                    scheduler.Log = logs.Enqueue;
                    for (int file = 0; file < fileCount; file++)
                    {
                        string dest = Path.Combine(root, "file-" + file + ".bin");
                        string partsRoot = Path.Combine(root, "cache");
                        SegmentedDownloader.ResolvePaths(dest, partsRoot, out string parts, out string meta);
                        if (!fresh)
                        {
                            Directory.CreateDirectory(parts);
                            File.WriteAllBytes(dest, Data(file, 0, MergedParts * PartSize));
                            var lines = new List<string> { Total.ToString(), PartCount.ToString() };
                            for (int p = 0; p < PartCount; p++)
                            {
                                long start = p * PartSize;
                                int written = p < MergedParts ? PartSize : CachedPrefix;
                                lines.Add(start + "," + (start + PartSize - 1) + "," + written);
                                if (p >= MergedParts)
                                    File.WriteAllBytes(Path.Combine(parts, "p" + p.ToString("D6")),
                                        Data(file, start, written));
                            }
                            File.WriteAllLines(meta, lines);
                            metadata.Add(meta, string.Join("\n", lines));
                        }
                        jobs.Add(new DownloadJob
                        {
                            Name = "file-" + file, Dest = dest, Threads = workers,
                            PartsRoot = partsRoot, Url = server.Url + file, Cookie = "",
                        });
                    }
                    var updates = new Updates();
                    var download = scheduler.RunAllAsync(jobs, updates, timeout.Token);
                    try
                    {
                        var ready = await Task.WhenAny(server.AllWorkersReady, download,
                            Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);
                        if (ready != server.AllWorkersReady)
                            throw new InvalidOperationException("Did not reach configured simultaneous ranges: "
                                + string.Join(",", server.Peaks));
                        await server.AllWorkersReady.ConfigureAwait(false);

                        // Bodies are held at the server: no new byte can have been downloaded.
                        // Check the original checkpoint geometry AND W, not just final file size.
                        foreach (var pair in metadata)
                            Program.Assert(await ReadCheckpointAsync(pair.Key).ConfigureAwait(false) == pair.Value,
                                "existing checkpoint must not be repartitioned or cleared");
                        Program.Assert(server.Active == concurrent * workers,
                            "all configured range requests must be live at the same instant");
                        Console.WriteLine("Observed simultaneous ranges per file: "
                            + string.Join(",", server.Peaks) + "; total=" + server.Active);
                    }
                    finally
                    {
                        if (server.AllWorkersReady.Status == TaskStatus.RanToCompletion)
                            server.ReleaseDownloads();
                        else
                            server.Release();
                        await download.ConfigureAwait(false);
                    }
                    foreach (var job in jobs)
                    {
                        Program.Assert(updates.Completed.ContainsKey(job),
                            "download must complete: " + job.Name + "; " + string.Join("; ", updates.Errors));
                        int file = jobs.IndexOf(job);
                        VerifyFile(job.Dest, file, server.TotalFor(file));
                    }
                    Program.Assert(server.Errors.IsEmpty, string.Join("; ", server.Errors));
                    Program.Assert(server.Peaks.All(p => p == workers), "every file must reach requested workers");
                    Program.Assert(updates.PeakJobs == concurrent, "scheduler must enforce simultaneous task count");
                    long expected = fresh
                        ? Enumerable.Range(0, fileCount).Sum(server.TotalFor)
                        : (long)fileCount * (PartCount - MergedParts) * (PartSize - CachedPrefix);
                    Program.Assert(server.Served == expected,
                        "must request only missing bytes (no cached bytes downloaded again)");
                    if (throttle)
                    {
                        Program.Assert(server.Throttled == fileCount * workers, "inject a full wave of HTTP 429");
                        Program.Assert(logs.Any(l => l.Contains("HTTP 429/503")), "throttling must be reported");
                        Program.Assert(!logs.Any(l => l.Contains("收窄") || l.Contains("回升")),
                            "request errors must not change the worker limit");
                    }
                    Console.WriteLine("Verified byte-identical output for " + fileCount + " files; missing bytes served="
                        + server.Served + "; cached bytes re-requested=0");
                }
                passed = true;
            }
            finally
            {
                if (passed) Directory.Delete(root, true);
                else Console.WriteLine("Synthetic fixture retained: " + root);
            }
        }

        private static async Task<string> ReadCheckpointAsync(string path)
        {
            // SavePlan atomically replaces this file once a second. Permit replacement
            // and retry brief Windows sharing conflicts without changing the checkpoint.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                        return (await reader.ReadToEndAsync().ConfigureAwait(false))
                            .Replace("\r\n", "\n").TrimEnd('\n');
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(25).ConfigureAwait(false);
                }
            }
        }

        private static byte[] Data(int file, long start, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                long offset = start + i;
                data[i] = (byte)((offset * 31 + (offset >> 8) * 17 + file * 43) % 251);
            }
            return data;
        }

        private static void VerifyFile(string path, int file, long total)
        {
            using (var stream = File.OpenRead(path))
            {
                Program.Assert(stream.Length == total, "output length must match the HTTP source");
                var buffer = new byte[65536];
                long offset = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    Program.Assert(buffer.Take(read).SequenceEqual(Data(file, offset, read)),
                        "output must match every source byte at " + offset);
                    offset += read;
                }
            }
        }

        private sealed class Updates : IProgress<JobUpdate>
        {
            private readonly HashSet<DownloadJob> _running = new HashSet<DownloadJob>();
            internal int PeakJobs;
            internal readonly ConcurrentDictionary<DownloadJob, byte> Completed =
                new ConcurrentDictionary<DownloadJob, byte>();
            internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
            public void Report(JobUpdate value)
            {
                lock (_running)
                {
                    if (value.State == JobState.Downloading) _running.Add(value.Job);
                    else _running.Remove(value.Job);
                    PeakJobs = Math.Max(PeakJobs, _running.Count);
                }
                if (value.State == JobState.Completed) Completed.TryAdd(value.Job, 0);
                if (value.State == JobState.Failed || value.State == JobState.Cancelled)
                    Errors.Enqueue(value.Job.Name + ": " + value.Error);
            }
        }

        private sealed class RangeServer : IDisposable
        {
            private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _ready =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly ConcurrentBag<Task> _handlers = new ConcurrentBag<Task>();
            private readonly int[] _active;
            private readonly int[] _peak;
            private readonly int _readyCount;
            private readonly int _workers;
            private readonly TaskCompletionSource<bool>[] _bodyGates;
            private bool _autoRelease;
            private readonly bool _fresh;
            private readonly bool _throttle;
            private int _throttled;
            private readonly ConcurrentDictionary<string, byte> _attempted =
                new ConcurrentDictionary<string, byte>();
            private readonly object _lock = new object();
            private readonly Task _accept;
            private volatile bool _stopping;
            private int _totalActive;
            private long _served;
            private readonly bool _hold;
            private readonly bool _failSegments;
            internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();

            internal RangeServer(bool hold = true, bool failSegments = false,
                int fileCount = FileCount, int workers = Workers, int concurrent = FileCount,
                bool fresh = false, bool throttle = false)
            {
                _fresh = fresh;
                _throttle = throttle;
                _active = new int[fileCount];
                _peak = new int[fileCount];
                _readyCount = workers * concurrent;
                _workers = workers;
                _bodyGates = Enumerable.Range(0, fileCount).Select(_ =>
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
                _hold = hold;
                _failSegments = failSegments;
                if (!hold) _release.TrySetResult(true);
                _listener.Start(4096);
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
                _accept = AcceptAsync();
            }
            internal string Url { get; }
            internal Task AllWorkersReady => _ready.Task;
            internal int Active => Volatile.Read(ref _totalActive);
            internal int[] Peaks { get { lock (_lock) return _peak.ToArray(); } }
            internal long Served => Interlocked.Read(ref _served);
            internal int Throttled => Volatile.Read(ref _throttled);
            internal long TotalFor(int file) => _fresh ? (256L * 1024 * 1024 + file * 512L * 1024) : Total;
            internal void Release() => _release.TrySetResult(true);
            internal void ReleaseDownloads()
            {
                lock (_lock)
                {
                    _autoRelease = true;
                    for (int i = 0; i < _active.Length; i++)
                        if (_active[i] >= _workers) _bodyGates[i].TrySetResult(true);
                }
            }

            private async Task AcceptAsync()
            {
                try
                {
                    while (!_stopping)
                    {
                        var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        _handlers.Add(HandleAsync(client));
                    }
                }
                catch (ObjectDisposedException) when (_stopping) { }
                catch (SocketException) when (_stopping) { }
            }

            private async Task HandleAsync(TcpClient client)
            {
                int file = -1;
                bool counted = false;
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string request = await reader.ReadLineAsync().ConfigureAwait(false);
                        file = int.Parse(request.Split(' ')[1].Trim('/'), CultureInfo.InvariantCulture);
                        string range = null;
                        string line;
                        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                            if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                                range = line.Substring(6).Trim();
                        if (range == null) throw new InvalidOperationException("Missing Range header");
                        var bounds = range.Substring("bytes=".Length).Split('-');
                        long start = long.Parse(bounds[0], CultureInfo.InvariantCulture);
                        long end = long.Parse(bounds[1], CultureInfo.InvariantCulture);
                        bool probe = start == 0 && end == 0;
                        if (!probe && _throttle && start < (MergedParts + _workers) * PartSize
                            && _attempted.TryAdd(file + ":" + start, 0))
                        {
                            Interlocked.Increment(ref _throttled);
                            byte[] failure = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(failure, 0, failure.Length).ConfigureAwait(false);
                            return;
                        }
                        if (!probe && _failSegments)
                        {
                            byte[] failure = Encoding.ASCII.GetBytes(
                                "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(failure, 0, failure.Length).ConfigureAwait(false);
                            return;
                        }
                        if (!probe && _hold && !_fresh)
                        {
                            Program.Assert(start >= MergedParts * PartSize
                                && start % PartSize == CachedPrefix && end == start - CachedPrefix + PartSize - 1,
                                "range must start after the cached prefix and retain the old part boundary");
                        }
                        int length = checked((int)(end - start + 1));
                        byte[] headers = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 206 Partial Content\r\nContent-Length: " + length
                            + "\r\nContent-Range: bytes " + start + "-" + end + "/" + TotalFor(file)
                            + "\r\nConnection: close\r\n\r\n");
                        if (!probe)
                        {
                            lock (_lock)
                            {
                                counted = true;
                                _active[file]++;
                                _totalActive++;
                                _peak[file] = Math.Max(_peak[file], _active[file]);
                                if (_totalActive == _readyCount) _ready.TrySetResult(true);
                                if (_autoRelease && _active[file] >= _workers)
                                    _bodyGates[file].TrySetResult(true);
                            }
                            await Task.WhenAny(_release.Task, _bodyGates[file].Task).ConfigureAwait(false);
                        }
                        await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                        for (int sent = 0; sent < length;)
                        {
                            byte[] data = Data(file, start + sent, Math.Min(65536, length - sent));
                            await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                            sent += data.Length;
                        }
                        if (!probe) Interlocked.Add(ref _served, length);
                    }
                }
                catch (Exception ex)
                {
                    Errors.Enqueue(ex.GetType().Name + ": " + ex.Message);
                    _ready.TrySetException(ex);
                }
                finally
                {
                    if (counted)
                        lock (_lock) { _active[file]--; _totalActive--; }
                }
            }

            public void Dispose()
            {
                _stopping = true;
                Release();
                _listener.Stop();
                _accept.GetAwaiter().GetResult();
                Task.WhenAll(_handlers.ToArray()).GetAwaiter().GetResult();
            }
        }
    }
}
