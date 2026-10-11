using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using ModProfiler.Profiling;
using UnityEngine;

namespace ModProfiler.UI
{
    internal static class CsvExport
    {
        internal const int Format = 2;
        internal const string TriggerManual = "manual";
        internal const string TriggerAuto = "auto";
        internal const string TriggerHeadlessStart = "headless-start";
        internal const string TriggerHeadlessStop = "headless-stop";
        internal static DateTime? LastExportAt;
        internal static string LastExportMessage;

        private struct RowBaseline
        {
            internal MethodStats Row;
            internal long Ticks;
            internal long Calls;
        }
        private struct ModRow
        {
            internal string Name;
            internal int Methods;
            internal int MethodsNew;
            internal long Calls;
            internal double TotalMs;
            internal double MsPerFrame;
            internal double CallsPerFrame;
            internal bool HasDelta;
            internal long CallsDelta;
            internal double MsDelta;
            internal double MsPerFrameDelta;
            internal double CallsPerFrameDelta;
        }

        private sealed class Outcome
        {
            internal string Trigger;
            internal string BaseName;
            internal string Path;
            internal bool Ok;
            internal string Error;
            internal string PointerError;
            internal DateTime Time;
            internal int Index;
            internal long Frames;
            internal double Seconds;
            internal int ResetCount;
            internal List<RowBaseline> Rows;
        }

        private static readonly ConcurrentQueue<Outcome> finished = new ConcurrentQueue<Outcome>();
        private static readonly PointerFile pointer = new PointerFile();
        private static string lastPointerError;

        internal static string ExportsRoot =>
            Path.Combine(BepInEx.Paths.PluginPath, "maschine-ModProfiler", "exports");
        internal static string SessionFolder =>
            ProfilerCore.SessionStart.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        internal static string PointerFileName => PointerFile.NameFor(Environment.MachineName);
        private static readonly HashSet<string> inFlight = new HashSet<string>();
        internal static string Begin(string trigger)
        {
            if (ProfilerCore.Active)
                ProfilerCore.SkipNextFlush = true;

            string sessionFolder = SessionFolder;
            string dir = Path.Combine(ExportsRoot, sessionFolder);
            Directory.CreateDirectory(dir);
            string pointerPath = ProfilerCore.CaptureHeld || trigger == TriggerHeadlessStop
                ? Path.Combine(ExportsRoot, PointerFileName)
                : null;

            DateTime now = DateTime.Now;
            string baseName = UniqueBaseName(dir, "modprofiler-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            string csvPath = Path.Combine(dir, baseName + ".csv");
            string jsonPath = Path.Combine(dir, baseName + ".meta.json");
            int committed = ProfilerCore.ExportCommitted;
            long deltaFrames = ProfilerCore.TotalFrames - ProfilerCore.ExportFrames;
            double deltaSeconds = ProfilerCore.MeasuredSeconds - ProfilerCore.ExportSeconds;
            bool sinceReset = committed == 0 || ProfilerCore.ResetSinceExport;
            bool deltaValid = committed > 0 && !ProfilerCore.ResetSinceExport;
            string previousFile = ProfilerCore.ExportFile;
            int index = ++ProfilerCore.ExportIndex;

            bool timersIntact = Instrumenter.TimersIntact(20);
            if (!timersIntact)
                Plugin.Log.LogWarning("export: some of this mod's timer patches are missing - another mod's " +
                                      "Harmony.UnpatchAll() may have removed them; press Rescan to reinstate them");

            var rows = new List<RowBaseline>(ProfilerCore.StatsByMethod.Count);
            var mods = new List<ModRow>(ProfilerCore.Mods.Count);
            string csv = BuildCsv(committed, deltaFrames, rows, mods, out int idleRows);
            string json = BuildSidecar(now, index, trigger, sessionFolder, baseName + ".csv", sinceReset, deltaValid,
                deltaFrames, deltaSeconds, previousFile, idleRows, timersIntact, mods);

            var outcome = new Outcome
            {
                Trigger = trigger,
                BaseName = baseName,
                Path = csvPath,
                Index = index,
                Frames = ProfilerCore.TotalFrames,
                Seconds = ProfilerCore.MeasuredSeconds,
                ResetCount = ProfilerCore.ResetCount,
                Rows = rows,
            };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    File.WriteAllText(csvPath, csv, new UTF8Encoding(false));
                    File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
                    outcome.Ok = true;
                }
                catch (Exception e)
                {
                    outcome.Error = e.Message;
                }
                if (outcome.Ok && pointerPath != null)
                {
                    try
                    {
                        pointer.Write(pointerPath, json, index);
                    }
                    catch (Exception e)
                    {
                        outcome.PointerError = e.Message;
                    }
                }
                outcome.Time = DateTime.Now;
                finished.Enqueue(outcome);
            });

