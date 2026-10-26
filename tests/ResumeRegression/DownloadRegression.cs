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
        private const int PartCount = 505;
        private const int PartSize = 4096;
        private const int MergedParts = 230;
        private const int CachedPrefix = 1024;
        private const int Workers = 256;
        private const long Total = PartCount * PartSize;

        internal static async Task RunAsync()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "download-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            bool passed = false;
            try
            {
                using (var server = new RangeServer())
                using (var scheduler = new DownloadScheduler(FileCount))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    var jobs = new List<DownloadJob>();
                    var metadata = new Dictionary<string, string>();
                    for (int file = 0; file < FileCount; file++)
                    {
                        string dest = Path.Combine(root, "file-" + file + ".bin");
                        string partsRoot = Path.Combine(root, "cache");
                        SegmentedDownloader.ResolvePaths(dest, partsRoot, out string parts, out string meta);
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
                        jobs.Add(new DownloadJob
                        {
                            Name = "file-" + file, Dest = dest, Threads = Workers,
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
                            throw new InvalidOperationException("Did not reach 11 x 256 simultaneous ranges: "
                                + string.Join(",", server.Peaks));
                        await server.AllWorkersReady.ConfigureAwait(false);

                        // Bodies are held at the server: no new byte can have been downloaded.
                        // Check the original checkpoint geometry AND W, not just final file size.
                        foreach (var pair in metadata)
                            Program.Assert(await ReadCheckpointAsync(pair.Key).ConfigureAwait(false) == pair.Value,
                                "existing checkpoint must not be repartitioned or cleared");
                        Program.Assert(server.Active == FileCount * Workers,
                            "all 2816 range requests must be live at the same instant");
                        Console.WriteLine("Observed simultaneous ranges per file: "
                            + string.Join(",", server.Peaks) + "; total=" + server.Active);
                    }
                    finally
                    {
                        server.Release();
                        await download.ConfigureAwait(false);
                    }
                    foreach (var job in jobs)
                    {
                        Program.Assert(updates.Completed.ContainsKey(job),
                            "download must complete: " + job.Name + "; " + string.Join("; ", updates.Errors));
                        int file = jobs.IndexOf(job);
                        Program.Assert(File.ReadAllBytes(job.Dest).SequenceEqual(Data(file, 0, (int)Total)),
                            "resumed output must match every source byte");
                    }
                    Program.Assert(server.Errors.IsEmpty, string.Join("; ", server.Errors));
                    long expected = (long)FileCount * (PartCount - MergedParts) * (PartSize - CachedPrefix);
                    Program.Assert(server.Served == expected,
                        "must request only missing bytes (no cached bytes downloaded again)");
                    Console.WriteLine("Verified byte-identical output for 11 files; missing bytes served="
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

        private sealed class Updates : IProgress<JobUpdate>
        {
            internal readonly ConcurrentDictionary<DownloadJob, byte> Completed =
                new ConcurrentDictionary<DownloadJob, byte>();
            internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
            public void Report(JobUpdate value)
            {
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
            private readonly int[] _active = new int[FileCount];
            private readonly int[] _peak = new int[FileCount];
            private readonly object _lock = new object();
            private readonly Task _accept;
            private volatile bool _stopping;
            private int _totalActive;
            private long _served;
            internal readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();

            internal RangeServer()
            {
                _listener.Start(4096);
                Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
                _accept = AcceptAsync();
            }
            internal string Url { get; }
            internal Task AllWorkersReady => _ready.Task;
            internal int Active => Volatile.Read(ref _totalActive);
            internal int[] Peaks { get { lock (_lock) return _peak.ToArray(); } }
            internal long Served => Interlocked.Read(ref _served);
            internal void Release() => _release.TrySetResult(true);

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
                        if (!probe)
                        {
                            Program.Assert(start >= MergedParts * PartSize
                                && start % PartSize == CachedPrefix && end == start - CachedPrefix + PartSize - 1,
                                "range must start after the cached prefix and retain the old part boundary");
                        }
                        int length = checked((int)(end - start + 1));
                        byte[] headers = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 206 Partial Content\r\nContent-Length: " + length
                            + "\r\nContent-Range: bytes " + start + "-" + end + "/" + Total
                            + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                        if (!probe)
                        {
                            lock (_lock)
                            {
                                counted = true;
                                _active[file]++;
                                _totalActive++;
                                _peak[file] = Math.Max(_peak[file], _active[file]);
                                if (_totalActive == FileCount * Workers) _ready.TrySetResult(true);
                            }
                            await _release.Task.ConfigureAwait(false);
                        }
                        byte[] data = Data(file, start, length);
                        await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
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
