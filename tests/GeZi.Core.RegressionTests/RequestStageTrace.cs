using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;

// Test-only observation around the original production HTTP handler; no fake network responses.
internal sealed class RequestStageTrace : DelegatingHandler
{
    private readonly ConcurrentDictionary<long, long> _waiting = new ConcurrentDictionary<long, long>();
    private readonly ConcurrentDictionary<long, long> _cancelledAt = new ConcurrentDictionary<long, long>();
    private HttpClient _originalClient;
    private int _bodies, _reads, _cancelled, _requests;
    private long _readBytes, _readTicks, _maxHeaderTicks;
    private double _minRetryMs = double.MaxValue;

    internal int CancelledHeaders => Volatile.Read(ref _cancelled);
    internal int RequestsStarted => Volatile.Read(ref _requests);
    internal double MinRetryAfterCancelMs => _minRetryMs == double.MaxValue ? 0 : _minRetryMs;

    internal static RequestStageTrace Attach(SegmentedDownloader downloader)
    {
        var field = typeof(SegmentedDownloader).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic);
        var original = (HttpClient)field.GetValue(downloader);
        var handlerField = typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(HttpMessageHandler));
        var trace = new RequestStageTrace
        {
            InnerHandler = (HttpMessageHandler)handlerField.GetValue(original),
            _originalClient = original
        };
        field.SetValue(downloader, new HttpClient(trace) { Timeout = original.Timeout });
        return trace;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var range = request.Headers.Range?.Ranges.SingleOrDefault();
        if (range == null || (range.From == 0 && range.To == 0))
            return await base.SendAsync(request, cancel).ConfigureAwait(false);
        Interlocked.Increment(ref _requests);
        long key = range.From.Value, start = Stopwatch.GetTimestamp();
        if (_cancelledAt.TryRemove(key, out long previous))
        {
            double gap = Milliseconds(start - previous);
            lock (_cancelledAt) _minRetryMs = Math.Min(_minRetryMs, gap);
        }
        _waiting[key] = start;
        try
        {
            var response = await base.SendAsync(request, cancel).ConfigureAwait(false);
            response.Content = new ObservedContent(response.Content, this);
            return response;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            _cancelledAt[key] = Stopwatch.GetTimestamp();
            throw;
        }
        finally
        {
            _waiting.TryRemove(key, out _);
            long elapsed = Stopwatch.GetTimestamp() - start, old;
            do { old = Interlocked.Read(ref _maxHeaderTicks); }
            while (elapsed > old && Interlocked.CompareExchange(ref _maxHeaderTicks, elapsed, old) != old);
        }
    }

    internal string Describe()
    {
        long now = Stopwatch.GetTimestamp();
        return string.Format(CultureInfo.InvariantCulture,
            "headersWaiting={0} oldestHeaderMs={1:F1} bodiesOpen={2} bodyReadsPending={3} "
            + "bodyNonReadPhase={4} cancelledHeaders={5} maxHeaderMs={6:F1} minRetryAfterCancelMs={7:F1} "
            + "networkReadBytes={8} aggregateReadAwaitSeconds={9:F3}",
            _waiting.Count, _waiting.Values.Select(t => Milliseconds(now - t)).DefaultIfEmpty().Max(),
            Volatile.Read(ref _bodies), Volatile.Read(ref _reads),
            Math.Max(0, Volatile.Read(ref _bodies) - Volatile.Read(ref _reads)),
            CancelledHeaders, Milliseconds(Interlocked.Read(ref _maxHeaderTicks)), MinRetryAfterCancelMs,
            Interlocked.Read(ref _readBytes), Milliseconds(Interlocked.Read(ref _readTicks)) / 1000);
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _originalClient?.Dispose();
    }

    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly RequestStageTrace _owner;
        internal ObservedContent(HttpContent inner, RequestStageTrace owner)
        {
            _inner = inner;
            _owner = owner;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override async Task<Stream> CreateContentReadStreamAsync()
            => new ObservedStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _owner);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            => _inner.CopyToAsync(stream);
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class ObservedStream : Stream
    {
        private readonly Stream _inner;
        private readonly RequestStageTrace _owner;
        private int _disposed;
        internal ObservedStream(Stream inner, RequestStageTrace owner)
        {
            _inner = inner;
            _owner = owner;
            Interlocked.Increment(ref owner._bodies);
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel)
        {
            long start = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _owner._reads);
            try
            {
                int n = await _inner.ReadAsync(buffer, offset, count, cancel).ConfigureAwait(false);
                Interlocked.Add(ref _owner._readBytes, n);
                return n;
            }
            finally
            {
                Interlocked.Add(ref _owner._readTicks, Stopwatch.GetTimestamp() - start);
                Interlocked.Decrement(ref _owner._reads);
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref _owner._bodies);
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
