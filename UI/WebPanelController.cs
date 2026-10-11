using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ModProfiler.Profiling;
using UnityEngine;

namespace ModProfiler.UI
{
    internal sealed class WebPanelController : MonoBehaviour
    {
        internal static volatile bool OwnsHotkey;

        private bool shown;
        private float nextSnapshot;
        private readonly StringBuilder json = new StringBuilder(16 * 1024);
        private readonly StringBuilder reply = new StringBuilder(4 * 1024);
        private readonly List<MethodStats> methodScratch = new List<MethodStats>();
        private static string pageHtml;
        private bool blockingGameInput;

        private void Start()
        {
            if (!Plugin.PreferWebUi.Value || !WebOverlayGate.IsLoaded)
            {
                enabled = false;
                return;
            }
            if (!WebOverlayGate.IsUsable)
            {
                Plugin.Log.LogInfo("Anvil-WebOverlay " + WebOverlayGate.FoundVersion + " found, but "
                    + WebOverlayGate.MinimumVersion + " or newer is needed for the web window - using the IMGUI overlay.");
                enabled = false;
                return;
            }
            OwnsHotkey = true;
            Plugin.Log.LogDebug("Anvil-WebOverlay " + WebOverlayGate.FoundVersion + " found; the profiler uses the web window.");
        }

        private void Update()
        {
            WebOverlayGate.Pump();
            bool live = WebOverlayGate.IsVisible();
            if (live != shown)
            {
                shown = live;
                ProfilerCore.SetActive(live);
                if (live)
                    nextSnapshot = 0f;
            }
            if (CsvExport.LastExportMessage != lastExportShown)
            {
                lastExportShown = CsvExport.LastExportMessage;
                nextSnapshot = 0f;
            }

            ManageRaidInput();
            ReportInputState();

            if (Hotkey.IsPressed(Plugin.ToggleKey.Value))
                TogglePanel();
            if (shown && ProfilerCore.Instrumented && ProfilerCore.FramesObserved > 0
                && Time.unscaledTime >= nextSnapshot)
            {
                nextSnapshot = Time.unscaledTime + Plugin.RefreshInterval.Value;
                WebOverlayGate.Post("snap", BuildSnapshot());
            }
        }
        private void ManageRaidInput()
        {
            bool panelHoldsFocus = shown && ForegroundProbe.OverlayInFront();
            if (panelHoldsFocus)
            {
                if (!blockingGameInput)
                {
                    blockingGameInput = true;
                    GameInputBlocker.Blocked = true;
                }
            }
            else
                ReleaseGameInput();
        }
        private void ReportInputState()
        {
            if (!Plugin.LogInputState.Value)
                return;
            string state = "shown=" + shown
                + " overlayInFront=" + ForegroundProbe.OverlayInFront()
                + " blocking=" + blockingGameInput
                + " blocked=" + GameInputBlocker.Blocked
                + " gameWindow=" + ForegroundProbe.GameWindowHandle.ToString("X")
                + " foreground=" + ForegroundProbe.ForegroundHandle.ToString("X");
            if (state == lastInputState)
                return;
            lastInputState = state;
            Plugin.Log.LogInfo("input: " + state);
        }

        private string lastInputState;
        private string lastExportShown;
        private void ReleaseGameInput()
        {
            if (!blockingGameInput)
                return;
            blockingGameInput = false;
            GameInputBlocker.Blocked = false;
        }

        private void OnDestroy()
        {
            ReleaseGameInput();
            if (WebOverlayGate.IsUsable)
                WebOverlayGate.Shutdown();
        }

        private void TogglePanel()
        {
            if (!shown)
            {
                if (!ProfilerCore.Instrumented)
                {
                    Plugin.Log.LogInfo("first activation - instrumenting all mod code, this can take a moment...");
                    Instrumenter.InstrumentAll();
                }
                if (!ProfilerCore.Active)
                    ProfilerCore.ResetWindows();
            }

            string html = LoadPage();
            if (html == null || !WebOverlayGate.Toggle(html, 960, 640,
                    Plugin.ToggleKey.Value,
                    onChannel: OnChannel,
                    onRequest: OnRequest,
                    onFailed: HandOverToImgui,
                    logWarning: w => Plugin.Log.LogWarning(w)))
            {
                HandOverToImgui();
                return;
            }

            shown = !shown;
            ProfilerCore.SetActive(shown);
            if (shown)
                nextSnapshot = 0f;
        }
        private void HandOverToImgui()
        {
            OwnsHotkey = false;
            shown = false;
            ProfilerCore.SetActive(false);
            ReleaseGameInput();
            enabled = false;
        }

