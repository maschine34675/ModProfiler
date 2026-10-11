namespace ModProfiler.Profiling
{
    internal sealed class ActivityArbiter
    {
        private bool window;
        private int holds;

        internal bool Active => window || holds != 0;
        internal bool Held => holds != 0;

        internal bool SetWindow(bool on)
        {
            bool before = Active;
            window = on;
            return Active != before;
        }
        internal bool SetHold(int bit, bool on)
        {
            bool before = Active;
            holds = on ? holds | bit : holds & ~bit;
            return Active != before;
        }
    }
    internal sealed class CaptureSwitch
    {
        internal enum Step
        {
            None,
            Start,
            Stop,
        }

        private bool last;
        internal bool Running { get; private set; }

        internal Step Poll(bool wanted)
        {
            if (wanted == last)
                return Step.None;
            last = wanted;
            if (wanted)
                return Running ? Step.None : Step.Start;
            return Running ? Step.Stop : Step.None;
        }
        internal void Started(bool ok)
        {
            Running = ok;
        }
        internal void Stopped()
        {
            Running = false;
        }
    }
}
