using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using GeZi.Core.Api;
using GeZi.Core.Models;
using GeZi.Core.Support;

internal static class LiveGaps
{
    private static string phase = "inputs";
    private static int reports;
    private static readonly int[] Numbers = { 3, 4, 14, 16 };
    private static readonly long[] Starts = { 1157762616L, 1158663371L, 2081890919L, 1913538391L };
    private static readonly long[] Ends = { 1159688271L, 1159688271L, 2089961854L, 1917396196L };
    private const long Total = 4294967296L;

    private static string Numeric(object value)
    {
        long number;
        return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number)
            ? number.ToString(CultureInfo.InvariantCulture) : "none";
    }

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.FirstChanceException += delegate(object sender,
            System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            var api = e.Exception as QuarkApiException;
            if (api != null && reports++ < 24)
                Console.WriteLine("API_STATUS phase=" + phase + " http=" + Numeric(api.HttpStatus) +
                    " code=" + Numeric(api.Code) + " status=" + Numeric(api.Status));
        };
        try { Run(args).GetAwaiter().GetResult(); return 0; }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED phase=" + phase + " type=" + ex.GetType().Name +
                "; no response bodies, credentials or URLs logged");
            return 1;
        }
    }

    private static async Task Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("config state share output-directory");
        string output = Path.GetFullPath(args[3]);
        if (Directory.Exists(output) || File.Exists(output) ||
            !Directory.Exists(Path.GetDirectoryName(output)))
            throw new InvalidOperationException("new output directory required");
        var xml = new XmlDocument { XmlResolver = null };
        using (var input = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read))
            xml.Load(input);
        XmlNode node = xml.DocumentElement.SelectSingleNode("Cookie");
        string cookie = SecretProtector.Unprotect(node == null ? "" : node.InnerText);
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Contains("\r") || cookie.Contains("\n"))
            throw new InvalidOperationException("cookie unavailable");
        var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
        Dictionary<string, object> state;
        using (var reader = new StreamReader(new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read)))
            state = serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
        var pending = ((IEnumerable)state["Pending"]).Cast<Dictionary<string, object>>().ToList();
        var share = ShareUrlParser.Parse(args[2]);
        if (string.IsNullOrEmpty(share.PwdId) || share.StartFid == "0")
            throw new InvalidOperationException("explicit share folder required");
        using (var client = new QuarkClient(cookie))
        {
            phase = "production-token";
            string stoken = await client.GetStokenAsync(share.PwdId, share.Passcode);
            if (string.IsNullOrEmpty(stoken)) throw new InvalidOperationException("no stoken");
            Console.WriteLine("STATUS token=ok");
            phase = "production-directory";
            var entries = await client.ShareDetailAsync(share.PwdId, stoken, share.StartFid);
            Console.WriteLine("STATUS directory=ok");
            for (int i = 0; i < Numbers.Length; i++)
            {
                string name = "COD23SP-" + Numbers[i] + ".bin";
                phase = "identity-" + Numbers[i];
                var original = pending.Single(p => Convert.ToString(p["Name"]) == name);
                string fid = Convert.ToString(original["Fid"]);
                var file = entries.Single(e => e.Fid == fid && e.Name == name &&
                    !e.IsDir && e.Size == Total && !string.IsNullOrEmpty(e.Token));
                phase = "production-link-" + Numbers[i];
                List<DownloadLink> links = await client.GetShareDownloadAsync(
                    new[] { file.Fid }, new[] { file.Token }, share.PwdId, stoken);
                var link = links.Single(l => l.Fid == file.Fid);
                var uri = new Uri(link.DownloadUrl);
                if (uri.Scheme != "https") throw new InvalidOperationException("HTTPS required");
                Console.WriteLine("STATUS link=" + Numbers[i] + " ready");
                if (!Directory.Exists(output)) Directory.CreateDirectory(output);
                string filename = name + "." + Starts[i] + "-" + Ends[i] + ".range";
                string path = Path.Combine(output, filename);
                string partial = path + ".partial";
                phase = "strict-range-" + Numbers[i];
                string downloadCookie = uri.Host.EndsWith(".quark.cn", StringComparison.OrdinalIgnoreCase)
                    ? client.CookieStr : null;
                GeZi.Recovery.Recovery.FetchRange(uri, downloadCookie, Starts[i], Ends[i], Total, partial);
                File.Move(partial, path);
                string hash;
                using (var md5 = SHA256.Create())
                using (var input = File.OpenRead(path))
                    hash = BitConverter.ToString(md5.ComputeHash(input)).Replace("-", "");
                Console.WriteLine("FETCHED file=" + name + " bytes=" + (Ends[i] - Starts[i] + 1) +
                    " SHA256=" + hash);
            }
            Console.WriteLine("STATUS all-four-gaps=verified-206 total-bytes=14879299");
        }
    }
}
