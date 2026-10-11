using System;
using System.Globalization;
using ModProfiler.UI;

namespace ModProfiler.Profiling
{
    internal static class HeadlessCapture
    {
        private static readonly CaptureSwitch Switch = new CaptureSwitch();
        internal static void Poll()
        {
            switch (Switch.Poll(Plugin.CaptureHeadless.Value))
            {
                case CaptureSwitch.Step.Start:
                    Switch.Started(Start());
                    break;
                case CaptureSwitch.Step.Stop:
                    try
                    {
                        Stop();
                    }
                    finally
                    {
                        Switch.Stopped();
                    }
                    break;
            }
        }

        private static bool Start()
        {
            try
            {
                Plugin.Log.LogInfo("headless capture requested (Capture/Headless) - scanning all mod code first, "
                    + "the game can freeze for a few seconds...");
                bool idle = !ProfilerCore.Active;
                Instrumenter.InstrumentAll();
                if (idle)
                    ProfilerCore.ResetWindows();
                ProfilerCore.SetHold(ProfilerCore.HoldHeadless, true);
                CsvExport.RestartAutoInterval();
                CsvExport.Begin(CsvExport.TriggerHeadlessStart);
                int startIndex = ProfilerCore.ExportIndex;

                float interval = Plugin.AutoExportIntervalSeconds.Value;
                string cadence = interval > 0f
                    ? "auto-export every " + Math.Max(interval, CsvExport.MinAutoExportSeconds)
                          .ToString("0.#", CultureInfo.InvariantCulture) + " s"
                    : "auto-export is off (CSV/AutoExportIntervalSeconds = 0), only the start and the final export are written";
                Plugin.Log.LogInfo("headless capture running: " + ProfilerCore.StatsByMethod.Count + " methods across "
                    + ProfilerCore.Mods.Count + " mods, exports in " + CsvExport.SessionFolder
                    + " from index " + startIndex + ", pointer file " + CsvExport.PointerFileName + ", " + cadence);
                return true;
            }
            catch (Exception e)
            {
                ProfilerCore.SetHold(ProfilerCore.HoldHeadless, false);
                Plugin.Log.LogError("headless capture could not start - switch Capture/Headless off and on again "
                    + "to retry: " + e);
                return false;
            }
        }

        private static void Stop()
        {
            ProfilerCore.SetHold(ProfilerCore.HoldHeadless, false);
            try
            {
                CsvExport.RestartAutoInterval();
                CsvExport.Begin(CsvExport.TriggerHeadlessStop);
                Plugin.Log.LogInfo("headless capture ending - writing the final export");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("headless capture ended without a final export - it could not be started: " + e.Message);
            }
        }
    }
}
