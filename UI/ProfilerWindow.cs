using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using ModProfiler.Profiling;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ModProfiler.UI
{
    internal sealed class ProfilerWindow : MonoBehaviour
    {
        private enum SortColumn
        {
            Name,
            Avg,
            Max,
            Calls,
            Total
        }

        private sealed class MethodRow
        {
            internal string Name;
            internal string Growth;
            internal string NestedUnder;
            internal string Category;
            internal double AvgMs;
            internal double MaxMs;
            internal double Calls;
            internal double TotalMs;
        }

        private sealed class ModRow
        {
            internal ModStats Mod;
            internal double AvgMs;
            internal double MaxMs;
            internal double Calls;
            internal double Percent;
            internal double TotalMs;
            internal bool Growing;
            internal List<MethodRow> Methods = new List<MethodRow>();
        }

        private const int WindowId = 0x4D505246;

        private const float NameWidth = 340f;
        private const float NumWidth = 72f;
        private const float TotalWidth = 92f;

        private bool _visible;
        private bool _mouseMode;
        private EventSystem _suppressedEventSystem;
        private Rect _rect = new Rect(80f, 80f, 880f, 560f);
        private Vector2 _scroll;
        private readonly HashSet<ModStats> _expanded = new HashSet<ModStats>();
        private SortColumn _sort = SortColumn.Avg;
        private bool _sortDesc = true;
        private readonly List<ModRow> _rows = new List<ModRow>();
        private float _nextRefresh;
        private string _headerText = "";
        private string _memText = "";
        private string _statusText = "";
        private float _statusUntil;

        private GUIStyle _rowStyle;
        private GUIStyle _numStyle;
        private GUIStyle _methodStyle;
        private GUIStyle _methodNumStyle;
        private GUIStyle _headStyle;

        private void Start()
        {
            Plugin.Log.LogInfo($"profiler overlay component running (hotkey: {Plugin.ToggleKey.Value})");
        }

        private void Update()
        {
            FrameClock.OnUpdate();
            if (!WebPanelController.OwnsHotkey && Hotkey.IsPressed(Plugin.ToggleKey.Value))
                Toggle();
            HeadlessCapture.Poll();
            string exportMessage = CsvExport.Poll();
            if (exportMessage != null)
                SetStatus(exportMessage);
            CsvExport.AutoTick();

            if (!_visible)
                return;

            UpdateEventSystemSuppression();

            if (Time.unscaledTime >= _nextRefresh)
            {
                RefreshRows();
                _nextRefresh = Time.unscaledTime + Plugin.RefreshInterval.Value;
            }
        }
        private void FixedUpdate()
        {
            ProfilerCore.CountFixedStep();
        }
        private void SetStatus(string text)
        {
            _statusText = text;
            _statusUntil = Time.unscaledTime + 10f;
        }


        private void OnDestroy()
        {
            SetMouseMode(false);
            RestoreEventSystem();
        }

        private void SetMouseMode(bool on)
        {
            if (on != _mouseMode)
                CursorRequestNode.Want(on);
            _mouseMode = on;
            GameInputBlocker.Blocked = on;
        }
        private void UpdateEventSystemSuppression()
        {
            bool suppress = _mouseMode;
            if (!suppress && Cursor.visible && Cursor.lockState != CursorLockMode.Locked)
            {
                Vector3 mouse = Input.mousePosition;
                suppress = _rect.Contains(new Vector2(mouse.x, Screen.height - mouse.y));
            }

            if (suppress)
            {
                if (_suppressedEventSystem != null)
                    return;
                EventSystem current = EventSystem.current;
                if (current != null && current.enabled)
                {
                    current.enabled = false;
                    _suppressedEventSystem = current;
                }
            }
            else
            {
                RestoreEventSystem();
            }
        }

        private void RestoreEventSystem()
        {
            if (_suppressedEventSystem == null)
                return;
            if (_suppressedEventSystem)
                _suppressedEventSystem.enabled = true;
            _suppressedEventSystem = null;
        }

        private void Toggle()
        {
            if (_visible)
            {
                _visible = false;
                ProfilerCore.SetActive(false);
                SetMouseMode(false);
                RestoreEventSystem();
                return;
            }

            if (!ProfilerCore.Instrumented)
            {
                Plugin.Log.LogInfo("first activation - instrumenting all mod code, this can take a moment...");
                Instrumenter.InstrumentAll();
            }
            if (!ProfilerCore.Active)
                ProfilerCore.ResetWindows();
            ProfilerCore.SetActive(true);
            _visible = true;
            _nextRefresh = 0f;
        }

        private void RefreshRows()
        {
            int frames = Math.Max(1, ProfilerCore.FramesObserved);
            double frameAvgMs = ProfilerCore.AverageFrameMs();

            _rows.Clear();
            double profiledAvgMs = 0.0;
            foreach (ModStats mod in ProfilerCore.Mods)
            {
                if (mod.Methods.Count == 0)
                    continue;

                var row = new ModRow
                {
                    Mod = mod,
                    AvgMs = mod.WindowAvgMs(frames),
                    MaxMs = mod.WindowMaxMs(frames),
                    Calls = mod.WindowCallsPerFrame(frames),
                    TotalMs = mod.TotalTicks * ProfilerCore.MsPerTick
                };
                row.Percent = frameAvgMs > 0.0 ? row.AvgMs / frameAvgMs * 100.0 : 0.0;
                profiledAvgMs += row.AvgMs;

                foreach (MethodStats m in mod.Methods)
                    if (m.Growth != null && m.Growth.Growing)
                        row.Growing = true;

                if (_expanded.Contains(mod))
                {
                    var methods = new List<MethodRow>(mod.Methods.Count);
                    foreach (MethodStats m in mod.Methods)
                    {
                        methods.Add(new MethodRow
                        {
                            Name = m.DisplayName,
                            NestedUnder = m.NestedUnder,
                            Growth = m.Growth != null && m.Growth.Growing
                                ? $"            growing: {m.Growth.From:F1} → {m.Growth.To:F1} per frame over " +
                                  $"{Math.Max(0.0, (ProfilerCore.MeasuredSeconds - m.Growth.SinceSeconds) / 60.0):F0} measured min" +
                                  " - a leak candidate if it never falls back"
                                : null,
                            Category = m.Category,
                            AvgMs = m.WindowAvgMs(frames),
                            MaxMs = m.WindowMaxMs(frames),
                            Calls = m.WindowCallsPerFrame(frames),
                            TotalMs = m.TotalTicks * ProfilerCore.MsPerTick
                        });
                    }
                    methods.Sort((a, b) => b.AvgMs.CompareTo(a.AvgMs));
                    int n = Math.Min(Plugin.TopMethodsPerMod.Value, methods.Count);
                    for (int i = n; i < methods.Count; i++)
                        if (methods[i].Growth != null)
                            methods[n++] = methods[i];
                    row.Methods = methods.GetRange(0, n);
                }

                _rows.Add(row);
            }

            SortRows();

            double fps = frameAvgMs > 0.0 ? 1000.0 / frameAvgMs : 0.0;
            TimeSpan measured = TimeSpan.FromSeconds(ProfilerCore.MeasuredSeconds);
            _headerText =
                $"Frame Ø {frameAvgMs:F1} ms ({fps:F0} FPS)  |  profiled mods Σ {profiledAvgMs:F2} ms/frame  |  " +
                $"{ProfilerCore.StatsByMethod.Count} methods, window {frames}, measured {ProfilerCore.TotalFrames:N0} frames " +
                $"/ {(int)measured.TotalMinutes}:{measured.Seconds:00}" +
                (CsvExport.LastExportAt.HasValue ? $"  |  exported {CsvExport.LastExportAt.Value:HH:mm:ss}" : "") +
                (ProfilerCore.PatchFailures > 0 ? $"  |  {ProfilerCore.PatchFailures} patch failures" : "");
            _memText =
                $"GC {(ProfilerCore.GcModeEnabled ? "on" : "off")}" +
                (ProfilerCore.GcModeEnabled ? $" {ProfilerCore.GcPerSecond:F1}/s" : "") +
                $"  |  heap {ProfilerCore.HeapMb:F0} MB (peak {ProfilerCore.HeapPeakMb:F0})" +
                (GcHeapSource.Available
                    ? $", alloc {ProfilerCore.AllocMbS:F1} MB/s" + (GcHeapSource.PerThreadCounter ? "" : " (heap delta)")
                    : ", alloc rate n/a") +
                (ProfilerCore.CaptureHeld ? "  |  headless capture running (Capture/Headless)" : "");
        }

        private void SortRows()
        {
            Comparison<ModRow> cmp;
            switch (_sort)
            {
                case SortColumn.Name:
                    cmp = (a, b) => string.Compare(a.Mod.Name, b.Mod.Name, StringComparison.OrdinalIgnoreCase);
                    break;
                case SortColumn.Max:
                    cmp = (a, b) => a.MaxMs.CompareTo(b.MaxMs);
                    break;
                case SortColumn.Calls:
                    cmp = (a, b) => a.Calls.CompareTo(b.Calls);
                    break;
                case SortColumn.Total:
                    cmp = (a, b) => a.TotalMs.CompareTo(b.TotalMs);
                    break;
                default:
                    cmp = (a, b) => a.AvgMs.CompareTo(b.AvgMs);
                    break;
            }
            _rows.Sort(cmp);
            if (_sortDesc)
                _rows.Reverse();
        }

        private void OnGUI()
        {
            if (!_visible)
                return;
            EnsureStyles();
            _rect = GUI.Window(WindowId, _rect, DrawWindow, "ModProfiler — CPU time per mod");
        }

        private void EnsureStyles()
        {
            if (_rowStyle != null)
                return;
            _rowStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _numStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleRight };
            _methodStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            _methodStyle.normal.textColor = new Color(0.72f, 0.72f, 0.72f);
            _methodNumStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleRight };
            _methodNumStyle.normal.textColor = new Color(0.72f, 0.72f, 0.72f);
            _headStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !ProfilerCore.CaptureHeld;
            if (GUILayout.Button(ProfilerCore.Active ? "Pause" : "Resume", GUILayout.Width(70f)))
                ProfilerCore.SetActive(!ProfilerCore.Active);
            GUI.enabled = wasEnabled;
            if (GUILayout.Button("Rescan", GUILayout.Width(70f)))
            {
                Instrumenter.InstrumentAll();
                _nextRefresh = 0f;
            }
            if (GUILayout.Button("Reset", GUILayout.Width(70f)))
            {
                ProfilerCore.ResetAll();
                _nextRefresh = 0f;
            }
            if (GUILayout.Button("CSV", GUILayout.Width(60f)))
                ExportCsv();
            string mouseLabel = _mouseMode ? "Mouse ✓" : "Mouse";
            if (GUILayout.Button(mouseLabel, GUILayout.Width(80f)))
                SetMouseMode(!_mouseMode);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(26f)))
                Toggle();
            GUILayout.EndHorizontal();
            GUILayout.Label(_headerText, _headStyle);
            GUILayout.Label(_memText, _headStyle);

            DrawHeaderRow();

            _scroll = GUILayout.BeginScrollView(_scroll);
            for (int i = 0; i < _rows.Count; i++)
                DrawModRow(_rows[i]);
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_statusText) && Time.unscaledTime < _statusUntil)
                GUILayout.Label(_statusText, _headStyle);

            GUI.DragWindow(new Rect(0f, 0f, 100000f, 20f));
        }

        private void DrawHeaderRow()
        {
            GUILayout.BeginHorizontal();
            SortButton("Mod", SortColumn.Name, NameWidth);
            SortButton("Ø ms", SortColumn.Avg, NumWidth);
            SortButton("Max ms", SortColumn.Max, NumWidth);
            SortButton("Calls/F", SortColumn.Calls, NumWidth);
            SortButton("% Frame", SortColumn.Avg, NumWidth);
            SortButton("Σ ms", SortColumn.Total, TotalWidth);
            GUILayout.EndHorizontal();
        }

        private void SortButton(string label, SortColumn column, float width)
        {
            string text = _sort == column ? label + (_sortDesc ? " ▼" : " ▲") : label;
            if (GUILayout.Button(text, GUILayout.Width(width)))
            {
                if (_sort == column)
                {
                    _sortDesc = !_sortDesc;
                }
                else
                {
                    _sort = column;
                    _sortDesc = column != SortColumn.Name;
                }
                SortRows();
            }
        }

        private void DrawModRow(ModRow row)
        {
            bool expanded = _expanded.Contains(row.Mod);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button((expanded ? "▼ " : "▶ ") + (row.Growing ? "▲ " : "") + row.Mod.Name, _rowStyle, GUILayout.Width(NameWidth)))
            {
                if (expanded)
                    _expanded.Remove(row.Mod);
                else
                    _expanded.Add(row.Mod);
                _nextRefresh = 0f;
            }
            GUILayout.Label(row.AvgMs.ToString("F2"), _numStyle, GUILayout.Width(NumWidth));
            GUILayout.Label(row.MaxMs.ToString("F2"), _numStyle, GUILayout.Width(NumWidth));
            GUILayout.Label(row.Calls.ToString("F1"), _numStyle, GUILayout.Width(NumWidth));
            GUILayout.Label(row.Percent.ToString("F1") + " %", _numStyle, GUILayout.Width(NumWidth));
            GUILayout.Label(row.TotalMs.ToString("F0"), _numStyle, GUILayout.Width(TotalWidth));
            GUILayout.EndHorizontal();

            if (!expanded)
                return;

            for (int i = 0; i < row.Methods.Count; i++)
            {
                MethodRow m = row.Methods[i];
                GUILayout.BeginHorizontal();
                string prefix = m.NestedUnder != null ? "↳ " : (m.Growth != null ? "▲ " : "");
                GUILayout.Label("      [" + m.Category[0] + "] " + prefix + m.Name, _methodStyle, GUILayout.Width(NameWidth));
                GUILayout.Label(m.AvgMs.ToString("F3"), _methodNumStyle, GUILayout.Width(NumWidth));
                GUILayout.Label(m.MaxMs.ToString("F3"), _methodNumStyle, GUILayout.Width(NumWidth));
                GUILayout.Label(m.Calls.ToString("F1"), _methodNumStyle, GUILayout.Width(NumWidth));
                GUILayout.Label("", _methodNumStyle, GUILayout.Width(NumWidth));
                GUILayout.Label(m.TotalMs.ToString("F0"), _methodNumStyle, GUILayout.Width(TotalWidth));
                GUILayout.EndHorizontal();
                if (m.Growth != null)
                    GUILayout.Label(m.Growth, _methodStyle);
                if (m.NestedUnder != null)
                    GUILayout.Label($"            counted inside {m.NestedUnder}'s row above - not additional time", _methodStyle);
            }
        }

        private void ExportCsv()
        {
            try
            {
                SetStatus(CsvExport.Begin(CsvExport.TriggerManual));
            }
            catch (Exception e)
            {
                SetStatus("CSV export failed: " + e.Message);
                Plugin.Log.LogError(_statusText);
            }
        }
    }
}
