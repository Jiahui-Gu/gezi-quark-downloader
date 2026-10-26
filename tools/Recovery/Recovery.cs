using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GeZi.Core.Support;

namespace GeZi.Recovery
{
    public static class Recovery
    {
        private sealed class Failure : Exception
        {
            internal Failure(string message) : base(message) { }
        }
        private sealed class Span
        {
            internal long Start, End;
            internal FileStream Stream;
            internal string Name;
        }
        private sealed class Gap
        {
            internal long Start, End;
        }
        private sealed class Options
        {
            internal bool Plan, Recover;
            internal string Dest, Meta, Parts, Output, Md5, LinkFile, CookieFile;
            internal readonly List<string> Patches = new List<string>();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);

        public static int Run(string[] args, TextWriter output)
        {
            List<FileStream> handles = new List<FileStream>();
            try
            {
                Options options = Parse(args);
                ValidatePaths(options);
                List<Span> spans = new List<Span>();
                long total = ReadSources(options, handles, spans);
                foreach (string patch in options.Patches) AddPatch(patch, total, handles, spans);
                spans.Sort(delegate(Span a, Span b) { return a.Start.CompareTo(b.Start); });
                CompareOverlaps(spans);
                List<Gap> gaps = FindGaps(spans, total);
                output.WriteLine("TOTAL " + total.ToString(CultureInfo.InvariantCulture));
                foreach (Span span in spans)
                    output.WriteLine("SOURCE " + span.Name + " " + span.Start + "-" + span.End);
                foreach (Gap gap in gaps) output.WriteLine("GAP " + gap.Start + "-" + gap.End);
                if (options.Plan)
                {
                    output.WriteLine("PLAN ONLY; no files written; prefix integrity requires final expected MD5.");
                    return 0;
                }
                if (gaps.Count > 0 && options.LinkFile == null)
                    throw new Failure("MISSING_GAPS: supply explicit --patch mappings or --link-file.");
                Uri link = gaps.Count == 0 ? null : ReadLink(options.LinkFile, handles);
                string cookie = gaps.Count == 0 || options.CookieFile == null ? null : ReadSecretFile(options.CookieFile, handles);
                if (cookie != null && (cookie.Contains("\r") || cookie.Contains("\n")))
                    throw new Failure("INVALID_COOKIE_FILE");
                // Win32 creation fails if another process has already claimed this directory.
                if (!CreateDirectoryW(options.Output, IntPtr.Zero))
                    throw new Failure("OUTPUT_CREATE_FAILED: directory must be new with an existing parent.");
                RejectReparse(options.Output);
                for (int i = 0; i < gaps.Count; i++)
                {
                    Gap gap = gaps[i];
                    string path = Path.Combine(options.Output, "range-" + i.ToString("D6", CultureInfo.InvariantCulture) + ".patch");
                    DownloadRange(link, cookie, gap, total, path);
                    AddSpan(spans, handles, path, gap.Start, gap.End - gap.Start + 1, "range-" + i);
                }
                spans.Sort(delegate(Span a, Span b) { return a.Start.CompareTo(b.Start); });
                if (FindGaps(spans, total).Count != 0) throw new Failure("MISSING_GAPS");
                string failed = Path.Combine(options.Output, "candidate.failed");
                byte[] buffer = new byte[1024 * 1024];
                using (FileStream candidate = new FileStream(failed, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    long position = 0;
                    foreach (Span span in spans)
                    {
                        if (span.End < position) continue;
                        if (span.Start > position) throw new Failure("MISSING_GAPS");
                        span.Stream.Position = position - span.Start;
                        long remaining = span.End - position + 1;
                        CopyExactly(span.Stream, candidate, remaining, buffer);
                        position = span.End + 1;
                    }
                    if (position != total || candidate.Length != total) throw new Failure("CANDIDATE_LENGTH_FAILED");
                    candidate.Flush(true);
                    candidate.Position = 0;
                    string checksum;
                    using (MD5 md5 = MD5.Create())
                        checksum = BitConverter.ToString(md5.ComputeHash(candidate)).Replace("-", "");
                    if (!string.Equals(checksum, options.Md5, StringComparison.OrdinalIgnoreCase))
                        throw new Failure("MD5_MISMATCH: candidate.failed is not a repaired file.");
                }
                File.Move(failed, Path.Combine(options.Output, "candidate.bin"));
                output.WriteLine("VERIFIED candidate.bin: expected MD5 matched. Original sources were not modified.");
                return 0;
            }
            catch (Failure ex) { output.WriteLine("FAILED " + ex.Message); return 1; }
            catch (Exception ex)
            {
                // Framework/network exceptions may embed signed URLs, paths or server bodies.
                output.WriteLine("FAILED " + ex.GetType().Name + " (details suppressed)");
                return 1;
            }
            finally { foreach (FileStream handle in handles) handle.Dispose(); }
        }

        private static Options Parse(string[] args)
        {
            Options o = new Options();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (key != "--patch" && !seen.Add(key)) throw new Failure("DUPLICATE_OPTION");
                if (key == "--plan") { o.Plan = true; continue; }
                if (key == "--recover") { o.Recover = true; continue; }
                if (++i >= args.Length) throw new Failure("MISSING_OPTION_VALUE");
                string value = args[i];
                switch (key)
                {
                    case "--dest": o.Dest = value; break;
                    case "--meta": o.Meta = value; break;
                    case "--parts": o.Parts = value; break;
                    case "--outputDir": o.Output = value; break;
                    case "--expected-md5": o.Md5 = value; break;
                    case "--link-file": o.LinkFile = value; break;
                    case "--cookie-file": o.CookieFile = value; break;
                    case "--patch": o.Patches.Add(value); break;
                    default: throw new Failure("UNKNOWN_OPTION");
                }
            }
            if (o.Plan == o.Recover || o.Dest == null || o.Meta == null || o.Parts == null ||
                o.Output == null || o.Md5 == null || !Regex.IsMatch(o.Md5, @"\A[0-9a-fA-F]{32}\z"))
                throw new Failure("USAGE: --plan|--recover --dest PATH --meta PATH --parts DIR --outputDir NEW_DIR --expected-md5 HEX32 [--patch START:END:PATH] [--link-file PATH] [--cookie-file PATH]");
            o.Dest = Canonical(o.Dest); o.Meta = Canonical(o.Meta);
            o.Parts = Canonical(o.Parts); o.Output = Canonical(o.Output);
            if (o.LinkFile != null) o.LinkFile = Canonical(o.LinkFile);
            if (o.CookieFile != null) o.CookieFile = Canonical(o.CookieFile);
            return o;
        }

