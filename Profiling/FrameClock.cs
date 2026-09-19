using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace ModProfiler.Profiling
{
    internal static class FrameClock
    {
        private struct ModProfilerFrameStart
        {
        }

        private const int MaxInstalls = 5;
        private static readonly PlayerLoopSystem.UpdateFunction Callback = OnFrameStart;

        private static bool ranSinceUpdate;
        private static int installs;
        private static int presentButSilent;
        private static bool gaveUp;
        private static bool reportedError;
        internal static bool Aligned { get; private set; }

        private static void OnFrameStart()
        {
            ranSinceUpdate = true;
            try
            {
                if (ProfilerCore.Active)
                    ProfilerCore.FlushFrame(Time.unscaledDeltaTime * 1000f);
            }
            catch (Exception e)
            {
                if (!reportedError)
                {
                    reportedError = true;
                    Plugin.Log.LogError("frame-start drain failed: " + e);
                }
            }
        }
        internal static void OnUpdate()
        {
            if (ranSinceUpdate)
            {
                ranSinceUpdate = false;
                presentButSilent = 0;
                Aligned = true;
                return;
            }
            Aligned = false;
            if (ProfilerCore.Active)
                ProfilerCore.FlushFrame(Time.unscaledDeltaTime * 1000f);
            EnsureInstalled();
        }

        private static void EnsureInstalled()
        {
            if (gaveUp)
                return;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (Contains(root))
                {
                    if (++presentButSilent >= 3)
                    {
                        gaveUp = true;
                        Plugin.Log.LogWarning("the frame-start drain is in Unity's player loop but does not run - " +
                                              "measurements are drained in Update instead, so spike contributors can be off by one frame");
                    }
                    return;
                }
                presentButSilent = 0;

                if (installs >= MaxInstalls)
                {
                    gaveUp = true;
                    Plugin.Log.LogWarning("the frame-start drain keeps being removed from Unity's player loop " +
                                          "(another mod resets it) - measurements are drained in Update instead, " +
                                          "so spike contributors can be off by one frame");
                    return;
                }

                if (!InsertFirst(ref root, typeof(EarlyUpdate)))
                {
                    gaveUp = true;
                    Plugin.Log.LogWarning("no EarlyUpdate phase in Unity's player loop - measurements are drained in Update instead");
                    return;
                }
                PlayerLoop.SetPlayerLoop(root);
                installs++;
                if (installs > 1)
                    Plugin.Log.LogInfo("the frame-start drain had been removed from Unity's player loop and was inserted again");
            }
            catch (Exception e)
            {
                gaveUp = true;
                Plugin.Log.LogWarning("could not insert the frame-start drain into Unity's player loop (" + e.Message +
                                      ") - measurements are drained in Update instead");
            }
        }

        private static bool Contains(PlayerLoopSystem system)
        {
            if (system.type == typeof(ModProfilerFrameStart))
                return true;
            if (system.subSystemList == null)
                return false;
            foreach (PlayerLoopSystem sub in system.subSystemList)
                if (Contains(sub))
                    return true;
            return false;
        }

        private static bool InsertFirst(ref PlayerLoopSystem root, Type phase)
        {
            if (root.subSystemList == null)
                return false;
            for (int i = 0; i < root.subSystemList.Length; i++)
            {
                if (root.subSystemList[i].type != phase)
                    continue;
                PlayerLoopSystem target = root.subSystemList[i];
                var list = new List<PlayerLoopSystem>(target.subSystemList ?? new PlayerLoopSystem[0]);
                list.Insert(0, new PlayerLoopSystem
                {
                    type = typeof(ModProfilerFrameStart),
                    updateDelegate = Callback,
                });
                target.subSystemList = list.ToArray();
                root.subSystemList[i] = target;
                return true;
            }
            return false;
        }
    }
}
