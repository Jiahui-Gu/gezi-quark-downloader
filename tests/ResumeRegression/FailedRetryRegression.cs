using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Diagnostics;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using GeZi;
using GeZi.Core.Download;

namespace ResumeRegression
{
    internal static class FailedRetryRegression
    {
        private static Task Retry(ResumeHarness harness, params TaskItem[] items)
        {
            var method = typeof(ResumeHarness).GetMethod("RetryFailedTasksAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Program.Assert(method != null, "GUI needs an in-process Failed retry path, not an application restart");
            return (Task)method.Invoke(harness, new object[] { items.ToList() });
        }

        private static TaskItem Failed(ResumeHarness h, string fid = "retry")
        {
            var job = new DownloadJob
            {
                Name = "synthetic.bin", Dest = @"C:\synthetic-only\retry.bin",
                PartsRoot = @"C:\synthetic-only\original-cache",
                Pending = new PendingInfo { Fid = fid, Dest = @"C:\synthetic-only\retry.bin",
                    Size = 12345678, FromShare = true, Token = "synthetic-token",
                    PwdId = "synthetic-share", Stoken = "synthetic-stoken" }
            };
            var item = new TaskItem(job, job.Name) { State = JobState.Failed, Done = 1234,
                Error = "synthetic EOF", HistoryRecorded = true };
            h.Add(item);
            return item;
        }

        internal static void ExactIdentity()
        {
            var h = new ResumeHarness();
            var item = Failed(h);
            var old = item.Job;
            var released = new TaskCompletionSource<bool>();
            typeof(DownloadJob).GetProperty("Completion").SetValue(old, released.Task);
            Task first = Retry(h, item);
            Task duplicate = Retry(h, item);
            Program.Assert(h.Started.Count == 0 && !first.IsCompleted,
                "retry must wait until the old execution releases its destination");
            released.SetResult(true);
            Program.Pump(Task.WhenAll(first, duplicate));
            Program.Assert(h.Started.Count == 1 && h.Items.Count == 1
                && ReferenceEquals(h.Items.Single(), item), "double click must retry exactly once in the original row");
            var job = h.Started.Single();
            Program.Assert(!ReferenceEquals(job, old) && ReferenceEquals(item.Job, job),
                "new cancellation/execution identity must replace the old execution only");
            Program.Assert(job.Dest == old.Dest && job.PartsRoot == old.PartsRoot
                && job.Pending.Fid == old.Pending.Fid && job.Pending.Size == 12345678
                && item.Done == 1234 && !item.HistoryRecorded, "retry must preserve exact identity, cache and committed progress");
            h.Update(new JobUpdate { Job = old, State = JobState.Failed, Error = "stale" });
            Program.Assert(item.Error != "stale" && item.State == JobState.Queued,
                "late callback from old job must not corrupt retried state");
            var execution = typeof(DiagnosticsSnapshot).GetProperty("JobId");
            Program.Assert(execution != null, "late diagnostics must also identify their original execution");
            var snapshot = new DiagnosticsSnapshot { Name = job.Name, Dest = job.Dest,
                ActiveConnections = 512, WorkerCount = 512 };
            execution.SetValue(snapshot, old.Id);
            h.Diagnose(snapshot);
            Program.Assert(item.ActiveConnections == 0, "queued diagnostics from an old execution cannot fake new body activity");
            execution.SetValue(snapshot, job.Id);
            h.Diagnose(snapshot);
            Program.Assert(item.ActiveConnections == 512, "the current execution's diagnostic callback still updates its row");
        }

        internal static void LinkFailure()
        {
            var h = new ResumeHarness { FailedFid = "retry" };
            var item = Failed(h);
            var old = item.Job;
            Program.Pump(Retry(h, item));
            Program.Assert(h.Started.Count == 0 && ReferenceEquals(item.Job, old)
                && item.State == JobState.Failed && HistoryStore.Last.Single().Size == 12345678,
                "link failure must stay visible and durable, not replace the row or erase context");
        }

        internal static void CancelOrRemoveDuringLink()
        {
            foreach (bool remove in new[] { false, true })
            {
                var h = new ResumeHarness();
                var item = Failed(h);
                Task task = Retry(h, item);
                item.Job.CancelJob();
                if (remove) h.Remove(item);
                else item.State = JobState.Cancelled;
                Program.Pump(task);
                Program.Assert(h.Started.Count == 0,
                    "cancelled/deleted pending link callback must never resurrect a job");
            }
        }

        internal static void ActiveDestination()
        {
            var invalid = new ResumeHarness();
            var bad = Failed(invalid);
            bad.Job.Dest = "invalid\0destination";
            Program.Pump(Retry(invalid, bad));
            Program.Assert(invalid.Started.Count == 0 && bad.State == JobState.Failed
                && bad.Error.Contains("路径"), "invalid restored destinations fail visibly rather than crash the UI");
            foreach (bool duringFetch in new[] { false, true })
            {
                var h = new ResumeHarness();
                var item = Failed(h);
                Task retry = duringFetch ? Retry(h, item) : null;
                h.Add(new TaskItem(new DownloadJob { Dest = item.Job.Dest }, "active")
                    { State = JobState.Downloading });
                Program.Pump(retry ?? Retry(h, item));
                Program.Assert(h.Started.Count == 0 && item.State == JobState.Failed,
                    "a destination active before or during fresh-link acquisition cannot re-enter");
            }
        }

        internal static void StartupRetainsUnselectedAndFailedLinks()
        {
            var h = new ResumeHarness { FailedFid = "one" };
            var records = new List<PendingTask>
            {
                new PendingTask { Name = "one.bin", Fid = "one", Dest = @"C:\synthetic-only\one.bin",
                    Size = 100, FromShare = true, Token = "t", PwdId = "p", Stoken = "s", PartsRoot = "" },
                new PendingTask { Name = "two.bin", Fid = "two", Dest = @"C:\synthetic-only\two.bin", Size = 200 }
            };
            var method = typeof(ResumeHarness).GetMethod("RestorePendingTasks",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Program.Assert(method != null, "startup must retain pending rows even when not selected or fresh-link acquisition fails");
            var restored = (Dictionary<PendingTask, TaskItem>)method.Invoke(h, new object[] { records });
            Program.Pump(Retry(h, restored[records[0]]));
            h.Sync();
            Program.Assert(HistoryStore.Last.Count == 2 && h.Items.Count == 2 && h.Started.Count == 0,
                "failed chosen link and unselected record must both survive startup and subsequent persistence");
        }

        internal static int PersistenceChild(string[] args)
        {
            try
            {
                string root = Path.GetFullPath(args[1]);
                string prefix = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "retry-state-");
                Program.Assert(root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
                    "persistence probe must remain inside its isolated synthetic fixture");
                typeof(SettingsStore).GetField("_filePath", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, Path.Combine(root, "GeZi.config.xml"));
                typeof(SettingsStore).GetField("_loaded", BindingFlags.Static | BindingFlags.NonPublic)
                    .SetValue(null, true);
                var store = typeof(TaskItem).Assembly.GetType("GeZi.HistoryStore", true);
                string state = (string)store.GetProperty("StatePath", BindingFlags.Static | BindingFlags.NonPublic)
                    .GetValue(null);
                Program.Assert(state == Path.Combine(root, "GeZi.state.json"), "never use real application state");
                if (args[0] == "--state-write")
                    store.GetMethod("SetPending").Invoke(null, new object[] { new List<PendingTask> {
                        new PendingTask { Name = "synthetic.bin", Status = "Failed", Fid = "exact-synthetic-fid",
                            Size = 12345678, Dest = Path.Combine(root, "synthetic.bin"),
                            PartsRoot = Path.Combine(root, "original-cache") },
                        new PendingTask { Name = "default.bin", Status = "Failed", Fid = "default-root",
                            Dest = Path.Combine(root, "default.bin"), Size = 987654, PartsRoot = "" }
                    } });
                var saved = (List<PendingTask>)store.GetMethod("GetPending").Invoke(null, null);
                Program.Assert(File.Exists(state) && saved.Count == 2 && saved[0].Status == "Failed"
                    && saved[0].Fid == "exact-synthetic-fid" && saved[0].Size == 12345678
                    && saved[0].Dest == Path.Combine(root, "synthetic.bin")
                    && saved[0].PartsRoot == Path.Combine(root, "original-cache") && saved[1].PartsRoot == "",
                    "real disk state and fresh-process reload must preserve exact Failed recovery fields");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void RealDiskRestart()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "retry-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string mode in new[] { "--state-write", "--state-read" })
                using (var child = Process.Start(new ProcessStartInfo
                {
                    FileName = typeof(Program).Assembly.Location, Arguments = mode + " \"" + root + "\"",
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    if (!child.WaitForExit(20000))
                    {
                        child.Kill();
                        throw new InvalidOperationException("isolated persistence process did not finish");
                    }
                    Program.Assert(child.ExitCode == 0,
                        "separate persistence process failed: " + child.StandardError.ReadToEnd());
                }
            }
            finally { Directory.Delete(root, true); }
        }

        internal static void TransferRetention()
        {
            var h = new ResumeHarness();
            var item = Failed(h);
            Program.Pump(h.Cleanup(item.Job.Pending.Fid));
            Program.Assert(h.Deleted.Count == 0,
                "automatic cloud cleanup must not destroy the exact Fid needed by a Failed retry");
            item.State = JobState.Completed;
            Program.Pump(h.Cleanup(item.Job.Pending.Fid));
            Program.Assert(h.Deleted.SequenceEqual(new[] { item.Job.Pending.Fid }),
                "completed transfers remain eligible for ordinary cleanup exactly once");
        }

        internal static void RestartRecord()
        {
            foreach (bool defaultRoot in new[] { false, true })
            {
                var h = new ResumeHarness();
                var item = Failed(h);
                if (defaultRoot) item.Job.PartsRoot = null;
                h.Sync();
                var serializer = new DataContractJsonSerializer(typeof(List<PendingTask>));
                List<PendingTask> saved;
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, HistoryStore.Last);
                    stream.Position = 0;
                    saved = (List<PendingTask>)serializer.ReadObject(stream);
                }
                Program.Assert(saved.Count == 1, "failed record survives persistence serialization");
                var next = new ResumeHarness();
                next.ConfigureCache(@"C:\synthetic-only\changed-cache-setting");
                Program.Pump(next.Resume(saved));
                var job = next.Started.Single();
                Program.Assert(job.Dest == item.Job.Dest && job.PartsRoot == (item.Job.PartsRoot ?? "")
                    && job.Pending.Fid == item.Job.Pending.Fid && job.Pending.Size == item.Total,
                    "restart must retain exact Fid/Dest/Size/cache rather than create a new destination");
            }
        }
    }
}