        private static string Canonical(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0)
                throw new Failure("UNSAFE_PATH: local filesystem paths only; no device paths or alternate streams.");
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool Inside(string path, string directory)
        {
            return string.Equals(path, directory, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparse(string path)
        {
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new Failure("UNSAFE_PATH: reparse points are not allowed.");
            }
        }

        private static void ValidatePaths(Options o)
        {
            foreach (string path in new[] { o.Dest, o.Meta, o.Parts, o.Output, o.LinkFile, o.CookieFile })
                if (path != null) RejectReparse(path);
            string cache = Path.GetDirectoryName(o.Parts);
            if (File.Exists(o.Output) || Directory.Exists(o.Output) || Inside(o.Output, cache) ||
                Inside(o.Dest, o.Output) || Inside(o.Meta, o.Output) || Inside(o.Parts, o.Output))
                throw new Failure("UNSAFE_OUTPUT: output must be new and outside the source cache tree.");
            if (!Directory.Exists(Path.GetDirectoryName(o.Output))) throw new Failure("OUTPUT_PARENT_MISSING");
        }

        private static FileStream OpenSource(string path, List<FileStream> handles)
        {
            RejectReparse(path);
            FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            handles.Add(stream);
            return stream;
        }

        private static long Number(string text)
        {
            long number;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                throw new Failure("INVALID_METADATA_OR_RANGE");
            return number;
        }

        private static long ReadSources(Options o, List<FileStream> handles, List<Span> spans)
        {
            FileStream meta = OpenSource(o.Meta, handles);
            List<Span> partition = new List<Span>();
            long total;
            using (StreamReader reader = new StreamReader(meta, System.Text.Encoding.UTF8, true, 4096, true))
            {
                total = Number(reader.ReadLine() ?? "");
                long count = Number(reader.ReadLine() ?? "");
                if (total <= 0 || count <= 0 || count > 1000000) throw new Failure("INVALID_METADATA");
                for (int i = 0; i < count; i++)
                {
                    string[] fields = (reader.ReadLine() ?? "").Split(',');
                    if (fields.Length != 3) throw new Failure("INVALID_METADATA");
                    long start = Number(fields[0]), end = Number(fields[1]), written = Number(fields[2]);
                    if (start > end || end >= total) throw new Failure("INVALID_METADATA");
                    string name = "p" + i.ToString("D6", CultureInfo.InvariantCulture);
                    partition.Add(new Span { Start = start, End = end, Name = name });
                    string path = Path.Combine(o.Parts, name);
                    if (File.Exists(path))
                        AddSpan(spans, handles, path, start, Math.Min(written, end - start + 1), name);
                }
                while (!reader.EndOfStream)
                    if (!string.IsNullOrWhiteSpace(reader.ReadLine())) throw new Failure("INVALID_METADATA_EXTRA_ROWS");
            }
            partition.Sort(delegate(Span a, Span b) { return a.Start.CompareTo(b.Start); });
            long cursor = 0;
            foreach (Span row in partition)
            {
                if (row.Start != cursor) throw new Failure("INVALID_METADATA_PARTITION");
                cursor = row.End + 1;
            }
            if (cursor != total) throw new Failure("INVALID_METADATA_PARTITION");
            FileStream dest = OpenSource(o.Dest, handles);
            if (dest.Length > total) throw new Failure("DEST_EXCEEDS_TOTAL");
            if (dest.Length > 0)
                spans.Add(new Span { Start = 0, End = dest.Length - 1, Stream = dest, Name = "dest-prefix" });
            return total;
        }

        private static void AddSpan(List<Span> spans, List<FileStream> handles, string path, long start, long length, string name)
        {
            FileStream stream = OpenSource(path, handles);
            long actual = Math.Min(length, stream.Length);
            if (actual > 0) spans.Add(new Span { Start = start, End = start + actual - 1, Stream = stream, Name = name });
        }

        private static void AddPatch(string mapping, long total, List<FileStream> handles, List<Span> spans)
        {
            string[] fields = mapping.Split(new[] { ':' }, 3);
            if (fields.Length != 3) throw new Failure("INVALID_PATCH_MAPPING");
            long start = Number(fields[0]), end = Number(fields[1]);
            if (start > end || end >= total) throw new Failure("INVALID_PATCH_RANGE");
            FileStream stream = OpenSource(Canonical(fields[2]), handles);
            if (stream.Length != end - start + 1) throw new Failure("PATCH_LENGTH_MISMATCH");
            spans.Add(new Span { Start = start, End = end, Stream = stream, Name = "explicit-patch" });
        }

        private static List<Gap> FindGaps(List<Span> spans, long total)
        {
            List<Gap> gaps = new List<Gap>();
            long cursor = 0;
            foreach (Span span in spans)
            {
                if (span.Start > cursor) gaps.Add(new Gap { Start = cursor, End = span.Start - 1 });
                cursor = Math.Max(cursor, span.End + 1);
            }
            if (cursor < total) gaps.Add(new Gap { Start = cursor, End = total - 1 });
            return gaps;
        }

        private static void CompareOverlaps(List<Span> spans)
        {
            byte[] left = new byte[1024 * 1024], right = new byte[1024 * 1024];
            for (int i = 0; i < spans.Count; i++)
            {
                Span a = spans[i];
                for (int j = i + 1; j < spans.Count && spans[j].Start <= a.End; j++)
                {
                    Span b = spans[j];
                    long start = Math.Max(a.Start, b.Start);
                    long remaining = Math.Min(a.End, b.End) - start + 1;
                    a.Stream.Position = start - a.Start; b.Stream.Position = start - b.Start;
                    while (remaining > 0)
                    {
                        int size = (int)Math.Min(left.Length, remaining);
                        ReadExactly(a.Stream, left, size); ReadExactly(b.Stream, right, size);
                        for (int k = 0; k < size; k++)
                            if (left[k] != right[k]) throw new Failure("OVERLAP_MISMATCH: sources disagree; no candidate created.");
                        remaining -= size;
                    }
                }
            }
        }

        private static void ReadExactly(Stream source, byte[] buffer, int size)
        {
            int read = 0;
            while (read < size)
            {
                int amount = source.Read(buffer, read, size - read);
                if (amount == 0) throw new Failure("SOURCE_SHORT_READ");
                read += amount;
            }
        }

        private static void CopyExactly(Stream source, Stream target, long count, byte[] buffer)
        {
            while (count > 0)
            {
                int size = (int)Math.Min(count, buffer.Length);
                ReadExactly(source, buffer, size); target.Write(buffer, 0, size);
                count -= size;
            }
        }

        private static Uri ReadLink(string path, List<FileStream> handles)
        {
            string value = ReadSecretFile(path, handles);
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)))
                throw new Failure("INVALID_LINK_FILE: HTTPS required (HTTP allowed only on loopback).");
            return uri;
        }

        private static string ReadSecretFile(string path, List<FileStream> handles)
        {
            FileStream file = OpenSource(path, handles);
            if (file.Length > 65536) throw new Failure("INPUT_FILE_TOO_LARGE");
            using (StreamReader reader = new StreamReader(file, System.Text.Encoding.UTF8, true, 4096, true))
                return reader.ReadToEnd().Trim();
        }

        public static void FetchRange(Uri uri, string cookie, long start, long end, long total, string path)
        {
            if (uri == null || !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.Scheme == "https" || (uri.Scheme == "http" && uri.IsLoopback)) ||
                start < 0 || end < start || end >= total)
                throw new Failure("INVALID_RANGE_REQUEST");
            path = Canonical(path);
            RejectReparse(path);
            if (File.Exists(path) || Directory.Exists(path))
                throw new Failure("PATCH_ALREADY_EXISTS");
            DownloadRange(uri, cookie, new Gap { Start = start, End = end }, total, path);
        }

        private static void DownloadRange(Uri uri, string cookie, Gap gap, long total, string path)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Proxy = null;
            request.UserAgent = QuarkConstants.DlUa;
            request.Referer = QuarkConstants.Referer;
            request.Headers["Origin"] = QuarkConstants.Origin;
            if (!string.IsNullOrEmpty(cookie)) request.Headers["Cookie"] = cookie;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AddRange(gap.Start, gap.End);
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.PartialContent) throw new Failure("HTTP_REQUIRES_206");
                string expected = "bytes " + gap.Start.ToString(CultureInfo.InvariantCulture) + "-" +
                    gap.End.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(response.Headers["Content-Range"], expected, StringComparison.Ordinal) ||
                    !string.IsNullOrEmpty(response.Headers["Content-Encoding"]))
                    throw new Failure("HTTP_RANGE_OR_ENCODING_MISMATCH");
                long count = gap.End - gap.Start + 1;
                if (response.ContentLength >= 0 && response.ContentLength != count)
                    throw new Failure("HTTP_BODY_LENGTH_MISMATCH");
                using (Stream source = response.GetResponseStream())
                using (FileStream target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyExactly(source, target, count, new byte[1024 * 1024]);
                    if (source.ReadByte() != -1) throw new Failure("HTTP_BODY_LENGTH_MISMATCH");
                    target.Flush(true);
                }
            }
        }

        private static int Main(string[] args) { return Run(args, Console.Out); }
    }
}