            return "writing " + baseName + ".csv ...";
        }
        internal static string Poll()
        {
            string message = null;
            while (finished.TryDequeue(out Outcome outcome))
            {
                inFlight.Remove(outcome.BaseName);
                bool auto = outcome.Trigger == TriggerAuto;
                bool stop = outcome.Trigger == TriggerHeadlessStop;
                if (outcome.Ok)
                {
                    Commit(outcome);
                    LastExportAt = outcome.Time;
                    lastAutoError = null;
                    if (auto)
                    {
                        message = "auto-export written: " + Path.GetFileName(outcome.Path);
                        Plugin.Log.LogDebug("CSV written: " + outcome.Path);
                    }
                    else
                    {
                        message = "CSV written: " + outcome.Path;
                        Plugin.Log.LogInfo(!stop ? message
                            : "headless capture stopped: final export " + outcome.Path
                              + (outcome.PointerError != null ? " (pointer file not updated)" : ""));
                    }
                    if (outcome.PointerError != null && (stop || outcome.PointerError != lastPointerError))
                        Plugin.Log.LogWarning("export written, but the pointer file " + PointerFileName
                            + " could not be replaced: " + outcome.PointerError);
                    lastPointerError = outcome.PointerError;
                }
                else
                {
                    message = (auto ? "auto-export failed: " : "CSV export failed: ") + outcome.Error;
                    if (!auto || outcome.Error != lastAutoError)
                        Plugin.Log.LogError(stop
                            ? "headless capture ended without a final export - writing it failed: " + outcome.Error
                            : message);
                    if (auto)
                        lastAutoError = outcome.Error;
                }
            }
            if (message != null)
                LastExportMessage = message;
            return message;
        }

        private static string lastAutoError;
        private static double autoExportedAt;
        internal static void AutoTick()
        {
            float interval = Plugin.AutoExportIntervalSeconds.Value;
            if (interval <= 0f || !ProfilerCore.Active || !ProfilerCore.Instrumented)
                return;
            if (interval < MinAutoExportSeconds)
                interval = MinAutoExportSeconds;
            double now = ProfilerCore.MeasuredSeconds;
            if (now < autoExportedAt)
                autoExportedAt = 0.0;
            if (now - autoExportedAt < interval)
                return;
            autoExportedAt = now;
            try
            {
                Begin(TriggerAuto);
            }
            catch (Exception e)
            {
                LastExportMessage = "auto-export failed: " + e.Message;
                if (e.Message != lastAutoError)
                    Plugin.Log.LogError(LastExportMessage);
                lastAutoError = e.Message;
            }
        }
        internal const float MinAutoExportSeconds = 10f;
        internal static void RestartAutoInterval()
        {
            autoExportedAt = ProfilerCore.MeasuredSeconds;
        }

        private static void Commit(Outcome outcome)
        {
            if (outcome.ResetCount != ProfilerCore.ResetCount || outcome.Index < ProfilerCore.ExportCommitted)
                return;
            ProfilerCore.ExportCommitted = outcome.Index;
            ProfilerCore.ExportFrames = outcome.Frames;
            ProfilerCore.ExportSeconds = outcome.Seconds;
            ProfilerCore.ExportFile = outcome.BaseName + ".csv";
            ProfilerCore.ResetSinceExport = false;
            foreach (RowBaseline b in outcome.Rows)
            {
                b.Row.ExportTicks = b.Ticks;
                b.Row.ExportCalls = b.Calls;
                b.Row.SeenAtExport = outcome.Index;
            }
        }
        private static string UniqueBaseName(string dir, string baseName)
        {
            string candidate = baseName;
            for (int n = 2; ; n++)
            {
                bool taken = inFlight.Contains(candidate)
                    || File.Exists(Path.Combine(dir, candidate + ".csv"))
                    || File.Exists(Path.Combine(dir, candidate + ".meta.json"));
                if (!taken)
                    break;
                candidate = baseName + "_" + n;
            }
            inFlight.Add(candidate);
            return candidate;
        }

        private static string BuildCsv(int committed, long deltaFrames, List<RowBaseline> rows, List<ModRow> mods,
            out int idleRows)
        {
            idleRows = 0;
            var targetsThatRan = new HashSet<MethodBase>();
            foreach (ModStats mod in ProfilerCore.Mods)
                foreach (MethodStats m in mod.Methods)
                    if (m.Targets != null && m.TotalCalls > 0)
                        foreach (MethodBase target in m.Targets)
                            targetsThatRan.Add(target);

            var sb = new StringBuilder(256 * 1024);
            sb.Append("mod,category,kind,method,target,method_token,frames,calls,total_ms,ms_per_frame,")
              .Append("calls_per_frame,avg_us_per_call,max_ms_in_frame,off_thread_calls,off_thread_ms,")
              .Append("calls_delta,total_ms_delta,ms_per_frame_delta,calls_per_frame_delta,flags,csv_format")
              .AppendLine();

            var flags = new List<string>(4);
            foreach (ModStats mod in ProfilerCore.Mods)
            {
                var sum = new ModRow { Name = mod.Name };
                foreach (MethodStats m in mod.Methods)
                {
                    bool counted = m.NestedUnder == null;
                    long frames = m.FramesSeen();
                    double totalMs = m.TotalTicks * ProfilerCore.MsPerTick;
                    double maxMs = m.MaxMainFrameTicks * ProfilerCore.MsPerTick;
                    double offMs = m.OffThreadTicks * ProfilerCore.MsPerTick;
                    double msPerFrame = frames > 0 ? totalMs / frames : 0.0;
                    double callsPerFrame = frames > 0 ? (double)m.TotalCalls / frames : 0.0;
                    double usPerCall = m.TotalCalls > 0 ? totalMs * 1000.0 / m.TotalCalls : 0.0;
                    bool isNew = committed > 0 && m.SeenAtExport != committed;
                    if (counted)
                    {
                        sum.Methods++;
                        if (isNew)
                            sum.MethodsNew++;
                        sum.Calls += m.TotalCalls;
                        sum.TotalMs += totalMs;
                        sum.MsPerFrame += msPerFrame;
                        sum.CallsPerFrame += callsPerFrame;
                    }

                    flags.Clear();
                    if (m.TotalCalls == 0)
                    {
                        bool siblingRan = false;
                        if (m.Targets != null)
                            foreach (MethodBase target in m.Targets)
                                if (targetsThatRan.Contains(target))
                                {
                                    siblingRan = true;
                                    break;
                                }
                        flags.Add(siblingRan ? "maybe-inlined" : "idle");
                        idleRows++;
                    }
                    if (callsPerFrame < 0.01 && maxMs >= 10.0)
                        flags.Add("rare-heavy");
                    if (totalMs > 0.0 && offMs >= 0.5 * totalMs)
                        flags.Add("off-thread");
                    if (m.Targets != null && m.Targets.Count > 1)
                        flags.Add("multi-target");
                    if (m.Growth != null && m.Growth.Growing)
                        flags.Add("growing");
                    if (isNew)
                        flags.Add("new");
                    if (m.NestedUnder != null)
                        flags.Add("nested");

                    Quote(sb, mod.Name).Append(',')
                      .Append(m.Category).Append(',')
                      .Append(m.Kind).Append(',');
                    Quote(sb, PlainName(m)).Append(',');
                    Quote(sb, TargetList(m)).Append(',')
                      .Append(m.Method.MetadataToken.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(frames.ToString(CultureInfo.InvariantCulture)).Append(',')
                      .Append(m.TotalCalls.ToString(CultureInfo.InvariantCulture)).Append(',');
                    Num(sb, totalMs, 3).Append(',');
                    Num(sb, msPerFrame, 5).Append(',');
                    Num(sb, callsPerFrame, 4).Append(',');
                    Num(sb, usPerCall, 2).Append(',');
                    Num(sb, maxMs, 3).Append(',')
                      .Append(m.OffThreadCalls.ToString(CultureInfo.InvariantCulture)).Append(',');
                    Num(sb, offMs, 3).Append(',');

                    if (isNew)
                        sb.Append(",,,,");
                    else
                    {
                        long callsDelta = m.TotalCalls - m.ExportCalls;
                        double msDelta = (m.TotalTicks - m.ExportTicks) * ProfilerCore.MsPerTick;
                        long intervalFrames = Math.Min(deltaFrames, frames);
                        double msPerFrameDelta = intervalFrames > 0 ? msDelta / intervalFrames : 0.0;
                        double callsPerFrameDelta = intervalFrames > 0 ? (double)callsDelta / intervalFrames : 0.0;
                        sb.Append(callsDelta.ToString(CultureInfo.InvariantCulture)).Append(',');
                        Num(sb, msDelta, 3).Append(',');
                        Num(sb, msPerFrameDelta, 5).Append(',');
                        Num(sb, callsPerFrameDelta, 4).Append(',');
                        if (counted)
                        {
                            sum.HasDelta = true;
                            sum.CallsDelta += callsDelta;
                            sum.MsDelta += msDelta;
                            sum.MsPerFrameDelta += msPerFrameDelta;
                            sum.CallsPerFrameDelta += callsPerFrameDelta;
                        }
                    }

                    sb.Append(string.Join(" ", flags)).Append(',')
                      .Append(Format).AppendLine();

                    rows.Add(new RowBaseline { Row = m, Ticks = m.TotalTicks, Calls = m.TotalCalls });
                }
                if (sum.Methods > 0)
                    mods.Add(sum);
            }
            return sb.ToString();
        }
        private static string PlainName(MethodStats m)
        {
            int cut = m.DisplayName.IndexOf(" (", StringComparison.Ordinal);
            return cut > 0 ? m.DisplayName.Substring(0, cut) : m.DisplayName;
        }

        private static string TargetList(MethodStats m)
        {
            if (m.Targets == null || m.Targets.Count == 0)
                return "";
            if (m.Targets.Count == 1)
                return Instrumenter.TargetName(m.Targets[0]);
            var names = new string[m.Targets.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = Instrumenter.TargetName(m.Targets[i]);
            return string.Join(";", names);
        }

        private static string BuildSidecar(DateTime now, int index, string trigger, string sessionFolder,
            string csvFile, bool sinceReset, bool deltaValid, long deltaFrames, double deltaSeconds,
            string previousFile, int idleRows, bool timersIntact, List<ModRow> mods)
        {
            var sb = new StringBuilder(16 * 1024);
            var json = new JsonBuilder(sb);
            json.BeginObject()
                .Value("format", Format)
                .Value("profiler", Plugin.PluginName + " " + Plugin.PluginVersion)
                .Value("session_id", ProfilerCore.SessionId.ToString())
                .BeginObject("export")
                    .Value("time", now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture))
                    .Value("index", index)
                    .Value("trigger", trigger)
                    .Value("dir", sessionFolder)
                    .Value("csv_file", csvFile)
                .EndObject()
                .BeginObject("game")
                    .Value("unity", Application.unityVersion)
                    .Value("eft", GameVersion())
                    .Value("spt", PluginVersion("com.SPT.core"))
                    .Value("weboverlay", WebOverlayGate.FoundVersion?.ToString())
                .EndObject()
                .BeginObject("machine")
                    .Value("cpu", SystemInfo.processorType)
                    .Value("gpu", SystemInfo.graphicsDeviceName)
                    .Value("ram_mb", SystemInfo.systemMemorySize)
                    .Value("resolution", Screen.currentResolution.width + "x" + Screen.currentResolution.height)
                    .Value("refresh_hz", Screen.currentResolution.refreshRateRatio.value, 2)
                    .Value("vsync", QualitySettings.vSyncCount)
                    .Value("target_fps", Application.targetFrameRate)
                .EndObject()
                .BeginObject("measurement")
                    .Value("frames", ProfilerCore.TotalFrames)
                    .Value("seconds", ProfilerCore.MeasuredSeconds, 3)
                    .Value("avg_fps", ProfilerCore.MeasuredSeconds > 0.0
                        ? (ProfilerCore.TotalFrames - ProfilerCore.SkippedFrames) / ProfilerCore.MeasuredSeconds : 0.0, 1)
                    .Value("active", ProfilerCore.Active)
                    .Value("headless", ProfilerCore.CaptureHeld)
                    .Value("reset_count", ProfilerCore.ResetCount)
                    .Value("rescan_count", ProfilerCore.RescanCount)
                    .Value("instrumented_methods", ProfilerCore.StatsByMethod.Count)
                    .Value("rows_idle", idleRows)
                    .Value("patch_failures", ProfilerCore.PatchFailures)
                    .Value("gc_collections_since_reset", ProfilerCore.GcCount() - ProfilerCore.GcCollectionsAtReset)
                    .Value("timers_intact", timersIntact)
                    .Value("frame_start_drain", FrameClock.Aligned)
                    .ValueOrNull("overhead_floor_us_per_call", Calibration.FloorUs, 3)
                    .ValueOrNull("overhead_detour_us_per_call", Calibration.DetourUs, 3)
                    .Value("gc_mode_enabled", ProfilerCore.GcModeEnabled)
                    .Value("heap_mb", ProfilerCore.HeapMb, 1)
                    .Value("heap_peak_mb_since_reset", ProfilerCore.HeapPeakMb, 1)
                    .ValueOrNull("alloc_mb_s", GcHeapSource.Available ? ProfilerCore.AllocMbS : double.NaN, 2)
                    .Value("alloc_source", GcHeapSource.SourceName)
                .EndObject()
                .BeginObject("delta")
                    .Value("since", sinceReset ? "reset" : "export")
                    .Value("frames", deltaFrames)
                    .Value("seconds", deltaSeconds, 3)
                    .Value("previous_file", sinceReset ? null : previousFile)
                    .Value("valid", deltaValid)
                .EndObject()
                .BeginArray("mods");
            foreach (ModRow mod in mods)
            {
                json.BeginObject()
                    .Value("name", mod.Name)
                    .Value("methods", mod.Methods)
                    .Value("methods_new", mod.MethodsNew)
                    .Value("calls", mod.Calls)
                    .Value("total_ms", mod.TotalMs, 3)
                    .Value("ms_per_frame", mod.MsPerFrame, 5)
                    .Value("calls_per_frame", mod.CallsPerFrame, 4)
                    .ValueOrNull("calls_delta", mod.HasDelta ? mod.CallsDelta : double.NaN, 0)
                    .ValueOrNull("total_ms_delta", mod.HasDelta ? mod.MsDelta : double.NaN, 3)
                    .ValueOrNull("ms_per_frame_delta", mod.HasDelta ? mod.MsPerFrameDelta : double.NaN, 5)
                    .ValueOrNull("calls_per_frame_delta", mod.HasDelta ? mod.CallsPerFrameDelta : double.NaN, 4)
                .EndObject();
            }
            json.EndArray()
                .BeginArray("spikes");
            foreach (ProfilerCore.SpikeEntry spike in ProfilerCore.Spikes)
            {
                json.BeginObject()
                    .Value("time", spike.Time)
                    .Value("frame", spike.Frame)
                    .Value("seconds", spike.Seconds, 3)
                    .Value("frame_ms", spike.FrameMs, 1)
                    .Value("gc", spike.Gc)
                    .BeginArray("top");
                foreach (ProfilerCore.SpikeContributor top in spike.Top)
                    json.BeginObject()
                        .Value("mod", top.Mod)
                        .Value("ms", top.Ms, 2)
                        .Value("method", top.Method)
                        .Value("method_ms", top.MethodMs, 2)
                    .EndObject();
                json.EndArray().EndObject();
            }
            json.EndArray()
                .BeginArray("growing");
            foreach (ModStats mod in ProfilerCore.Mods)
                foreach (MethodStats m in mod.Methods)
                {
                    if (m.Growth == null || !m.Growth.Growing)
                        continue;
                    json.BeginObject()
                        .Value("mod", mod.Name)
                        .Value("method", m.DisplayName)
                        .Value("from_per_frame", m.Growth.From, 2)
                        .Value("to_per_frame", m.Growth.To, 2)
                        .Value("measured_minutes", Math.Max(0.0, (ProfilerCore.MeasuredSeconds - m.Growth.SinceSeconds) / 60.0), 1)
                    .EndObject();
                }
            json.EndArray()
                .BeginArray("patch_failures");
            foreach (ProfilerCore.PatchFailure failure in ProfilerCore.PatchFailureDetails)
                json.BeginObject()
                    .Value("mod", failure.Mod)
                    .Value("method", failure.Method)
                    .Value("reason", failure.Reason)
                .EndObject();
            json.EndArray()
                .EndObject();
            sb.AppendLine();
            return sb.ToString();
        }

        private static string gameVersion;
        private static string GameVersion()
        {
            if (gameVersion != null)
                return gameVersion;
            try
            {
                gameVersion = FileVersionInfo.GetVersionInfo(BepInEx.Paths.ExecutablePath).FileVersion;
            }
            catch
            {
                gameVersion = null;
            }
            if (string.IsNullOrEmpty(gameVersion))
                gameVersion = Application.version;
            return gameVersion;
        }

        private static string PluginVersion(string guid)
        {
            return Chainloader.PluginInfos.TryGetValue(guid, out PluginInfo info) && info.Metadata != null
                ? info.Metadata.Version.ToString()
                : null;
        }

        private static StringBuilder Quote(StringBuilder sb, string value)
        {
            return sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }

        private static StringBuilder Num(StringBuilder sb, double value, int decimals)
        {
            JsonBuilder.AppendNumber(sb, value, decimals);
            return sb;
        }
    }
}
