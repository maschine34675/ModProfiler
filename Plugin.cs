using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ModProfiler.Profiling;
using ModProfiler.UI;
using UnityEngine;

namespace ModProfiler
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(WebOverlayGate.LibraryGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.maschine.ModProfiler";
        public const string PluginName = "maschine-ModProfiler";
        public const string PluginVersion = "1.4.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<bool> ProfileHarmonyPatches;
        internal static ConfigEntry<bool> ProfileMonoBehaviours;
        internal static ConfigEntry<bool> ProfileCoroutines;
        internal static ConfigEntry<bool> ProfileVanilla;
        internal static ConfigEntry<bool> IncludeSptPlugins;
        internal static ConfigEntry<int> TopMethodsPerMod;
        internal static ConfigEntry<float> RefreshInterval;
        internal static ConfigEntry<bool> PreferWebUi;
        internal static ConfigEntry<bool> LogInputState;
        internal static ConfigEntry<float> SpikeThresholdMs;
        internal static ConfigEntry<float> AutoExportIntervalSeconds;
        internal static ConfigEntry<bool> CaptureHeadless;

        private void Awake()
        {
            Log = Logger;
            ProfilerCore.MainThreadId = Environment.CurrentManagedThreadId;
            GcHeapSource.Validate();
            ToggleKey = Config.Bind("General", "ToggleOverlay", new KeyboardShortcut(KeyCode.F11),
                Tagged("Profiler Hotkey", 10,
                    "Opens/closes the profiler overlay. The first activation instruments all mod code, " +
                    "which can freeze the game for a few seconds."));
            LogInputState = Config.Bind("Diagnostics", "LogInputState", false,
                Advanced("Log Input State", 0,
                    "Writes one line whenever this mod's view of the foreground window or its"
                    + " muting of the game's input changes. Only useful when reporting a problem"
                    + " with the mouse or keyboard."));
            PreferWebUi = Config.Bind("UI", "PreferWebOverlay", true,
                Tagged("Use Web Overlay Window", 50,
                    "Show the profiler in an Anvil-WebOverlay window when that library is installed " +
                    "(movable, sortable, filterable, with trend graphs). The built-in overlay " +
                    "remains the fallback and can be forced by turning this off. Read once at startup."));
            RefreshInterval = Config.Bind("UI", "RefreshInterval", 0.5f,
                Tagged("Refresh Interval (s)", 40,
                    "Seconds between overlay table refreshes.",
                    new AcceptableValueRange<float>(0.1f, 5f)));
            TopMethodsPerMod = Config.Bind("UI", "TopMethodsPerMod", 15,
                Tagged("Methods Per Expanded Mod", 30,
                    "How many of the most expensive methods to list when a mod row is expanded.",
                    new AcceptableValueRange<int>(1, 100)));
            SpikeThresholdMs = Config.Bind("UI", "SpikeThresholdMs", 30f,
                Tagged("Spike Threshold (ms)", 20,
                    "Frames slower than this (and at least 1.5x the current average) are " +
                    "recorded in the web window's spike log together with their top contributors. 0 disables.",
                    new AcceptableValueRange<float>(0f, 1000f)));

            AutoExportIntervalSeconds = Config.Bind("CSV", "AutoExportIntervalSeconds", 0f,
                Tagged("Auto-Export Every (s)", 10,
                    "Writes a CSV export on its own every this many seconds of measuring, into the same " +
                    "folder as the CSV button. Time with the profiler closed or paused does not count, " +
                    "except while a headless capture is measuring. " +
                    "0 turns it off; values below 10 count as 10.",
                    new AcceptableValueRange<float>(0f, 3600f)));
            CaptureHeadless = Config.Bind("Capture", "Headless", false,
                Advanced("Headless Capture", 10,
                    "Measures without the profiler window, until this is switched off again - for automated " +
                    "test runs that read the exports. Switching it on scans all mod code first, which can " +
                    "freeze the game for a few seconds, and writes an export at the beginning and at the end; " +
                    "CSV/AutoExportIntervalSeconds adds exports in between. Pause has no effect meanwhile. " +
                    "Applies to the running game only: it is off again whenever the game starts."));
            if (CaptureHeadless.Value)
            {
                CaptureHeadless.Value = false;
                Log.LogInfo("Capture/Headless was still on from an earlier session and has been switched off - "
                    + "it only ever applies to the running game.");
            }

            ProfileHarmonyPatches = Config.Bind("Profiling", "HarmonyPatches", true,
                Tagged("Measure Harmony Patches", 40,
                    "Measure every Harmony prefix/postfix/finalizer that other mods have applied."));
            ProfileMonoBehaviours = Config.Bind("Profiling", "MonoBehaviours", true,
                Tagged("Measure Unity Frame Methods", 30,
                    "Measure Update/FixedUpdate/LateUpdate/OnGUI of MonoBehaviours defined in mod assemblies."));
            ProfileCoroutines = Config.Bind("Profiling", "CoroutinesAndAsync", true,
                Tagged("Measure Coroutines And Async", 20,
                    "Measure coroutine/async state machine steps (MoveNext) defined in mod assemblies."));
            ProfileVanilla = Config.Bind("Profiling", "VanillaEngineMethods", true,
                Tagged("Measure EFT Itself", 5,
                    "Measure a curated set of the game's own per-frame loops (world tick, bot AI, " +
                    "player update, ballistics, the interaction prompt) under 'EFT (vanilla)', so a " +
                    "spike can be told apart from mod work. Two of these seven rows are each already " +
                    "counted inside one of the other five; see the row tooltip."));
            IncludeSptPlugins = Config.Bind("Profiling", "IncludeSptCorePlugins", true,
                Tagged("Include SPT Core Plugins", 10,
                    "Also profile the SPT core plugins (BepInEx/plugins/spt). Turning this off only affects " +
                    "methods that have not been instrumented yet; a restart removes existing instrumentation."));

            GameInputBlocker.Apply();
            gameObject.AddComponent<ProfilerWindow>();
            gameObject.AddComponent<WebPanelController>();

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Press {ToggleKey.Value} to open the profiler overlay.");
        }

        private static ConfigDescription Tagged(string displayName, int order, string description)
        {
            return Tagged(displayName, order, description, null);
        }
        private static ConfigDescription Advanced(string displayName, int order, string description)
        {
            return new ConfigDescription(
                description,
                null,
                new ConfigurationManagerAttributes
                {
                    DispName = displayName,
                    Order = order,
                    IsAdvanced = true,
                    ShowRangeAsPercent = false,
                });
        }

        private static ConfigDescription Tagged(string displayName, int order, string description,
            AcceptableValueBase acceptableValues)
        {
            return new ConfigDescription(
                description,
                acceptableValues,
                new ConfigurationManagerAttributes
                {
                    DispName = displayName,
                    Order = order,
                    ShowRangeAsPercent = false,
                });
        }
    }
}
