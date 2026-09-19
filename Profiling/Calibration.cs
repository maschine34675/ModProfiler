using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace ModProfiler.Profiling
{
    internal static class Calibration
    {
        private const int Warmup = 10000;
        private const int Calls = 30000;
        private const int Rounds = 3;

        private static bool patched;
        private static bool failed;
        private static volatile bool on;
        private static long recordedTicks;
        private static long recordedCalls;
        private static int probeCounter;
        internal static double FloorUs = double.NaN;
        internal static double DetourUs = double.NaN;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Probe()
        {
            probeCounter++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Baseline()
        {
            probeCounter++;
        }

        private static void ProbePrefix(out long __state)
        {
            __state = on ? Stopwatch.GetTimestamp() : 0L;
        }

        private static void ProbePostfix(long __state, MethodBase __originalMethod)
        {
            if (__state == 0L)
                return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            ProfilerCore.StatsByMethod.TryGetValue(__originalMethod, out MethodStats _);
            if (Environment.CurrentManagedThreadId == ProfilerCore.MainThreadId)
            {
                Interlocked.Add(ref recordedTicks, elapsed);
                Interlocked.Increment(ref recordedCalls);
            }
        }
        internal static void Run(Harmony harmony)
        {
            if (failed)
                return;
            try
            {
                if (!patched)
                {
                    harmony.Patch(AccessTools.Method(typeof(Calibration), nameof(Probe)),
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(Calibration), nameof(ProbePrefix))) { priority = Priority.First },
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(Calibration), nameof(ProbePostfix))) { priority = Priority.Last });
                    patched = true;
                }

                on = true;
                for (int i = 0; i < Warmup; i++)
                {
                    Probe();
                    Baseline();
                }
                double usPerTick = 1000000.0 / Stopwatch.Frequency;
                double floor = double.MaxValue;
                double withTimers = double.MaxValue;
                double without = double.MaxValue;
                for (int round = 0; round < Rounds; round++)
                {
                    Interlocked.Exchange(ref recordedTicks, 0L);
                    Interlocked.Exchange(ref recordedCalls, 0L);
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < Calls; i++)
                        Probe();
                    long t1 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < Calls; i++)
                        Baseline();
                    long t2 = Stopwatch.GetTimestamp();

                    if (Interlocked.Read(ref recordedCalls) != Calls)
                        throw new InvalidOperationException("the probe's timers did not run on every call");
                    floor = Math.Min(floor, Interlocked.Read(ref recordedTicks) * usPerTick / Calls);
                    withTimers = Math.Min(withTimers, (t1 - t0) * usPerTick / Calls);
                    without = Math.Min(without, (t2 - t1) * usPerTick / Calls);
                }
                on = false;

                FloorUs = floor;
                DetourUs = Math.Max(0.0, withTimers - without);
                Plugin.Log.LogInfo($"measurement cost on this machine: at least {FloorUs:F3} µs of every measured call " +
                                   $"lands inside its value, {DetourUs:F3} µs per call in total");
            }
            catch (Exception e)
            {
                on = false;
                failed = true;
                Plugin.Log.LogWarning("could not calibrate the measurement cost: " + e.Message);
            }
        }
    }
}
