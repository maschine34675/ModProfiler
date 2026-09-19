using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using Comfort.Common;
using HarmonyLib;
using UnityEngine;

namespace ModProfiler.Profiling
{
    internal static class Instrumenter
    {
        internal const string TimerOwnerId = Plugin.PluginGuid + ".timing";

        private static Harmony _harmony;
        private static readonly HashSet<MethodBase> Patched = new HashSet<MethodBase>();
        private static readonly Dictionary<Assembly, ModStats> ModsByAssembly = new Dictionary<Assembly, ModStats>();

        private static readonly string[] UnityFrameMethods = { "Update", "FixedUpdate", "LateUpdate", "OnGUI" };

        private const BindingFlags DeclaredInstance =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        internal static int InstrumentAll()
        {
            bool wasActive = ProfilerCore.Active;
            ProfilerCore.Active = false;
            try
            {
                if (_harmony == null)
                    _harmony = new Harmony(TimerOwnerId);

                bool rescan = ProfilerCore.Instrumented;
                if (rescan)
                    ProfilerCore.RescanCount++;
                else
                    ProfilerCore.GcCollectionsAtReset = ProfilerCore.GcCount();

                RefreshModMap();

                var stats = new Dictionary<MethodBase, MethodStats>(ProfilerCore.StatsByMethod);
                int before = stats.Count;

                int repaired = rescan ? RepairLostTimers(stats) : 0;

                if (Plugin.ProfileHarmonyPatches.Value)
                    ScanHarmonyPatches(stats);
                if (Plugin.ProfileMonoBehaviours.Value || Plugin.ProfileCoroutines.Value)
                    ScanTypes(stats);
                if (Plugin.ProfileVanilla.Value)
                    InstrumentVanilla(stats);

                ProfilerCore.StatsByMethod = stats;
                ProfilerCore.Instrumented = true;
                Calibration.Run(_harmony);

                int added = stats.Count - before;
                Plugin.Log.LogInfo(
                    $"instrumented {stats.Count} methods across {ProfilerCore.Mods.Count} mods " +
                    $"(+{added} new, {ProfilerCore.PatchFailures} failures" +
                    (repaired > 0 ? $", {repaired} timers reinstated" : "") + ")");
                return added;
            }
            finally
            {
                ProfilerCore.Active = wasActive;
                ProfilerCore.SkipNextFlush = true;
            }
        }
        internal static bool HasTimer(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                return info != null && info.Owners.Contains(TimerOwnerId);
            }
            catch
            {
                return false;
            }
        }
        internal static bool TimersIntact(int sampleSize)
        {
            Dictionary<MethodBase, MethodStats> stats = ProfilerCore.StatsByMethod;
            if (stats.Count == 0)
                return true;
            int stride = Math.Max(1, stats.Count / Math.Max(1, sampleSize));
            int i = 0;
            foreach (MethodBase method in stats.Keys)
            {
                if (i++ % stride != 0)
                    continue;
                if (!HasTimer(method))
                    return false;
            }
            return true;
        }
        private static int RepairLostTimers(Dictionary<MethodBase, MethodStats> stats)
        {
            int repaired = 0;
            foreach (KeyValuePair<MethodBase, MethodStats> pair in stats)
            {
                if (HasTimer(pair.Key))
                    continue;
                Patched.Remove(pair.Key);
                if (ApplyTimer(pair.Key, out string reason))
                {
                    Patched.Add(pair.Key);
                    repaired++;
                }
                else
                {
                    RecordFailure(pair.Value.Mod, pair.Value.DisplayName,
                        "reinstating the timer failed: " + (reason ?? "no method body"));
                    Plugin.Log.LogWarning($"could not reinstate the timer on {pair.Value.DisplayName}: {reason}");
                }
            }
            if (repaired > 0)
                Plugin.Log.LogWarning(
                    $"{repaired} timer patches had been removed by another mod's unpatch and were reinstated - " +
                    "the affected rows measured nothing in between");
            return repaired;
        }

