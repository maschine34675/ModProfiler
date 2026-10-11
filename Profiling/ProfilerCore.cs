using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace ModProfiler.Profiling
{
    internal static class ProfilerCore
    {
        internal const int WindowSize = 150;

        internal static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        internal static volatile bool Active;
        internal static bool Instrumented;
        internal static int PatchFailures;
        internal static readonly Guid SessionId = Guid.NewGuid();
        internal static readonly DateTime SessionStart = DateTime.Now;
        internal static int MainThreadId;
        internal static long TotalFrames;
        internal static double MeasuredSeconds;
        internal static long SkippedFrames;
        internal static int ResetCount;
        internal static int RescanCount;
        internal static int GcCollectionsAtReset;
        internal static int ExportIndex;
        internal static int ExportCommitted;
        internal static long ExportFrames;
        internal static double ExportSeconds;
        internal static string ExportFile;
        internal static bool ResetSinceExport = true;

        internal sealed class PatchFailure
        {
            internal string Mod;
            internal string Method;
            internal string Reason;
        }
        internal static readonly List<PatchFailure> PatchFailureDetails = new List<PatchFailure>();
        internal const int MaxPatchFailureDetails = 200;
        internal static volatile Dictionary<MethodBase, MethodStats> StatsByMethod =
            new Dictionary<MethodBase, MethodStats>();

        internal static readonly List<ModStats> Mods = new List<ModStats>();

        internal static readonly float[] FrameMsWindow = new float[WindowSize];
        internal static int Cursor;
        internal static int FramesObserved;
        internal static bool GcModeEnabled;
        internal static double HeapMb;
        internal static double HeapPeakMb;
        internal static double AllocMbS;
        internal static double GcPerSecond;

        private static readonly long[] AllocBytesWindow = new long[WindowSize];
        private static readonly int[] GcCountWindow = new int[WindowSize];
        private static long lastAllocBytes;
        private static int lastRateGcCount;

        private static bool lastGcModeForLog;
        private static double lastGcModeLogTime = double.NegativeInfinity;
        private static bool lastGcModeLogPrimed;
        internal static void TimerPrefix(out long __state)
        {
            __state = Active ? Stopwatch.GetTimestamp() : 0L;
        }

        internal static void TimerPostfix(long __state, MethodBase __originalMethod)
        {
            if (__state == 0L)
                return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            if (StatsByMethod.TryGetValue(__originalMethod, out MethodStats stats))
                stats.Record(elapsed, Environment.CurrentManagedThreadId == MainThreadId);
        }
        internal static void SetActive(bool on)
        {
            if (Activity.SetWindow(on))
                ApplyActivity();
        }
        internal const int HoldHeadless = 1;
        internal static void SetHold(int bit, bool on)
        {
            if (Activity.SetHold(bit, on))
                ApplyActivity();
        }
        internal static bool CaptureHeld => Activity.Held;

        private static readonly ActivityArbiter Activity = new ActivityArbiter();
        private static void ApplyActivity()
        {
            bool on = Activity.Active;
            Active = on;
            DropPending();
            if (on)
            {
                DiscardNextFlush = true;
                lastAllocBytes = GcHeapSource.SampleBytes();
            }
        }
        internal static bool DiscardNextFlush;

        internal static void DropPending()
        {
            for (int i = 0; i < Mods.Count; i++)
                Mods[i].DropPending();
            pendingFixedSteps = 0;
        }
        private static int pendingFixedSteps;
        internal static long TotalFixedSteps;
        internal static void CountFixedStep()
        {
            if (Active)
                pendingFixedSteps++;
        }
        private static int lastGcCount;
        internal static bool SkipNextFlush;

        internal static void FlushFrame(float frameMs)
        {
            int gc = GcCount();
            bool gcRan = gc != lastGcCount;
            lastGcCount = gc;
            GcModeEnabled = UnityEngine.Scripting.GarbageCollector.GCMode
                          == UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
            HeapMb = GC.GetTotalMemory(false) / 1_000_000.0;
            if (HeapMb > HeapPeakMb)
                HeapPeakMb = HeapMb;
            long allocNow = GcHeapSource.SampleBytes();
            long deltaBytes = Math.Max(0L, allocNow - lastAllocBytes);
            lastAllocBytes = allocNow;
            int gcDelta = gc - lastRateGcCount;
            lastRateGcCount = gc;
            if (!lastGcModeLogPrimed)
            {
                lastGcModeLogPrimed = true;
            }
            else if (GcModeEnabled != lastGcModeForLog && MeasuredSeconds - lastGcModeLogTime >= 5.0)
            {
                Plugin.Log.LogInfo($"GC mode changed: {(lastGcModeForLog ? "on" : "off")} "
                                  + $"-> {(GcModeEnabled ? "on" : "off")}");
                lastGcModeLogTime = MeasuredSeconds;
            }
            lastGcModeForLog = GcModeEnabled;

            if (DiscardNextFlush)
            {
                DiscardNextFlush = false;
                SkipNextFlush = false;
                DropPending();
                return;
            }

            if (SkipNextFlush)
            {
                SkipNextFlush = false;
                for (int i = 0; i < Mods.Count; i++)
                    Mods[i].FlushTotalsOnly();
                TotalFrames++;
                SkippedFrames++;
                TotalFixedSteps += pendingFixedSteps;
                pendingFixedSteps = 0;
                return;
            }

            int slot = Cursor;
            FrameMsWindow[slot] = frameMs;
            AllocBytesWindow[slot] = deltaBytes;
            GcCountWindow[slot] = gcDelta;
            for (int i = 0; i < Mods.Count; i++)
                Mods[i].FlushFrame(slot);
            Cursor = (slot + 1) % WindowSize;
            if (FramesObserved < WindowSize)
                FramesObserved++;
            TotalFrames++;
            TotalFixedSteps += pendingFixedSteps;
            pendingFixedSteps = 0;
            MeasuredSeconds += frameMs / 1000.0;

            double windowSeconds = Sum(FrameMsWindow, FramesObserved) / 1000.0;
            if (windowSeconds > 0.0)
            {
                AllocMbS = Sum(AllocBytesWindow, FramesObserved) / 1_000_000.0 / windowSeconds;
                GcPerSecond = Sum(GcCountWindow, FramesObserved) / windowSeconds;
            }

            if (MeasuredSeconds - growthSampledAt >= GrowthSampleSeconds)
                SampleGrowth();
            Instrumenter.CheckVanillaCoverageIfDue();
            float threshold = Plugin.SpikeThresholdMs.Value;
            if (threshold > 0f && FramesObserved >= 30
                && frameMs >= threshold && frameMs >= AverageFrameMs() * 1.5f)
                CaptureSpike(slot, frameMs, gcRan);
        }
        internal const double GrowthSampleSeconds = 20.0;

        private static double growthSampledAt;
        private static long growthFrames;
        private static long growthFixedSteps;
        private static void SampleGrowth()
        {
            long frames = TotalFrames - growthFrames;
            long steps = TotalFixedSteps - growthFixedSteps;
            growthSampledAt = MeasuredSeconds;
            growthFrames = TotalFrames;
            growthFixedSteps = TotalFixedSteps;

            for (int i = 0; i < Mods.Count; i++)
            {
                List<MethodStats> methods = Mods[i].Methods;
                for (int k = 0; k < methods.Count; k++)
                {
                    MethodStats m = methods[k];
                    if (m.Growth == null)
                        continue;
                    long calls = m.TotalCalls - m.GrowthCalls;
                    m.GrowthCalls = m.TotalCalls;
                    if (!m.GrowthPrimed)
                    {
                        m.GrowthPrimed = true;
                        continue;
                    }
                    long per = m.Kind == "FixedUpdate" ? steps : frames;
                    if (per > 0L)
                        m.Growth.Feed((double)calls / per, MeasuredSeconds);
                }
            }
        }
        internal const int MaxSpikes = 200;
        internal const int SpikesInSnapshot = 20;

        internal sealed class SpikeEntry
        {
            internal string Time;
            internal long Frame;
            internal double Seconds;
            internal float FrameMs;
            internal bool Gc;
            internal List<SpikeContributor> Top;
        }

        internal sealed class SpikeContributor
        {
            internal string Mod;
            internal double Ms;
            internal string Method;
            internal double MethodMs;
        }
        internal static readonly List<SpikeEntry> Spikes = new List<SpikeEntry>();

        private static void CaptureSpike(int slot, float frameMs, bool gcRan)
        {
            var mods = new List<ModStats>();
            for (int i = 0; i < Mods.Count; i++)
                if (Mods[i].TicksWindow[slot] * MsPerTick >= 0.05)
                    mods.Add(Mods[i]);
            mods.Sort((a, b) => b.TicksWindow[slot].CompareTo(a.TicksWindow[slot]));
            if (mods.Count > 3)
                mods.RemoveRange(3, mods.Count - 3);
            var contributors = new List<SpikeContributor>(mods.Count);
            foreach (ModStats mod in mods)
            {
                MethodStats top = null;
                for (int k = 0; k < mod.Methods.Count; k++)
                    if (top == null || mod.Methods[k].TicksWindow[slot] > top.TicksWindow[slot])
                        top = mod.Methods[k];
                contributors.Add(new SpikeContributor
                {
                    Mod = mod.Name,
                    Ms = mod.TicksWindow[slot] * MsPerTick,
                    Method = top?.DisplayName,
                    MethodMs = top != null ? top.TicksWindow[slot] * MsPerTick : 0.0,
                });
            }

            Spikes.Add(new SpikeEntry
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Frame = TotalFrames,
                Seconds = MeasuredSeconds,
                FrameMs = frameMs,
                Gc = gcRan,
                Top = contributors,
            });
            if (Spikes.Count > MaxSpikes)
                Spikes.RemoveAt(0);
        }
        internal static void ResetWindows()
        {
            Array.Clear(FrameMsWindow, 0, WindowSize);
            Array.Clear(AllocBytesWindow, 0, WindowSize);
            Array.Clear(GcCountWindow, 0, WindowSize);
            for (int i = 0; i < Mods.Count; i++)
                Mods[i].ResetWindow();
            Cursor = 0;
            FramesObserved = 0;
        }
        internal static void ResetAll()
        {
            if (CaptureHeld)
                Plugin.Log.LogInfo("counters reset while a headless capture is running - "
                    + "the next export starts over (reset_count goes up, its delta is since the reset)");
            ResetWindows();
            Spikes.Clear();
            for (int i = 0; i < Mods.Count; i++)
                Mods[i].ResetTotals();
            TotalFrames = 0L;
            MeasuredSeconds = 0.0;
            TotalFixedSteps = 0L;
            growthSampledAt = 0.0;
            growthFrames = 0L;
            growthFixedSteps = 0L;
            ResetCount++;
            GcCollectionsAtReset = GcCount();
            HeapPeakMb = HeapMb;
            lastGcModeLogTime = double.NegativeInfinity;
            SkippedFrames = 0L;
            DiscardNextFlush = true;
            ExportFrames = 0L;
            ExportSeconds = 0.0;
            ResetSinceExport = true;
        }
        internal static int GcCount()
        {
            int count = 0;
            for (int g = 0; g <= GC.MaxGeneration; g++)
                count += GC.CollectionCount(g);
            return count;
        }

        internal static float AverageFrameMs()
        {
            int n = FramesObserved;
            if (n == 0)
                return 0f;
            float sum = 0f;
            for (int i = 0; i < n; i++)
                sum += FrameMsWindow[i];
            return sum / n;
        }

        internal static long Sum(long[] window, int frames)
        {
            long sum = 0L;
            for (int i = 0; i < frames; i++)
                sum += window[i];
            return sum;
        }

        internal static int Sum(int[] window, int frames)
        {
            int sum = 0;
            for (int i = 0; i < frames; i++)
                sum += window[i];
            return sum;
        }

        internal static float Sum(float[] window, int frames)
        {
            float sum = 0f;
            for (int i = 0; i < frames; i++)
                sum += window[i];
            return sum;
        }

        internal static long Max(long[] window, int frames)
        {
            long max = 0L;
            for (int i = 0; i < frames; i++)
                if (window[i] > max)
                    max = window[i];
            return max;
        }
    }
}
