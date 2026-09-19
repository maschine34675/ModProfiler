using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace ModProfiler.Profiling
{
    internal sealed class MethodStats
    {
        internal readonly MethodBase Method;
        internal string DisplayName;
        internal readonly string Category;
        internal string Kind;
        internal readonly ModStats Mod;
        internal string NestedUnder;
        internal List<MethodBase> Targets;
        internal long FramesAtCreation;
        private long _pendingMainTicks;
        private long _pendingMainCalls;
        private long _pendingOffTicks;
        private long _pendingOffCalls;

        internal readonly long[] TicksWindow = new long[ProfilerCore.WindowSize];
        internal readonly long[] CallsWindow = new long[ProfilerCore.WindowSize];

        internal long TotalTicks;
        internal long TotalCalls;
        internal long OffThreadTicks;
        internal long OffThreadCalls;
        internal long MaxMainFrameTicks;
        internal long ExportTicks;
        internal long ExportCalls;
        internal int SeenAtExport;
        internal readonly GrowthTracker Growth;
        internal long GrowthCalls;
        internal bool GrowthPrimed;

        internal MethodStats(MethodBase method, string displayName, string category, string kind, ModStats mod,
            string nestedUnder = null)
        {
            Method = method;
            DisplayName = displayName;
            Category = category;
            Kind = kind;
            Mod = mod;
            NestedUnder = nestedUnder;
            FramesAtCreation = ProfilerCore.TotalFrames;
            if (category == "Unity" && (kind == "Update" || kind == "LateUpdate" || kind == "FixedUpdate"))
                Growth = new GrowthTracker();
        }

        internal void Record(long ticks, bool mainThread)
        {
            if (mainThread)
            {
                Interlocked.Add(ref _pendingMainTicks, ticks);
                Interlocked.Increment(ref _pendingMainCalls);
            }
            else
            {
                Interlocked.Add(ref _pendingOffTicks, ticks);
                Interlocked.Increment(ref _pendingOffCalls);
            }
        }

        internal void FlushFrame(int slot, out long ticks, out long calls)
        {
            Drain(out long mainTicks, out long mainCalls, out long offTicks, out long offCalls);
            ticks = mainTicks + offTicks;
            calls = mainCalls + offCalls;
            TicksWindow[slot] = ticks;
            CallsWindow[slot] = calls;
            Accumulate(mainTicks, mainCalls, offTicks, offCalls);
        }
        internal void FlushTotalsOnly(out long ticks, out long calls)
        {
            Drain(out long mainTicks, out long mainCalls, out long offTicks, out long offCalls);
            ticks = mainTicks + offTicks;
            calls = mainCalls + offCalls;
            Accumulate(mainTicks, mainCalls, offTicks, offCalls);
        }

        private void Drain(out long mainTicks, out long mainCalls, out long offTicks, out long offCalls)
        {
            mainTicks = Interlocked.Exchange(ref _pendingMainTicks, 0L);
            mainCalls = Interlocked.Exchange(ref _pendingMainCalls, 0L);
            offTicks = Interlocked.Exchange(ref _pendingOffTicks, 0L);
            offCalls = Interlocked.Exchange(ref _pendingOffCalls, 0L);
        }

        private void Accumulate(long mainTicks, long mainCalls, long offTicks, long offCalls)
        {
            TotalTicks += mainTicks + offTicks;
            TotalCalls += mainCalls + offCalls;
            OffThreadTicks += offTicks;
            OffThreadCalls += offCalls;
            if (mainTicks > MaxMainFrameTicks)
                MaxMainFrameTicks = mainTicks;
        }
        internal void DropPending()
        {
            Interlocked.Exchange(ref _pendingMainTicks, 0L);
            Interlocked.Exchange(ref _pendingMainCalls, 0L);
            Interlocked.Exchange(ref _pendingOffTicks, 0L);
            Interlocked.Exchange(ref _pendingOffCalls, 0L);
        }

        internal void ResetWindow()
        {
            DropPending();
            Array.Clear(TicksWindow, 0, TicksWindow.Length);
            Array.Clear(CallsWindow, 0, CallsWindow.Length);
        }

        internal void ResetTotals()
        {
            TotalTicks = 0L;
            TotalCalls = 0L;
            OffThreadTicks = 0L;
            OffThreadCalls = 0L;
            MaxMainFrameTicks = 0L;
            FramesAtCreation = 0L;
            ExportTicks = 0L;
            ExportCalls = 0L;
            Growth?.Reset();
            GrowthCalls = 0L;
            GrowthPrimed = false;
        }
        internal long FramesSeen()
        {
            long frames = ProfilerCore.TotalFrames - FramesAtCreation;
            return frames > 0L ? frames : 0L;
        }

        internal double WindowAvgMs(int frames)
        {
            return ProfilerCore.Sum(TicksWindow, frames) * ProfilerCore.MsPerTick / frames;
        }

        internal double WindowMaxMs(int frames)
        {
            return ProfilerCore.Max(TicksWindow, frames) * ProfilerCore.MsPerTick;
        }

        internal double WindowCallsPerFrame(int frames)
        {
            return (double)ProfilerCore.Sum(CallsWindow, frames) / frames;
        }
    }
}
