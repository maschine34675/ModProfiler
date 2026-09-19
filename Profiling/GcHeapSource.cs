using System;

namespace ModProfiler.Profiling
{
    internal static class GcHeapSource
    {
        internal static bool Available { get; private set; } = true;
        internal static bool PerThreadCounter { get; private set; } = true;
        internal static string SourceName => PerThreadCounter ? "per-thread-counter" : "heap-delta";

        internal static void Validate()
        {
            try
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var probe = new byte[64 * 1024];
                long after = GC.GetAllocatedBytesForCurrentThread();
                GC.KeepAlive(probe);
                if (after - before < 32 * 1024)
                    PerThreadCounter = false;
            }
            catch (Exception)
            {
                PerThreadCounter = false;
            }

            if (!PerThreadCounter)
            {
                Plugin.Log.LogInfo("GC.GetAllocatedBytesForCurrentThread does not move on this runtime "
                    + "(Boehm reports 0 for it) - the allocation rate is taken from GC.GetTotalMemory(false) "
                    + "deltas instead: exact while the collector is off, a lower bound while it is on.");
                try
                {
                    GC.GetTotalMemory(false);
                }
                catch (Exception ex)
                {
                    Available = false;
                    Plugin.Log.LogWarning("GC.GetTotalMemory(false) threw as well (" + ex.Message
                        + ") - allocation rate will read as n/a.");
                }
            }
        }
        internal static long SampleBytes()
        {
            return PerThreadCounter ? GC.GetAllocatedBytesForCurrentThread() : GC.GetTotalMemory(false);
        }
    }
}
