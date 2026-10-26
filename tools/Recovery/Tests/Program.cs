using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using GeZi.Recovery;

internal static class Tests
{
    private static int failures;
    private static string root;
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("abcdefghijklmnop");

    private static void Main()
    {
        root = Path.Combine(Path.GetDirectoryName(typeof(Tests).Assembly.Location), "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Test("plan is read-only and reports logical gap", delegate {
                Fixture f = NewFixture();
                string text; int code = Run(f, out text, "--plan");
                Check(code == 0 && text.Contains("GAP 6-7") && !Directory.Exists(f.Output), text);
            });
            Test("missing gap refuses candidate", delegate {
                Fixture f = NewFixture(); string text;
                Check(Run(f, out text, "--recover") != 0 && !Directory.Exists(f.Output), text);
            });
            Test("patch recovery and source immutability", delegate {
                Fixture f = NewFixture(); string text;
                string before = Snapshot(f);
                Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) == 0, text);
                Check(BytesEqual(File.ReadAllBytes(Path.Combine(f.Output, "candidate.bin")), Data), "candidate bytes");
                Check(before == Snapshot(f), "sources changed");
            });
            Test("unordered metadata preserves original p-index", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllText(f.Meta, "16\n3\n8,15,8\n0,3,4\n4,7,2\n");
                File.WriteAllBytes(Path.Combine(f.Parts, "p000000"), Slice(8, 8));
                File.WriteAllBytes(Path.Combine(f.Parts, "p000001"), Slice(0, 4));
                File.WriteAllBytes(Path.Combine(f.Parts, "p000002"), Slice(4, 2));
                Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) == 0, text);
            });
            Test("overlap mismatch rejects before writing", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllBytes(f.Dest, Encoding.ASCII.GetBytes("abcXef"));
                Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) != 0 && !Directory.Exists(f.Output), text);
            });
            Test("patch overlap mismatch rejects", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllBytes(f.Patch, Encoding.ASCII.GetBytes("Xgh"));
                Check(Run(f, out text, "--recover", "--patch", "5:7:" + f.Patch) != 0 && !Directory.Exists(f.Output), text);
            });
            Test("wrong MD5 remains explicitly failed", delegate {
                Fixture f = NewFixture(); string text;
                f.Md5 = new string('0', 32);
                Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) != 0, text);
                Check(!File.Exists(Path.Combine(f.Output, "candidate.bin")) &&
                    File.Exists(Path.Combine(f.Output, "candidate.failed")), "failed candidate status");
            });
            Test("output aliases are rejected", delegate {
                foreach (string alias in new[] { "parts", "dest", "existing", "ancestor" })
                {
                    Fixture f = NewFixture(); string text;
                    f.Output = alias == "parts" ? Path.Combine(f.Parts, "..", "parts", "new") :
                        alias == "dest" ? f.Dest : alias == "ancestor" ? root : f.Directory;
                    Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) != 0, text);
                }
            });
            Test("W and physical lengths cap coverage without padding", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllBytes(Path.Combine(f.Parts, "p000001"), Slice(4, 4));
                Check(Run(f, out text, "--plan") == 0 && text.Contains("GAP 6-7"), text);
                File.WriteAllText(f.Meta, "16\n3\n0,3,4\n4,7,4\n8,15,8\n");
                File.WriteAllBytes(Path.Combine(f.Parts, "p000001"), Slice(4, 1));
                File.WriteAllBytes(f.Dest, Slice(0, 4));
                Check(Run(f, out text, "--plan") == 0 && text.Contains("GAP 5-7"), text);
            });
            Test("invalid metadata partition rejects", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllText(f.Meta, "16\n2\n0,7,4\n7,15,8\n");
                Check(Run(f, out text, "--plan") != 0, text);
            });
            Test("short patch refuses output", delegate {
                Fixture f = NewFixture(); string text;
                File.WriteAllBytes(f.Patch, new byte[] { 1 });
                Check(Run(f, out text, "--recover", "--patch", "6:7:" + f.Patch) != 0 && !Directory.Exists(f.Output), text);
            });
            Test("exact localhost Range recovers only gap", delegate { HttpCase(206, "bytes 6-7/16", "gh", true); });
            Test("HTTP 200 rejects full-body response", delegate { HttpCase(200, null, "abcdefghijklmnop", false); });
            Test("wrong Content-Range rejects", delegate { HttpCase(206, "bytes 5-6/16", "gh", false); });
            Test("short HTTP body rejects", delegate { HttpCase(206, "bytes 6-7/16", "g", false); });
            Test("extra HTTP body rejects", delegate { HttpCase(206, "bytes 6-7/16", "ghi", false); });
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("RESULT: " + (16 - failures) + "/16 passed");
        Environment.ExitCode = failures == 0 ? 0 : 1;
    }

    private static void HttpCase(int status, string range, string body, bool success)
    {
        Fixture f = NewFixture();
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string request = null;
        Thread server = new Thread(delegate() {
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    StringBuilder header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n"))
                    {
                        int c = stream.ReadByte(); if (c < 0) break;
                        header.Append((char)c);
                    }
                    request = header.ToString();
                    string response = "HTTP/1.1 " + status + " Test\r\nConnection: close\r\nContent-Length: " + body.Length +
                        "\r\n" + (range == null ? "" : "Content-Range: " + range + "\r\n") + "\r\n" + body;
                    byte[] bytes = Encoding.ASCII.GetBytes(response); stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
        });
        server.IsBackground = true; server.Start();
        string link = Path.Combine(f.Directory, "link.txt");
        File.WriteAllText(link, "http://127.0.0.1:" + port + "/signed?secret=DO_NOT_LOG");
        string cookie = Path.Combine(f.Directory, "cookie.txt");
        File.WriteAllText(cookie, "synthetic=COOKIE_DO_NOT_LOG");
        string before = Snapshot(f);
        string text;
        int code = Run(f, out text, "--recover", "--link-file", link, "--cookie-file", cookie);
        listener.Stop(); server.Join(6000);
        Check(success ? code == 0 : code != 0, text);
        Check(request != null && request.Contains("Range: bytes=6-7"), "exact Range request missing");
        Check(request.Contains("quark-cloud-drive/3.14.2") &&
            request.Contains("Referer: https://pan.quark.cn/") &&
            request.Contains("Origin: https://pan.quark.cn") &&
            request.Contains("Cookie: synthetic=COOKIE_DO_NOT_LOG"), "download headers missing");
        Check(!text.Contains("DO_NOT_LOG") && !text.Contains("signed"), "URL leaked");
        Check(before == Snapshot(f), "HTTP changed sources");
        Check(success == File.Exists(Path.Combine(f.Output, "candidate.bin")), "candidate publication");
    }

    private static Fixture NewFixture()
    {
        Fixture f = new Fixture();
        f.Directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        f.Parts = Path.Combine(f.Directory, "cache", "parts");
        Directory.CreateDirectory(f.Parts);
        f.Meta = Path.Combine(f.Directory, "cache", "meta.json");
        f.Dest = Path.Combine(f.Directory, "original.bin");
        f.Output = Path.Combine(f.Directory, "candidate-new");
        f.Patch = Path.Combine(f.Directory, "patch.bin");
        File.WriteAllText(f.Meta, "16\n3\n0,3,4\n4,7,2\n8,15,8\n");
        File.WriteAllBytes(f.Dest, Slice(0, 6));
        File.WriteAllBytes(Path.Combine(f.Parts, "p000000"), Slice(0, 4));
        File.WriteAllBytes(Path.Combine(f.Parts, "p000001"), Slice(4, 2));
        File.WriteAllBytes(Path.Combine(f.Parts, "p000002"), Slice(8, 8));
        File.WriteAllBytes(f.Patch, Slice(6, 2));
        using (MD5 md5 = MD5.Create()) { f.Md5 = BitConverter.ToString(md5.ComputeHash(Data)).Replace("-", ""); }
        return f;
    }

    private static int Run(Fixture f, out string text, params string[] extra)
    {
        string[] args = new string[10 + extra.Length];
        string[] basic = { "--dest", f.Dest, "--meta", f.Meta, "--parts", f.Parts, "--outputDir", f.Output, "--expected-md5", f.Md5 };
        basic.CopyTo(args, 0); extra.CopyTo(args, 10);
        StringWriter writer = new StringWriter();
        int result = Recovery.Run(args, writer); text = writer.ToString(); return result;
    }

    private static string Snapshot(Fixture f)
    {
        StringBuilder result = new StringBuilder();
        foreach (string path in new[] { f.Dest, f.Meta, Path.Combine(f.Parts, "p000000"), Path.Combine(f.Parts, "p000001"), Path.Combine(f.Parts, "p000002"), f.Patch })
            result.Append(Convert.ToBase64String(File.ReadAllBytes(path))).Append("|");
        return result.ToString();
    }
    private static byte[] Slice(int start, int count) { byte[] data = new byte[count]; Array.Copy(Data, start, data, 0, count); return data; }
    private static bool BytesEqual(byte[] a, byte[] b) { return Convert.ToBase64String(a) == Convert.ToBase64String(b); }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Test(string name, Action body)
    {
        try { body(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private sealed class Fixture { public string Directory, Dest, Meta, Parts, Output, Patch, Md5; }
}
