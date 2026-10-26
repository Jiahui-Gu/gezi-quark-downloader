using System;
using System.Collections.Generic;
using System.Reflection;
using GeZi;
using GeZi.Core.Download;
using GeZi.Core.Update;

namespace ResumeRegression
{
    internal static partial class MaintenanceRegression
    {
        internal static void Speed()
        {
            var cases = new[]
            {
                new TaskItem[0],
                new[] { Item(JobState.Downloading, "", 100) },
                new[] { Item(JobState.Downloading, "merging", 900) },
                new[] { Item(JobState.Downloading, "", 100), Item(JobState.Downloading, "merging", 900) },
                new[] { Item(JobState.Downloading, null, 200), Item(JobState.Downloading, "", 100) },
                new[] { Item(JobState.Queued, "", 0), Item(JobState.Paused, "", 0), Item(JobState.Cancelling, "", 0) },
                new[] { Item(JobState.Completed, "", 800), Item(JobState.Failed, "", 700) },
                new[] { Item(JobState.Downloading, "", 0), Item(JobState.Paused, "merging", 900) },
            };
            var expected = new double[] { 0, 100, 0, 100, 300, 0, 0, 0 };
            foreach (Func<IEnumerable<TaskItem>, double> sum in new Func<IEnumerable<TaskItem>, double>[] { MainWindow, MiniWindow })
                for (int i = 0; i < cases.Length; i++)
                    Program.Assert(sum(cases[i]) == expected[i], sum.Method.Name + " scenario " + i);
        }

        private static TaskItem Item(JobState state, string phase, double speed)
            => new TaskItem(null, "synthetic") { State = state, Phase = phase, Speed = speed };

        internal static void Release()
        {
            const string repo = "https://github.com/Jiahui-Gu/gezi-quark-downloader";
            Program.Assert(GitHubReleaseChecker.RepoOwner == "Jiahui-Gu", "update owner must be personal fork");
            Program.Assert(GitHubReleaseChecker.ReleasesPageUrl == repo + "/releases", "manual releases entry");
            Program.Assert(GitHubReleaseChecker.LatestApiUrl ==
                "https://api.github.com/repos/Jiahui-Gu/gezi-quark-downloader/releases/latest", "latest API entry");
            foreach (var assembly in new[] { typeof(TaskItem).Assembly, typeof(GitHubReleaseChecker).Assembly })
                Program.Assert(assembly.GetName().Version.ToString(3) == "2.3.2", "assembly version: " + assembly.GetName().Name);
            var aboutRepo = typeof(AboutWindow).GetField("RepoUrl", BindingFlags.NonPublic | BindingFlags.Static);
            Program.Assert((string)aboutRepo.GetRawConstantValue() == repo, "about source entry");
            var parse = typeof(GitHubReleaseChecker).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static);
            var release = (GitHubRelease)parse.Invoke(null, new object[] {
                "{\"tag_name\":\"v2.3.2\",\"html_url\":\"" + repo + "/releases/tag/v2.3.2\"," +
                "\"assets\":[{\"name\":\"GeZi-v2.3.2-source.zip\",\"size\":7}," +
                "{\"name\":\"GeZi-v2.3.2-win64.zip\",\"size\":12345}]}" });
            Program.Assert(release != null && release.Version.ToString() == "2.3.2", "numeric release tag");
            Program.Assert(release.AssetSize == 12345, "updater must recognize portable filename, not source archive");
            Program.Assert(release.HtmlUrl == repo + "/releases/tag/v2.3.2", "release navigation");
        }
    }
}
