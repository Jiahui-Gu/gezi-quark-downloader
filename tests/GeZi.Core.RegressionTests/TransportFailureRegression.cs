using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;
using GeZi.Core.Support;

internal static class TransportFailureRegression
{
    internal static async Task<int> RunAsync()
    {
        NetworkConfig.Apply(512);
        int failures = 0;
        foreach (string mode in new[] { "tls-fin", "tls-reset", "http-fin", "http-reset",
            "caller-cancel", "client-dispose", "scheduler-scope", "redacted-unknown" })
        {
            try
            {
                if (mode == "redacted-unknown")
                {
                    string text = Describe(new IOException("https://private.invalid/?token=secret Cookie=private"));
                    Check(text.Contains("Template=Unknown") && text.Contains("Origin=Unknown"),
                        "unrecognized IO must explicitly remain unknown");
                    Check(!text.Contains("private") && !text.Contains("secret"), "no raw exception message");
                }
                else using (var server = new Peer(mode))
                {
                    if (mode == "scheduler-scope") await Scope(server).ConfigureAwait(false);
                    else await Failure(server, mode).ConfigureAwait(false);
                }
                Console.WriteLine("PASS transport " + mode);
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine("FAIL transport " + mode + ": " + ex.Message);
            }
        }
        return failures == 0 ? 0 : 1;
    }

    private static string Describe(Exception error) => (string)typeof(SegmentedDownloader)
        .GetMethod("DescribeFailure", BindingFlags.NonPublic | BindingFlags.Static)
        .Invoke(null, new object[] { error });

    private static async Task Failure(Peer server, string mode)
    {
        using (var dl = new SegmentedDownloader(8))
        using (var cancel = new CancellationTokenSource())
        {
            var http = (HttpClient)typeof(SegmentedDownloader).GetField("_http",
                BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dl);
            Task<HttpResponseMessage> request = http.GetAsync(server.Url,
                HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            bool cancelled = mode == "caller-cancel" || mode == "client-dispose";
            if (cancelled)
            {
                Check(await Task.WhenAny(server.FirstBytes, Task.Delay(5000)).ConfigureAwait(false) == server.FirstBytes,
                    "the original handler must have sent a real request");
                if (mode == "caller-cancel") cancel.Cancel();
                else http.Dispose();
            }
            Check(await Task.WhenAny(request, Task.Delay(10000)).ConfigureAwait(false) == request,
                "controlled transport failure must terminate");
            Exception failure = null;
            try { using (await request.ConfigureAwait(false)) { } }
            catch (Exception ex) { failure = ex; }
            Check(failure != null, "peer must fail, not synthesize success");
            string text = Describe(failure);
            Console.WriteLine("TRANSPORT " + mode + " " + text);
            Check(!text.Contains(server.Url), "transport description must not print URL");
            if (mode.StartsWith("tls", StringComparison.Ordinal))
            {
                Check(text.Contains("Status=SendFailure") && text.Contains("IOException[HResult=0x80131620"),
                    "TLS pre-handshake close must reproduce the observed production chain");
                Check(text.Contains("Template=net_auth_eof") && text.Contains("Origin=TlsWrite"),
                    "safe framework template and origin must distinguish this IO cause");
            }
            else if (cancelled)
                Check(failure is OperationCanceledException, "local cancellation must not be relabelled TLS EOF");
            else
                Check((text.Contains("Status=ConnectionClosed") || text.Contains("Status=RequestCanceled"))
                    && !cancel.IsCancellationRequested && !text.Contains("Template=net_auth_eof"),
                    "plain HTTP peer close can be framework-cancelled without caller cancellation, but is not TLS EOF");
        }
    }

    private static async Task Scope(Peer server)
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scope-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var jobs = new List<DownloadJob>();
            foreach (string name in new[] { "synthetic-alpha.bin", "synthetic-beta.bin" })
            {
                string dest = Path.Combine(root, name);
                Directory.CreateDirectory(dest + ".qparts");
                File.WriteAllBytes(Path.Combine(dest + ".qparts", "p000000"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(dest + ".qparts", "p000001"), new byte[] { 1 });
                File.WriteAllLines(dest + ".qmeta", new[] {
                    "131072", "2", "0,65535,1", "65536,131071,1" });
                jobs.Add(new DownloadJob { Name = name, Dest = dest, Url = server.Url, Threads = 2,
                    Cookie = "synthetic_cookie=must_not_leak" });
            }
            var logs = new ConcurrentQueue<string>();
            using (var scheduler = new DownloadScheduler(2))
            {
                scheduler.Log = logs.Enqueue;
                await scheduler.RunAllAsync(jobs, null, CancellationToken.None).ConfigureAwait(false);
            }
            Check(logs.Any(l => l.Contains("有界尝试结束")), "real fatal responses must emit terminal attempt context");
            foreach (var job in jobs)
                Check(logs.Any(l => l.StartsWith("[" + job.Name + "; job=" + job.Id + "] ", StringComparison.Ordinal)),
                    "each download's core logs need exact file/task correlation");
            Check(logs.All(l => jobs.Any(job => l.StartsWith("[" + job.Name + "; job=" + job.Id + "] ",
                StringComparison.Ordinal))), "no unattributed interleaved downloader log");
            Check(logs.All(l => !l.Contains("must_not_leak") && !l.Contains(server.Url)),
                "attribution must not include Cookie/URL");
            Check(logs.Where(l => l.Contains("有界尝试结束")).All(l =>
                l.Contains("attemptMs=") && l.Contains("userCancelled=0")
                && l.Contains("taskCancelled=0") && l.Contains("partCancelled=0")),
                "terminal attempts need duration and actual cancellation state, separate from cause");
            Console.WriteLine("SCOPE twoFiles=True allLogsAttributed=True noSecret=True");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Peer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _peers = new ConcurrentBag<TcpClient>();
        private readonly ConcurrentBag<Task> _tasks = new ConcurrentBag<Task>();
        private readonly TaskCompletionSource<bool> _bytes = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _accept;
        private readonly string _mode;
        private bool _stopped;
        internal string Url { get; }
        internal Task FirstBytes => _bytes.Task;
        internal Peer(string mode)
        {
            _mode = mode;
            _listener.Start();
            Url = (mode.StartsWith("tls", StringComparison.Ordinal) ? "https" : "http")
                + "://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/synthetic";
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
            try
            {
                using (peer)
                using (var stream = peer.GetStream())
                {
                    if (_mode == "scheduler-scope")
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            await reader.ReadLineAsync().ConfigureAwait(false);
                            bool probe = false;
                            string line;
                            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                                if (line.Equals("Range: bytes=0-0", StringComparison.OrdinalIgnoreCase)) probe = true;
                            string response = probe
                                ? "HTTP/1.1 206 Partial Content\r\nContent-Length: 1\r\nContent-Range: bytes 0-0/131072\r\nConnection: close\r\n\r\n"
                                : "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                            byte[] data = System.Text.Encoding.ASCII.GetBytes(response);
                            await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                            if (probe) await stream.WriteAsync(new byte[] { 1 }, 0, 1).ConfigureAwait(false);
                        }
                        return;
                    }
                    var buffer = new byte[8192];
                    await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    _bytes.TrySetResult(true);
                    if (_mode == "caller-cancel" || _mode == "client-dispose")
                        await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    else if (_mode.EndsWith("reset", StringComparison.Ordinal))
                        peer.Client.LingerState = new LingerOption(true, 0);
                }
            }
            catch (IOException) { /* Controlled peer cancellation/teardown. */ }
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
