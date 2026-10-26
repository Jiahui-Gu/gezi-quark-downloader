using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GeZi;
using GeZi.Core.Download;
using GeZi.Core.Models;

namespace ResumeRegression
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            int failures = 0;
            Run("11 asynchronous share resumes stay on the WPF dispatcher", () =>
            {
                var h = new ResumeHarness();
                try { Pump(h.Resume(Tasks(11))); }
                catch
                {
                    Console.WriteLine("Resume failure snapshot: collection=" + h.Items.Count
                        + ", WPF view=" + h.View.Cast<object>().Count()
                        + ", scheduled=" + h.Started.Count + ", buttons ready=" + h.Ready);
                    throw;
                }
                Assert(h.Items.Count == 11, "all tasks must reach the collection");
                Assert(h.View.Cast<object>().Count() == 11, "all tasks must reach the WPF view");
                Assert(h.Started.Count == 11, "scheduler must receive all tasks");
                Assert(h.Started.All(j => j.Threads == 256), "each job must request 256 workers");
                Assert(h.Ready, "buttons must be re-enabled");
            }, ref failures);
            Run("drive and share resumes can be mixed", () =>
            {
                var h = new ResumeHarness();
                var tasks = Tasks(3);
                tasks[1].FromShare = false;
                Pump(h.Resume(tasks));
                Assert(h.Started.Count == 3 && h.Ready, "mixed batch must start and release buttons");
            }, ref failures);
            Run("one failed link does not prevent other resumes", () =>
            {
                var h = new ResumeHarness { FailedFid = "1" };
                Pump(h.Resume(Tasks(3)));
                Assert(h.Started.Count == 2 && h.Ready, "remaining tasks must start");
                Assert(h.Logs.Any(l => l.Contains("link failure")), "link error must be visible");
            }, ref failures);
            Run("batch startup errors are observed and visible", () =>
            {
                var h = new ResumeHarness { FailStart = true };
                Pump(h.Resume(Tasks(1)));
                Assert(h.Logs.Any(l => l.Contains("start failure")), "batch failure must be logged");
                Assert(h.Ready, "startup failure must release buttons");
            }, ref failures);
            Run("all failed links release buttons without starting jobs", () =>
            {
                var h = new ResumeHarness { FailedFid = "0" };
                Pump(h.Resume(Tasks(1)));
                Assert(h.Started.Count == 0 && h.Ready, "failed batch must not start");
            }, ref failures);
            if (args.Contains("--download"))
                Run("11 files resume at 256 real concurrent HTTP ranges each",
                    () => Pump(DownloadRegression.RunAsync()), ref failures);
            Console.WriteLine("Failures: " + failures);
            return failures == 0 ? 0 : 1;
        }

        private static List<PendingTask> Tasks(int count)
        {
            return Enumerable.Range(0, count).Select(i => new PendingTask
            {
                Name = "synthetic-" + i, Fid = i.ToString(), FromShare = true,
                Dest = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "synthetic-" + i + ".bin"),
            }).ToList();
        }

        internal static void Pump(Task task)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() => frame.Continue = false)),
                CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }

        internal static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Run(string name, Action test, ref int failures)
        {
            try { test(); Console.WriteLine("PASS: " + name); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL: " + name + " -- " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    internal partial class ResumeHarness : DispatcherObject
    {
        private readonly ObservableCollection<TaskItem> _tasks = new ObservableCollection<TaskItem>();
        private readonly AppSettings _settings = new AppSettings { Threads = 256, ConcurrentTasks = 11 };
        private readonly HashSet<Button> _busyButtons = new HashSet<Button>();
        private readonly Button ParseBtn = new Button();
        private readonly Button DownloadSelBtn = new Button();
        private readonly Button DownloadAllBtn = new Button();
        private readonly Button DriveRefreshBtn = new Button();
        private readonly Button DriveUpBtn = new Button();
        private readonly Button FetchLinksBtn = new Button();
        internal readonly List<DownloadJob> Started = new List<DownloadJob>();
        internal readonly List<string> Logs = new List<string>();
        internal readonly System.ComponentModel.ICollectionView View;
        internal bool FailStart;
        internal string FailedFid;

        internal ResumeHarness()
        {
            View = CollectionViewSource.GetDefaultView(_tasks);
            View.Filter = _ => true;
        }

        internal IReadOnlyCollection<TaskItem> Items => _tasks;
        internal bool Ready => ParseBtn.IsEnabled && DownloadSelBtn.IsEnabled
            && DownloadAllBtn.IsEnabled && DriveRefreshBtn.IsEnabled
            && DriveUpBtn.IsEnabled && FetchLinksBtn.IsEnabled;
        internal Task Resume(List<PendingTask> tasks) => ResumePendingBatchAsync(tasks);
        private Task<QuarkClient> EnsureClientForResumeAsync() => Task.FromResult(new QuarkClient());
        private async Task<string> GetShareLinkWithReResolveAsync(QuarkClient client,
            string fid, string token, string pwdId, string stoken, string passcode,
            string startFid, string shareRelDir, Action<string, string> refreshed, CancellationToken ct)
        {
            await Task.Delay(20).ConfigureAwait(false);
            if (fid == FailedFid) throw new IOException("link failure");
            return "http://127.0.0.1/synthetic/" + fid;
        }
        private Task<string> RefreshLinkAsyncForPending(DownloadJob job, CancellationToken ct)
            => Task.FromResult(job.Url);
        private void RunJobs(List<DownloadJob> jobs)
        {
            Dispatcher.VerifyAccess();
            if (FailStart) throw new IOException("start failure");
            Started.AddRange(jobs);
        }
        private void Log(string message) { lock (Logs) Logs.Add(message); }
    }

    internal sealed class QuarkClient
    {
        internal string CookieStr => "";
        internal async Task<List<DownloadLink>> GetDownloadAsync(List<string> fids)
        {
            await Task.Delay(20).ConfigureAwait(false);
            return new List<DownloadLink> { new DownloadLink { DownloadUrl = "http://127.0.0.1/synthetic" } };
        }
    }

    internal static class AppDialog
    {
        internal static void Show(object owner, string text, string title,
            MessageBoxButton buttons, MessageBoxImage image) { }
    }
}
