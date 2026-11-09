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
            if (args.Length == 2 && (args[0] == "--state-write" || args[0] == "--state-read"))
                return FailedRetryRegression.PersistenceChild(args);
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            int failures = 0;
            Run("every production job entry uses 512 independently of size and task count", () =>
            {
                foreach (int concurrent in new[] { 1, 3, 11 })
                {
                    var h = new ResumeHarness();
                    h.Configure(512, concurrent);
                    foreach (var sizes in new[] {
                        new long[] { 9L << 30, 9L << 30, 9L << 30 },
                        new long[] { 1, 1L << 20, 100L << 30 },
                        new long[] { 0, -1, 0, 5L << 30 } })
                        foreach (var entry in new Func<IList<long>, int[]>[] { h.Entry0, h.Entry1, h.Entry2 })
                            Assert(entry(sizes).All(t => t == 512),
                                entry.Method.Name + " must not allocate a shared connection budget");
                    Pump(h.Resume(Tasks(3)));
                    Assert(h.Started.All(j => j.Threads == 512), "startup resume must use the same setting");
                }
            }, ref failures);
            Run("no hidden startup or adaptive worker limit", () =>
            {
                Assert(typeof(SegmentedDownloader).GetNestedType("SlowStartGate",
                    System.Reflection.BindingFlags.NonPublic) == null, "response-batched startup must be removed");
                Assert(!typeof(SegmentedDownloader).GetNestedTypes(System.Reflection.BindingFlags.NonPublic)
                    .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    .Any(f => f.Name == "concurrencyTarget"), "adaptive worker ceiling must be removed");
            }, ref failures);
            Run("synthetic saved configuration preserves 512 and simultaneous count", () =>
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                var pathField = typeof(SettingsStore).GetField("_filePath", flags);
                var loadedField = typeof(SettingsStore).GetField("_loaded", flags);
                object oldPath = pathField.GetValue(null), oldLoaded = loadedField.GetValue(null);
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "settings-fixture-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "GeZi.config.xml");
                pathField.SetValue(null, path);
                loadedField.SetValue(null, true);
                try
                {
                    foreach (int concurrent in new[] { 3, 1 })
                    {
                        SettingsStore.Save(new AppSettings { Threads = 512, ConcurrentTasks = concurrent });
                        Assert(SettingsStore.LastError == null && File.Exists(path), "synthetic configuration saved");
                        var settings = SettingsStore.Load();
                        Assert(SettingsStore.LastError == null && settings.Threads == 512
                            && settings.ConcurrentTasks == concurrent, "saved settings round trip");
                    }
                }
                finally
                {
                    pathField.SetValue(null, oldPath);
                    loadedField.SetValue(null, oldLoaded);
                    Directory.Delete(root, true);
                }
            }, ref failures);
            Run("both speed aggregations exclude local merging (16 assertions)",
                MaintenanceRegression.Speed, ref failures);
            Run("maintained fork version and release parsing",
                MaintenanceRegression.Release, ref failures);
            Run("known pending size survives queued and unknown progress",
                PendingSizeRegression.PreserveKnown, ref failures);
            Run("authoritative total backfills size without per-frame persistence",
                PendingSizeRegression.Backfill, ref failures);
            Run("pending signature distinguishes same-name destinations",
                PendingSizeRegression.Identity, ref failures);
            Run("failed recovery identity remains durable until explicit cancellation",
                PendingSizeRegression.FailedRetention, ref failures);
            Run("in-app retry preserves exact row and rejects duplicate or stale execution",
                FailedRetryRegression.ExactIdentity, ref failures);
            Run("in-app retry link failure preserves Failed recovery context",
                FailedRetryRegression.LinkFailure, ref failures);
            Run("cancel or removal during link acquisition prevents retry resurrection",
                FailedRetryRegression.CancelOrRemoveDuringLink, ref failures);
            Run("retry rejects already active destination",
                FailedRetryRegression.ActiveDestination, ref failures);
            Run("failed source fid is not auto-cleaned before retry",
                FailedRetryRegression.TransferRetention, ref failures);
            Run("failed recovery record round-trips and resumes original cache root",
                FailedRetryRegression.RestartRecord, ref failures);
            Run("startup retains unselected records and failed link acquisitions",
                FailedRetryRegression.StartupRetainsUnselectedAndFailedLinks, ref failures);
            Run("real Failed state survives disk write and separate-process restart",
                FailedRetryRegression.RealDiskRestart, ref failures);
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
            {
                Run("out-of-order split plan preserves merged logical prefixes",
                    () => Pump(DownloadRegression.RunSplitPlanAsync(false)), ref failures);
                Run("missing partial-head cache fails without truncating source bytes",
                    () => Pump(DownloadRegression.RunSplitPlanAsync(true)), ref failures);
                Run("real worker-created split plan survives failure and restart",
                    () => Pump(DownloadRegression.RunDynamicSplitAsync()), ref failures);
                Run("11 files resume at 256 real concurrent HTTP ranges each",
                    () => Pump(DownloadRegression.RunAsync()), ref failures);
                Run("three files resume at 512 real simultaneous ranges each",
                    () => Pump(DownloadRegression.RunAsync(3, 512, 3)), ref failures);
                Run("simultaneous setting one serializes three 512-worker files",
                    () => Pump(DownloadRegression.RunAsync(3, 512, 1)), ref failures);
                Run("three fresh unequal-size files start 512 ranges before any segment response",
                    () => Pump(DownloadRegression.RunAsync(3, 512, 3, fresh: true)), ref failures);
                Run("HTTP 429 retries retain configured workers and complete intact",
                    () => Pump(DownloadRegression.RunAsync(3, 64, 3, throttle: true)), ref failures);
            }
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
        private string _pendingSig;
        private bool _restoringPending;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot> _diagLatest
            = new System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticsSnapshot>();
        internal void Diagnose(DiagnosticsSnapshot snapshot) { OnDiagnostics(snapshot); }
        private readonly List<string> _pendingCleanupFids = new List<string>();
        private readonly object _cleanupLock = new object();
        private readonly QuarkClient _client = new QuarkClient();

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
        internal void Add(TaskItem item) => _tasks.Add(item);
        internal void Remove(TaskItem item) => _tasks.Remove(item);
        internal void Sync() => SyncPending();
        internal void Update(JobUpdate update) => OnJobUpdate(update);
        internal Task Cleanup(string fid)
        {
            if (!_pendingCleanupFids.Contains(fid)) _pendingCleanupFids.Add(fid);
            _settings.AutoClean = true;
            return CleanupTransferredAsync();
        }
        internal IReadOnlyList<string> Deleted => _client.Deleted;
        private void UpdatePauseAllButton() { }
        internal void Configure(int threads, int concurrent)
        {
            _settings.Threads = threads;
            _settings.ConcurrentTasks = concurrent;
        }
        private void LogDetail(string message) => Log(message);
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
        internal void ConfigureCache(string root) { _settings.PartsRoot = root; }

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
        internal readonly List<string> Deleted = new List<string>();
        internal Task DeleteAsync(List<string> fids) { Deleted.AddRange(fids); return Task.CompletedTask; }
        internal Task PurgeRecycleAsync(List<string> fids) => Task.CompletedTask;
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
