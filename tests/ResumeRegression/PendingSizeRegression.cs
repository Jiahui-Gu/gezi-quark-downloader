using System.Collections.Generic;
using System.Linq;
using GeZi;
using GeZi.Core.Download;

namespace ResumeRegression
{
    internal static class PendingSizeRegression
    {
        private static TaskItem Item(long size, string dest = @"C:\synthetic-only\first.bin") =>
            new TaskItem(new DownloadJob
            {
                Name = "synthetic.bin", Dest = dest,
                Pending = new PendingInfo { Fid = dest, Dest = dest, Size = size }
            }, "synthetic.bin");

        internal static void PreserveKnown()
        {
            HistoryStore.Reset();
            var h = new ResumeHarness();
            var item = Item(15728640000);
            Program.Assert(item.Total == 15728640000, "queued resume must inherit known pending size");
            h.Add(item);
            h.Sync();
            Program.Assert(HistoryStore.Last.Single().Size == 15728640000, "queued save must not overwrite known size with zero");
            h.Update(new JobUpdate { Job = item.Job, State = JobState.Downloading,
                Progress = new DownloadProgress { Done = 1, Total = 0 } });
            Program.Assert(item.Total == 15728640000 && HistoryStore.Last.Single().Size == 15728640000,
                "unknown report must not erase the known total");
            item.Total = 0;
            item.State = JobState.Paused;
            h.Sync();
            Program.Assert(HistoryStore.Last.Single().Size == 15728640000,
                "persistence must independently retain original Pending.Size when UI total is unknown");
            Program.Assert(HistoryStore.Last.Single().Fid == item.Job.Pending.Fid
                && HistoryStore.Last.Single().Dest == item.Job.Dest, "task identity must remain exact");
        }

        internal static void Backfill()
        {
            HistoryStore.Reset();
            var h = new ResumeHarness();
            var item = Item(0);
            h.Add(item);
            h.Update(new JobUpdate { Job = item.Job, State = JobState.Downloading });
            Program.Assert(HistoryStore.Last.Single().Size == 0, "unknown size must remain genuinely unknown");
            int before = HistoryStore.Writes;
            h.Update(new JobUpdate { Job = item.Job, State = JobState.Downloading,
                Progress = new DownloadProgress { Done = 1, Total = 13299312567 } });
            Program.Assert(HistoryStore.Writes == before + 1
                && HistoryStore.Last.Single().Size == 13299312567,
                "same-state authoritative total must trigger one persistence backfill");
            for (int i = 0; i < 10; i++)
                h.Update(new JobUpdate { Job = item.Job, State = JobState.Downloading,
                    Progress = new DownloadProgress { Done = i + 2, Total = 13299312567 } });
            Program.Assert(HistoryStore.Writes == before + 1, "byte progress alone must retain existing persistence throttling");
            h.Update(new JobUpdate { Job = item.Job, State = JobState.Downloading,
                Progress = new DownloadProgress { Total = 13299312568 } });
            Program.Assert(HistoryStore.Last.Single().Size == 13299312568,
                "newly reported true total supersedes an older inherited size");
        }

        internal static void Identity()
        {
            HistoryStore.Reset();
            var h = new ResumeHarness();
            var first = Item(123);
            var second = Item(123, @"C:\synthetic-only\second.bin");
            h.Add(first);
            h.Sync();
            h.Remove(first);
            h.Add(second);
            h.Sync();
            Program.Assert(HistoryStore.Writes == 2 && HistoryStore.Last.Single().Dest == second.Job.Dest,
                "same name/state/size but distinct task must not be hidden by signature throttling");
        }
    }

    // Compile production persistence orchestration against a capture-only edge: never read/write real state.
    internal static class HistoryStore
    {
        internal static List<PendingTask> Last;
        internal static int Writes;
        internal static void Reset() { Last = null; Writes = 0; }
        internal static void SetPending(List<PendingTask> tasks) { Last = tasks; Writes++; }
        internal static void AddHistory(string name, string size, string dest, string status) { }
    }
}