        private static string LoadPage()
        {
            if (pageHtml != null)
                return pageHtml;
            try
            {
                using (Stream stream = typeof(WebPanelController).Assembly
                           .GetManifestResourceStream("ModProfiler.profiler.html"))
                {
                    if (stream == null)
                        throw new FileNotFoundException("embedded resource ModProfiler.profiler.html");
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        pageHtml = reader.ReadToEnd();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("could not load the profiler page: " + e.Message);
            }
            return pageHtml;
        }
        private void OnChannel(string channel, string payload)
        {
            if (channel != "cmd" || payload == null)
                return;
            switch (payload)
            {
                case "pause":
                    ProfilerCore.SetActive(false);
                    nextSnapshot = 0f;
                    break;
                case "resume":
                    ProfilerCore.SetActive(shown);
                    nextSnapshot = 0f;
                    break;
                case "reset":
                    ProfilerCore.ResetAll();
                    nextSnapshot = 0f;
                    break;
                case "close":
                    if (shown)
                        TogglePanel();
                    break;
            }
        }
        private string OnRequest(string channel, string payload)
        {
            if (channel == "methods")
                return BuildMethods(payload);
            if (channel == "failures")
                return BuildFailures();
            if (channel != "action")
                return null;
            switch (payload)
            {
                case "csv":
                    try
                    {
                        return CsvExport.Begin(CsvExport.TriggerManual);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("CSV export failed: " + e.Message);
                        return "CSV export failed: " + e.Message;
                    }
                case "rescan":
                    if (lastRescanFrame != Time.frameCount)
                    {
                        lastRescanFrame = Time.frameCount;
                        Instrumenter.InstrumentAll();
                        nextSnapshot = 0f;
                    }
                    return "rescan complete: " + ProfilerCore.StatsByMethod.Count + " methods instrumented";
                default:
                    return null;
            }
        }

        private int lastRescanFrame = -1;
        private string BuildFailures()
        {
            reply.Length = 0;
            reply.Append('[');
            List<ProfilerCore.PatchFailure> failures = ProfilerCore.PatchFailureDetails;
            for (int i = 0; i < failures.Count; i++)
            {
                if (i > 0)
                    reply.Append(',');
                reply.Append("{\"m\":");
                AppendJsonString(reply, failures[i].Mod);
                reply.Append(",\"n\":");
                AppendJsonString(reply, failures[i].Method);
                reply.Append(",\"r\":");
                AppendJsonString(reply, failures[i].Reason);
                reply.Append('}');
            }
            reply.Append(']');
            return reply.ToString();
        }

        private string BuildMethods(string modName)
        {
            ModStats mod = null;
            if (modName != null)
                foreach (ModStats candidate in ProfilerCore.Mods)
                    if (candidate.Name == modName)
                    {
                        mod = candidate;
                        break;
                    }
            if (mod == null)
                return "[]";

            int frames = Math.Max(1, ProfilerCore.FramesObserved);
            methodScratch.Clear();
            methodScratch.AddRange(mod.Methods);
            methodScratch.Sort((a, b) => b.WindowAvgMs(frames).CompareTo(a.WindowAvgMs(frames)));
            int n = Math.Min(Plugin.TopMethodsPerMod.Value, methodScratch.Count);
            for (int i = n; i < methodScratch.Count; i++)
                if (methodScratch[i].Growth != null && methodScratch[i].Growth.Growing)
                    methodScratch[n++] = methodScratch[i];

            reply.Length = 0;
            reply.Append('[');
            for (int i = 0; i < n; i++)
            {
                MethodStats m = methodScratch[i];
                if (i > 0)
                    reply.Append(',');
                reply.Append("{\"n\":");
                AppendJsonString(reply, m.DisplayName);
                reply.Append(",\"c\":\"").Append(m.Category.Length > 0 ? m.Category[0] : '?').Append('"');
                if (m.NestedUnder != null)
                {
                    reply.Append(",\"nu\":");
                    AppendJsonString(reply, m.NestedUnder);
                }
                AppendNumbers(reply, m.WindowAvgMs(frames), m.WindowMaxMs(frames),
                    m.WindowCallsPerFrame(frames), m.TotalTicks * ProfilerCore.MsPerTick);
                if (m.Growth != null && m.Growth.Growing)
                {
                    reply.Append(",\"g\":");
                    AppendGrowth(reply, m.Growth);
                }
                reply.Append('}');
            }
            reply.Append(']');
            return reply.ToString();
        }
        private static void AppendGrowth(StringBuilder sb, GrowthTracker growth)
        {
            sb.Append('[');
            AppendGrowthValues(sb, growth);
            sb.Append(']');
        }
        private static void AppendGrowthValues(StringBuilder sb, GrowthTracker growth)
        {
            double minutes = Math.Max(0.0, (ProfilerCore.MeasuredSeconds - growth.SinceSeconds) / 60.0);
            sb.Append(growth.From.ToString("F1", CultureInfo.InvariantCulture))
              .Append(',').Append(growth.To.ToString("F1", CultureInfo.InvariantCulture))
              .Append(',').Append(minutes.ToString("F1", CultureInfo.InvariantCulture));
        }
        private string BuildSnapshot()
        {
            int frames = Math.Max(1, ProfilerCore.FramesObserved);

            json.Length = 0;
            json.Append("{\"frameMs\":").Append(ProfilerCore.AverageFrameMs().ToString("F2", CultureInfo.InvariantCulture))
                .Append(",\"active\":").Append(ProfilerCore.Active ? "true" : "false")
                .Append(",\"headless\":").Append(ProfilerCore.CaptureHeld ? "true" : "false")
                .Append(",\"methods\":").Append(ProfilerCore.StatsByMethod.Count)
                .Append(",\"frames\":").Append(frames)
                .Append(",\"failures\":").Append(ProfilerCore.PatchFailures)
                .Append(",\"gc\":").Append(ProfilerCore.GcCount())
                .Append(",\"gcOn\":").Append(ProfilerCore.GcModeEnabled ? "true" : "false")
                .Append(",\"gcRate\":").Append(ProfilerCore.GcPerSecond.ToString("F2", CultureInfo.InvariantCulture))
                .Append(",\"heapMb\":").Append(ProfilerCore.HeapMb.ToString("F1", CultureInfo.InvariantCulture))
                .Append(",\"heapPeakMb\":").Append(ProfilerCore.HeapPeakMb.ToString("F1", CultureInfo.InvariantCulture))
                .Append(",\"allocMbS\":").Append(GcHeapSource.Available
                    ? ProfilerCore.AllocMbS.ToString("F2", CultureInfo.InvariantCulture)
                    : "null")
                .Append(",\"allocSource\":\"").Append(GcHeapSource.SourceName).Append('"')
                .Append(",\"measuredFrames\":").Append(ProfilerCore.TotalFrames)
                .Append(",\"measuredSeconds\":").Append(ProfilerCore.MeasuredSeconds.ToString("F1", CultureInfo.InvariantCulture))
                .Append(",\"lastExport\":");
            AppendJsonString(json, CsvExport.LastExportMessage);
            json.Append(",\"lastExportAt\":");
            AppendJsonString(json, CsvExport.LastExportAt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            if (!double.IsNaN(Calibration.FloorUs))
                json.Append(",\"floorUs\":").Append(Calibration.FloorUs.ToString("F3", CultureInfo.InvariantCulture));
            json.Append(",\"spikes\":[");

            int firstSpike = Math.Max(0, ProfilerCore.Spikes.Count - ProfilerCore.SpikesInSnapshot);
            for (int i = firstSpike; i < ProfilerCore.Spikes.Count; i++)
            {
                ProfilerCore.SpikeEntry spike = ProfilerCore.Spikes[i];
                if (i > firstSpike)
                    json.Append(',');
                json.Append("{\"t\":\"").Append(spike.Time).Append("\",\"ms\":")
                    .Append(spike.FrameMs.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(",\"gc\":").Append(spike.Gc ? "true" : "false")
                    .Append(",\"top\":[");
                for (int k = 0; k < spike.Top.Count; k++)
                {
                    ProfilerCore.SpikeContributor top = spike.Top[k];
                    if (k > 0)
                        json.Append(',');
                    json.Append('[');
                    AppendJsonString(json, top.Mod);
                    json.Append(',').Append(top.Ms.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                    AppendJsonString(json, top.Method);
                    json.Append(',').Append(top.MethodMs.ToString("F1", CultureInfo.InvariantCulture)).Append(']');
                }
                json.Append("]}");
            }

            json.Append("],\"mods\":[");

            bool firstMod = true;
            foreach (ModStats mod in ProfilerCore.Mods)
            {
                if (mod.Methods.Count == 0)
                    continue;
                if (!firstMod)
                    json.Append(',');
                firstMod = false;

                json.Append("{\"n\":");
                AppendJsonString(json, mod.Name);
                AppendNumbers(json, mod.WindowAvgMs(frames), mod.WindowMaxMs(frames),
                    mod.WindowCallsPerFrame(frames), mod.TotalTicks * ProfilerCore.MsPerTick);
                bool anyGrowth = false;
                foreach (MethodStats m in mod.Methods)
                {
                    if (m.Growth == null || !m.Growth.Growing)
                        continue;
                    json.Append(anyGrowth ? "," : ",\"grow\":[");
                    anyGrowth = true;
                    json.Append('[');
                    AppendJsonString(json, m.DisplayName);
                    json.Append(',');
                    AppendGrowthValues(json, m.Growth);
                    json.Append(']');
                }
                if (anyGrowth)
                    json.Append(']');
                json.Append('}');
            }

            json.Append("]}");
            return json.ToString();
        }

        private static void AppendNumbers(StringBuilder sb, double avg, double max, double calls, double total)
        {
            sb.Append(",\"avg\":").Append(avg.ToString("F4", CultureInfo.InvariantCulture))
              .Append(",\"max\":").Append(max.ToString("F3", CultureInfo.InvariantCulture))
              .Append(",\"calls\":").Append(calls.ToString("F1", CultureInfo.InvariantCulture))
              .Append(",\"total\":").Append(total.ToString("F0", CultureInfo.InvariantCulture));
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            JsonBuilder.AppendString(sb, value);
        }
    }
}
