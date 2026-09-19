using System;
using EFT.GlobalEvents;
using EFT.InputSystem;
using UnityEngine;

namespace ModProfiler.UI
{
    internal sealed class CursorRequestNode : InputNode
    {
        private static CursorRequestNode _node;
        private static bool _unavailable;
        private bool _wanted;
        internal static void Want(bool on)
        {
            if (_unavailable)
            {
                RequestThroughGlobalFlag(on);
                return;
            }

            try
            {
                Vote(on);
            }
            catch (Exception error)
            {
                _unavailable = true;
                Plugin.Log.LogWarning("the input tree could not be used for the cursor ("
                    + error.GetType().Name + "); falling back to the global flag.");
                RequestThroughGlobalFlag(on);
            }
        }

        private static void Vote(bool on)
        {
            if (_node == null)
            {
                GameObject input = GameObject.Find("___Input");
                InputTree tree = input == null ? null : input.GetComponent<InputTree>();
                if (tree == null)
                    return;

                var host = new GameObject("ModProfiler_CursorNode");
                UnityEngine.Object.DontDestroyOnLoad(host);
                _node = host.AddComponent<CursorRequestNode>();
                tree.Add(_node);
            }

            _node._wanted = on;
        }
        private static void RequestThroughGlobalFlag(bool on)
        {
            try
            {
                GlobalEventsController.Instance
                    .CreateCommonEvent<ToggleShowInGameCursorEvent>()
                    .Invoke(on);
            }
            catch
            {
            }
        }

        public override ECursorResult ShouldLockCursor() =>
            _wanted ? ECursorResult.ShowCursor : ECursorResult.Ignore;

        public override ETranslateResult TranslateCommand(ECommand command) =>
            ETranslateResult.Ignore;

        public override void TranslateAxes(ref float[] axes)
        {
        }
    }
}