        private static void RefreshModMap()
        {
            Assembly self = typeof(Instrumenter).Assembly;

            foreach (PluginInfo info in Chainloader.PluginInfos.Values)
            {
                object instance = info.Instance;
                if (instance == null)
                    continue;
                Assembly asm = instance.GetType().Assembly;
                if (asm == self || ModsByAssembly.ContainsKey(asm))
                    continue;
                if (!Plugin.IncludeSptPlugins.Value && IsSptAssembly(asm))
                    continue;
                AddMod(asm, info.Metadata.Name);
            }
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || asm == self || ModsByAssembly.ContainsKey(asm))
                    continue;
                string location = SafeLocation(asm);
                if (string.IsNullOrEmpty(location) ||
                    !location.StartsWith(BepInEx.Paths.PluginPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Plugin.IncludeSptPlugins.Value && IsSptAssembly(asm))
                    continue;
                AddMod(asm, asm.GetName().Name);
            }
        }

        private static void AddMod(Assembly asm, string name)
        {
            var mod = new ModStats(name, asm);
            ModsByAssembly[asm] = mod;
            ProfilerCore.Mods.Add(mod);
        }

        private static string SafeLocation(Assembly asm)
        {
            try { return asm.Location; }
            catch { return null; }
        }

        private static bool IsSptAssembly(Assembly asm)
        {
            string location = SafeLocation(asm);
            return location != null &&
                   location.IndexOf(@"\plugins\spt\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ScanHarmonyPatches(Dictionary<MethodBase, MethodStats> stats)
        {
            List<MethodBase> originals;
            try
            {
                originals = Harmony.GetAllPatchedMethods().ToList();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"could not enumerate patched methods: {e.Message}");
                return;
            }

            foreach (MethodBase original in originals)
            {
                Patches info;
                try { info = Harmony.GetPatchInfo(original); }
                catch { continue; }
                if (info == null)
                    continue;

                CollectPatches(info.Prefixes, "prefix", original, stats);
                CollectPatches(info.Postfixes, "postfix", original, stats);
                CollectPatches(info.Finalizers, "finalizer", original, stats);
            }
        }

        private static void CollectPatches(ReadOnlyCollection<Patch> patches, string kind,
            MethodBase original, Dictionary<MethodBase, MethodStats> stats)
        {
            if (patches == null)
                return;
            foreach (Patch patch in patches)
            {
                MethodInfo method = patch.PatchMethod;
                if (method == null || method.DeclaringType == null)
                    continue;
                if (!ModsByAssembly.TryGetValue(method.DeclaringType.Assembly, out ModStats mod))
                    continue;

                Instrument(method, HarmonyDisplayName(method, kind, new[] { original }),
                    "Harmony", kind, original, mod, stats);
            }
        }
        internal static string TargetName(MethodBase original)
        {
            return original.DeclaringType != null
                ? original.DeclaringType.Name + "." + original.Name
                : original.Name;
        }

        private static string HarmonyDisplayName(MethodBase patch, string kind, IEnumerable<MethodBase> targets)
        {
            return $"{patch.DeclaringType.Name}.{patch.Name} ({kind} → {string.Join(", ", targets.Select(TargetName))})";
        }

        private static void ScanTypes(Dictionary<MethodBase, MethodStats> stats)
        {
            foreach (KeyValuePair<Assembly, ModStats> pair in ModsByAssembly)
            {
                Type[] types;
                try
                {
                    types = pair.Key.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    try { ScanType(type, pair.Value, stats); }
                    catch { /* single broken type must not abort the scan */ }
                }
            }
        }

        private static void ScanType(Type type, ModStats mod, Dictionary<MethodBase, MethodStats> stats)
        {
            if (type.IsInterface || type.ContainsGenericParameters)
                return;

            if (Plugin.ProfileMonoBehaviours.Value && typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                foreach (string name in UnityFrameMethods)
                {
                    MethodInfo method = type.GetMethod(name, DeclaredInstance, null, Type.EmptyTypes, null);
                    if (method != null)
                        Instrument(method, $"{type.Name}.{name}", "Unity", name, null, mod, stats);
                }
            }

            if (Plugin.ProfileCoroutines.Value && IsCompilerStateMachine(type))
            {
                MethodInfo method = type.GetMethod("MoveNext", DeclaredInstance, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    Type ownerType = type.DeclaringType;
                    while (ownerType != null && ownerType.Name.StartsWith("<", StringComparison.Ordinal))
                        ownerType = ownerType.DeclaringType;
                    string owner = ownerType != null ? ownerType.Name + "." : "";
                    Instrument(method, $"{owner}{PrettyStateMachineName(type.Name)} (coroutine/async)",
                        "Coroutine", "MoveNext", null, mod, stats);
                }
            }
        }
        private static bool IsCompilerStateMachine(Type type)
        {
            if (typeof(IAsyncStateMachine).IsAssignableFrom(type))
                return true;
            return typeof(IEnumerator).IsAssignableFrom(type)
                && type.IsDefined(typeof(CompilerGeneratedAttribute), false);
        }
        private static string PrettyStateMachineName(string raw)
        {
            int lt = raw.IndexOf('<');
            int gt = raw.LastIndexOf('>');
            if (lt < 0 || gt <= lt + 1)
                return raw;
            string inner = raw.Substring(lt + 1, gt - lt - 1);
            if (inner.IndexOf('<') < 0)
                return inner;
            int innerGt = inner.LastIndexOf('>');
            string head = PrettyStateMachineName(inner.Substring(0, innerGt + 1));
            string tail = inner.Substring(innerGt + 1);
            return tail.Length > 0 ? head + "." + tail : head;
        }
        private readonly struct VanillaEntryPoint
        {
            internal readonly string TypeName;
            internal readonly string MethodName;
            internal readonly Type[] Parameters;
            internal readonly string Kind;
            internal readonly string NestedUnder;

            internal VanillaEntryPoint(string typeName, string methodName, Type[] parameters, string kind, string nestedUnder)
            {
                TypeName = typeName;
                MethodName = methodName;
                Parameters = parameters;
                Kind = kind;
                NestedUnder = nestedUnder;
            }
        }
        private const string GameWorldType = "EFT.GameWorld";
        private const string DoWorldTickMethod = "DoWorldTick";
        private const string DoOtherWorldTickMethod = "DoOtherWorldTick";

        private const string DoWorldTickDisplayName = GameWorldType + "." + DoWorldTickMethod;
        private const string DoOtherWorldTickDisplayName = GameWorldType + "." + DoOtherWorldTickMethod;

        private static readonly VanillaEntryPoint[] CuratedVanillaEntryPoints =
        {
            new VanillaEntryPoint(GameWorldType, DoWorldTickMethod,
                new[] { typeof(float) }, "WorldTick", null),
            new VanillaEntryPoint(GameWorldType, DoOtherWorldTickMethod,
                new[] { typeof(float) }, "WorldTick", null),
            new VanillaEntryPoint("AICoreController", "Update",
                Type.EmptyTypes, "BotAI-Goals", null),
            new VanillaEntryPoint("EFT.BotOwner", "UpdateManual",
                Type.EmptyTypes, "BotAI-Mechanics", null),
            new VanillaEntryPoint("EFT.UI.InteractionButtonsContainer", "Update",
                Type.EmptyTypes, "UI", null),
            new VanillaEntryPoint("EFT.Player", "ComplexUpdate",
                new[] { typeof(EFT.EUpdateQueue), typeof(float) }, "PlayerUpdate",
                DoWorldTickDisplayName + " or " + DoOtherWorldTickDisplayName),
            new VanillaEntryPoint("EFT.Ballistics.BallisticsCalculator", "ManualUpdate",
                new[] { typeof(float) }, "Ballistics",
                DoWorldTickDisplayName),
        };
        internal static ModStats VanillaMod;
        private static double _vanillaRaidSeenAt = -1.0;
        private static int _vanillaRaidWorldId;
        private static bool _vanillaCoverageChecked;
        private static bool _updateQueueLogged;
        private const double VanillaCoverageSeconds = 60.0;
        private static void InstrumentVanilla(Dictionary<MethodBase, MethodStats> stats)
        {
            if (VanillaMod == null)
            {
                string name = "EFT (vanilla)";
                if (ProfilerCore.Mods.Exists(m => m.Name == name))
                    name = "EFT (vanilla) [profiler]";
                VanillaMod = new ModStats(name, typeof(EFT.GameWorld).Assembly);
                ProfilerCore.Mods.Add(VanillaMod);
            }

            foreach (VanillaEntryPoint e in CuratedVanillaEntryPoints)
            {
                Type type = AccessTools.TypeByName(e.TypeName);
                MethodBase target = type != null
                    ? AccessTools.Method(type, e.MethodName, e.Parameters)
                    : null;
                if (target == null)
                {
                    RecordFailure(VanillaMod, $"{e.TypeName}.{e.MethodName}",
                        "method not found (game version drift?)");
                    continue;
                }
                Instrument(target, $"{e.TypeName}.{e.MethodName}", "EFT", e.Kind,
                    null, VanillaMod, stats, e.NestedUnder);
            }

            OrphanOrphanedNestedUnderRows();
        }
        private static void OrphanOrphanedNestedUnderRows()
        {
            var instrumented = new HashSet<string>();
            foreach (MethodStats m in VanillaMod.Methods)
                instrumented.Add(m.DisplayName);

            foreach (MethodStats m in VanillaMod.Methods)
            {
                if (m.NestedUnder == null)
                    continue;
                string[] alternatives = m.NestedUnder.Split(
                    new[] { " or " }, StringSplitOptions.None);
                bool anyParentPresent = false;
                foreach (string alt in alternatives)
                {
                    if (instrumented.Contains(alt))
                    {
                        anyParentPresent = true;
                        break;
                    }
                }
                if (!anyParentPresent)
                {
                    Plugin.Log.LogWarning($"{m.NestedUnder} failed to instrument - "
                        + $"{m.DisplayName} is no longer covered by its normal parent "
                        + "and is now counted directly in the EFT (vanilla) total instead, "
                        + "so per-call attribution may look off.");
                    m.NestedUnder = null;
                }
            }
        }
        private static void LogVanillaUpdateQueueOnce(EFT.GameWorld gameWorld)
        {
            if (_updateQueueLogged)
                return;
            _updateQueueLogged = true;
            try
            {
                Plugin.Log.LogDebug($"GameWorld.UpdateQueue = {gameWorld.UpdateQueue} "
                    + "(determines whether DoWorldTick runs in Update or FixedUpdate this raid)");
            }
            catch
            {
            }
        }
        private static EFT.GameWorld RaidWorld()
        {
            if (!Singleton<EFT.GameWorld>.Instantiated)
                return null;
            EFT.GameWorld world = Singleton<EFT.GameWorld>.Instance;
            if (!(world is EFT.ClientGameWorld) || world is HideoutGameWorld || world is NarrateGameWorld)
                return null;
            return world.MainPlayer != null ? world : null;
        }
        internal static void CheckVanillaCoverageIfDue()
        {
            if (_vanillaCoverageChecked || VanillaMod == null)
                return;
            EFT.GameWorld world = RaidWorld();
            if (world == null)
            {
                _vanillaRaidSeenAt = -1.0;
                return;
            }
            int worldId = world.GetInstanceID();
            if (_vanillaRaidSeenAt < 0.0 || worldId != _vanillaRaidWorldId
                || ProfilerCore.MeasuredSeconds < _vanillaRaidSeenAt)
            {
                _vanillaRaidWorldId = worldId;
                _vanillaRaidSeenAt = ProfilerCore.MeasuredSeconds;
                LogVanillaUpdateQueueOnce(world);
                return;
            }
            if (ProfilerCore.MeasuredSeconds - _vanillaRaidSeenAt < VanillaCoverageSeconds)
                return;
            _vanillaCoverageChecked = true;
            foreach (MethodStats m in VanillaMod.Methods)
                if (m.TotalCalls == 0L)
                    Plugin.Log.LogWarning($"{m.DisplayName} recorded no calls in the first measured minute "
                        + "of this raid - either nothing in this raid ran it (an inactive UI container or a "
                        + "raid without bots counts too), or it was inlined before the patch could attach, "
                        + "and EFT (vanilla) undercounts it.");
        }

        private static void Instrument(MethodBase target, string displayName, string category, string kind,
            MethodBase original, ModStats mod, Dictionary<MethodBase, MethodStats> stats, string nestedUnder = null)
        {
            if (stats.TryGetValue(target, out MethodStats known))
            {
                if (original != null && known.Targets != null && !known.Targets.Contains(original))
                {
                    known.Targets.Add(original);
                    if (known.Kind.IndexOf(kind, StringComparison.Ordinal) < 0)
                        known.Kind = known.Kind + "+" + kind;
                    known.DisplayName = HarmonyDisplayName(target, known.Kind, known.Targets);
                }
                return;
            }
            if (Patched.Contains(target))
                return;
            if (target.IsAbstract || target.IsGenericMethod || target.ContainsGenericParameters)
                return;

            if (!ApplyTimer(target, out string reason))
            {
                if (reason == null)
                    return;
                RecordFailure(mod, displayName, reason);
                Plugin.Log.LogDebug($"could not instrument {target.DeclaringType?.FullName}.{target.Name}: {reason}");
                return;
            }

            Patched.Add(target);
            var methodStats = new MethodStats(target, displayName, category, kind, mod, nestedUnder);
            if (original != null)
                methodStats.Targets = new List<MethodBase> { original };
            mod.Methods.Add(methodStats);
            stats[target] = methodStats;
        }

        private static void RecordFailure(ModStats mod, string displayName, string reason)
        {
            ProfilerCore.PatchFailures++;
            if (ProfilerCore.PatchFailureDetails.Count < ProfilerCore.MaxPatchFailureDetails)
                ProfilerCore.PatchFailureDetails.Add(new ProfilerCore.PatchFailure
                {
                    Mod = mod.Name,
                    Method = displayName,
                    Reason = reason,
                });
        }
        private static bool ApplyTimer(MethodBase target, out string reason)
        {
            reason = null;
            try
            {
                if (target.GetMethodBody() == null)
                    return false;
                _harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ProfilerCore), nameof(ProfilerCore.TimerPrefix)))
                    {
                        priority = Priority.First
                    },
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ProfilerCore), nameof(ProfilerCore.TimerPostfix)))
                    {
                        priority = Priority.Last
                    });
                return true;
            }
            catch (Exception e)
            {
                Exception cause = e;
                while (cause.InnerException != null)
                    cause = cause.InnerException;
                reason = (cause.Message ?? cause.GetType().Name).Split('\n')[0].Trim();
                if (reason.Length > 200)
                    reason = reason.Substring(0, 200) + "...";
                if (reason.Length == 0)
                    reason = cause.GetType().Name;
                return false;
            }
        }
    }
}
