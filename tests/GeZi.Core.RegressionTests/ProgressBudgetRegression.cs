using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GeZi.Core.Download;

internal static class ProgressBudgetRegression
{
    internal static int Run()
    {
        try
        {
            Type type = typeof(SegmentedDownloader).GetNestedType("ProgressRetryBudget", BindingFlags.NonPublic);
            Check(type != null, "retry budget must distinguish historical failures from bounded lack of progress");
            Func<int, long, object> create = (limit, initial) => Activator.CreateInstance(type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { limit, initial }, null);
            Func<object, long, bool> reserve = (budget, bytes) =>
                (bool)type.GetMethod("TryReserve").Invoke(budget, new object[] { bytes });
            const long quantum = 512 * 1024;
            object shared = create(500, 100 * quantum);
            int accepted = 0;
            Parallel.For(0, 100000, _ =>
            {
                if (reserve(shared, 100 * quantum)) Interlocked.Increment(ref accepted);
            });
            Check(accepted == 500, "cached starting bytes cannot renew; exactly 500 concurrent reservations");
            Check(!reserve(shared, 101 * quantum - 1), "sub-threshold trickle cannot renew");
            accepted = 0;
            Parallel.For(0, 100000, _ =>
            {
                if (reserve(shared, 101 * quantum)) Interlocked.Increment(ref accepted);
            });
            Check(accepted == 500 && !reserve(shared, 101 * quantum),
                "one shared progress watermark can renew only once across all workers");
            Check(!reserve(shared, 100 * quantum), "stale or redistributed watermarks cannot renew");
            object part = create(3, 0);
            for (int i = 0; i < 3; i++) Check(reserve(part, 0), "original per-part requeues");
            Check(!reserve(part, 0), "other parts' shared progress cannot reset this part");
            Check(reserve(shared, 102 * quantum) && !reserve(part, quantum - 1),
                "healthy parts cannot indefinitely sustain a last bad part or sub-threshold drips");
            Check(reserve(part, quantum), "own new committed quantum can renew a part window");
            for (int i = 0; i < 2; i++) Check(reserve(part, quantum), "remaining own-window reservations");
            Check(!reserve(part, quantum), "same part quantum cannot be spent repeatedly");
            Console.WriteLine("PASS progress budget atomic watermarks, cached bytes, trickle and isolated per-part bounds");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL progress budget " + ex.Message);
            return 1;
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
