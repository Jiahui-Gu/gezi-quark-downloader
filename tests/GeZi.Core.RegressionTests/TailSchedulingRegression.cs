using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;
using GeZi.Core.Support;

internal static class TailSchedulingRegression
{
    internal static async Task<int> RunAsync()
    {
        NetworkConfig.Apply(512);
        int failures = 0;
        foreach (var test in new Func<Task>[] { ReplenishAfterPreemption, CancelWithIdleWorkers,
            EmptyQueueCompletion, RequeueAndLastCompletionRace })
        {
            try
            {
                await test().ConfigureAwait(false);
                Console.WriteLine("PASS tail scheduling " + test.Method.Name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL tail scheduling " + test.Method.Name + ": " + ex.Message);
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static Task ReplenishAfterPreemption() => Download(false);
    private static Task CancelWithIdleWorkers() => Download(true);

    private static async Task Download(bool cancelIdle)
    {
        const int size = 4 * 1048576, total = 8 * size;
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tail-fixture-" + Guid.NewGuid().ToString("N"));
        string dest = Path.Combine(root, "synthetic.bin");
        Directory.CreateDirectory(dest + ".qparts");
        var meta = new List<string> { total.ToString(), "8" };
        for (int i = 0; i < 8; i++)
        {
            meta.Add((i * size) + "," + ((i + 1) * size - 1) + "," + (i == 0 ? 1 : 0));
            File.WriteAllBytes(Path.Combine(dest + ".qparts", "p" + i.ToString("D6")),
                i == 0 ? new[] { Value(0) } : new byte[0]);
        }
        File.WriteAllLines(dest + ".qmeta", meta);
        try
        {
            using (var server = new TailServer(total))
            using (var dl = new SegmentedDownloader(8))
            using (var cancel = new CancellationTokenSource(60000))
            {
                var logs = new ConcurrentQueue<string>();
                dl.Log = logs.Enqueue;
                var clock = Stopwatch.StartNew();
                Task<DownloadResult> task = dl.DownloadAsync(server.Url, dest, cancel: cancel.Token);
                if (cancelIdle)
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    Check(!task.IsCompleted && server.PeakBodies >= 4, "other workers must first finish while one body remains");
                    cancel.Cancel();
                    Check(await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false) == task,
                        "cancelling idle workers must wake signal waits");
                }
                var result = await task.ConfigureAwait(false);
                if (cancelIdle)
                {
                    Check(!result.Ok && result.Message == "已取消", "idle-worker cancellation must keep user cancellation semantics");
                    Check(!File.Exists(dest + ".qmeta") && !Directory.Exists(dest + ".qparts"),
                        "explicit user cancellation still clears its own partial data");
                    return;
                }
                Check(result.Ok, result.Message);
                using (var file = File.OpenRead(dest))
                {
                    Check(file.Length == total, "full output length");
                    var buffer = new byte[65536];
                    long offset = 0;
                    int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        for (int i = 0; i < count; i++) Check(buffer[i] == Value(offset + i), "Range/byte mismatch");
                        offset += count;
                    }
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "TAIL seconds={0:F3} requests={1} served={2} committed={3} peakBodies={4} peakRetriedTailBodies={5} preemptions={6} identical=True",
                    clock.Elapsed.TotalSeconds, server.Requests, server.Served, total, server.PeakBodies,
                    server.PeakTailBodies, logs.Count(l => l.StartsWith("慢连接抢占:"))));
                Check(logs.Any(l => l.StartsWith("慢连接抢占:")), "actual body preemption must requeue the slow range");
                Check(server.PeakBodies <= 8, "never add overlapping extra workers");
                Check(server.PeakTailBodies >= 2,
                    "queued tail children must wake idle workers, not be serviced by the sole survivor");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class QueueHarness : IDisposable
    {
        private readonly object _queue;
        private readonly Type _type;
        internal QueueHarness(int[] parts)
        {
            _type = typeof(SegmentedDownloader).GetNestedType("PartWorkQueue", BindingFlags.NonPublic);
            Check(_type != null, "segmented workers need explicit outstanding-work completion, not queue-empty retirement");
            _queue = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { parts, 8 }, CultureInfo.InvariantCulture);
        }
        private object Call(string method, params object[] values) =>
            _type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(_queue, values);
        internal Task<int> Take(CancellationToken cancel = default) => (Task<int>)Call("TakeAsync", cancel);
        internal void Complete() => Call("CompletePart");
        internal void Add(int idx) => Call("Add", idx);
        internal void Requeue(int idx) => Call("Requeue", idx);
        public void Dispose() => ((IDisposable)_queue).Dispose();
    }

    private static async Task EmptyQueueCompletion()
    {
        using (var queue = new QueueHarness(new int[0]))
        {
            Task<int[]> stopped = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => queue.Take()));
            Check(await Task.WhenAny(stopped, Task.Delay(2000)).ConfigureAwait(false) == stopped,
                "zero work must not wait for a nonexistent producer");
            Check((await stopped.ConfigureAwait(false)).All(i => i == -1), "zero work returns terminal signal only");
        }
    }

    private static async Task RequeueAndLastCompletionRace()
    {
        for (int round = 0; round < 200; round++)
        using (var queue = new QueueHarness(new[] { 0, 1 }))
        using (var cancel = new CancellationTokenSource(5000))
        {
            Check(await queue.Take(cancel.Token).ConfigureAwait(false) == 0, "claim first part");
            Check(await queue.Take(cancel.Token).ConfigureAwait(false) == 1, "claim last queued part");
            Task<int>[] waiters = Enumerable.Range(0, 6).Select(_ => queue.Take(cancel.Token)).ToArray();
            Check(waiters.All(t => !t.IsCompleted), "empty with in-flight work is not completion");
            await Task.WhenAll(Task.Run(() => queue.Complete()), Task.Run(() => queue.Requeue(1))).ConfigureAwait(false);
            Task<int> resumed = await Task.WhenAny(waiters).ConfigureAwait(false);
            Check(await resumed.ConfigureAwait(false) == 1, "requeued work must be consumed by a waiting worker");
            queue.Add(2);
            queue.Complete();
            Task<int> child = await Task.WhenAny(waiters.Where(t => t != resumed)).ConfigureAwait(false);
            Check(await child.ConfigureAwait(false) == 2, "new split child must wake another idle worker");
            queue.Complete();
            int[] ended = await Task.WhenAll(waiters.Where(t => t != resumed && t != child)).ConfigureAwait(false);
            Check(ended.All(i => i == -1), "last completion must wake all remaining idle workers");
        }
        Console.WriteLine("QUEUE races=200 requeueSplitAndLastCompletion=True noPolling=True");
    }

    private static byte Value(long offset) => (byte)(1 + offset % 251);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TailServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _peers = new ConcurrentBag<TcpClient>();
        private readonly ConcurrentBag<Task> _tasks = new ConcurrentBag<Task>();
        private readonly Task _accept;
        private readonly int _total;
        private bool _stopped;
        private int _firstTail, _body, _tailBody;
        internal int Requests, PeakBodies, PeakTailBodies;
        internal long Served;
        internal string Url { get; }
        internal TailServer(int total)
        {
            _total = total;
            _listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/synthetic";
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!Volatile.Read(ref _stopped))
                    {
                        TcpClient peer = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        _peers.Add(peer);
                        _tasks.Add(Serve(peer));
                    }
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _stopped)) { }
                catch (SocketException) when (Volatile.Read(ref _stopped)) { }
            });
        }
        private async Task Serve(TcpClient peer)
        {
            bool active = false, retriedTail = false;
            try
            {
                using (peer)
                using (var stream = peer.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    await reader.ReadLineAsync().ConfigureAwait(false);
                    long start = 0, end = _total - 1;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                        if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] range = line.Substring("Range: bytes=".Length).Split('-');
                            start = long.Parse(range[0], CultureInfo.InvariantCulture);
                            end = long.Parse(range[1], CultureInfo.InvariantCulture);
                        }
                    bool probe = start == 0 && end == 0;
                    bool tail = start < 4 * 1048576 && !probe;
                    bool slow = tail && Interlocked.CompareExchange(ref _firstTail, 1, 0) == 0;
                    if (!probe) Interlocked.Increment(ref Requests);
                    await Task.Delay(probe ? 0 : 120).ConfigureAwait(false);
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Length: "
                        + (end - start + 1) + "\r\nContent-Range: bytes " + start + "-" + end + "/" + _total
                        + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
                    if (!probe)
                    {
                        active = true;
                        Peak(ref PeakBodies, Interlocked.Increment(ref _body));
                        if (tail && !slow)
                        {
                            retriedTail = true;
                            Peak(ref PeakTailBodies, Interlocked.Increment(ref _tailBody));
                        }
                    }
                    byte[] buffer = new byte[slow ? 1024 : 65536];
                    for (long offset = start; offset <= end;)
                    {
                        int count = (int)Math.Min(buffer.Length, end - offset + 1);
                        for (int i = 0; i < count; i++) buffer[i] = Value(offset + i);
                        await stream.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                        if (!probe) Interlocked.Add(ref Served, count);
                        offset += count;
                        if (!probe && offset <= end)
                            await Task.Delay(slow ? 125 : tail ? 250 : 8).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { /* Synthetic body cancellation/teardown. */ }
            finally
            {
                if (active) Interlocked.Decrement(ref _body);
                if (retriedTail) Interlocked.Decrement(ref _tailBody);
            }
        }
        private static void Peak(ref int peak, int current)
        {
            int old;
            do { old = Volatile.Read(ref peak); }
            while (current > old && Interlocked.CompareExchange(ref peak, current, old) != old);
        }
        public void Dispose()
        {
            Volatile.Write(ref _stopped, true);
            _listener.Stop();
            _accept.GetAwaiter().GetResult();
            foreach (var peer in _peers) peer.Dispose();
            Task.WhenAll(_tasks.ToArray()).GetAwaiter().GetResult();
        }
    }
}
