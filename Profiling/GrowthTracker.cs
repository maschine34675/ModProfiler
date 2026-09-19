using System;

namespace ModProfiler.Profiling
{
    internal sealed class GrowthTracker
    {
        internal const int WindowSamples = 3;
        internal const int ShortWindows = 4;
        internal const int ShortRises = 3;
        internal const double ShortRelativeRise = 1.0;
        internal const int LongWindows = 10;
        internal const int LongRises = 4;
        internal const double MinRise = 3.0;
        internal const double Eps = 0.25;
        internal const int HoldSamples = 9;

        private const int Capacity = WindowSamples * LongWindows;

        private readonly double[] values = new double[Capacity];
        private readonly double[] times = new double[Capacity];
        private int count;
        private int next;
        private int samplesSinceHit;
        private double hitFloor;
        internal bool Growing { get; private set; }
        internal double From { get; private set; }
        internal double To { get; private set; }
        internal double SinceSeconds { get; private set; }

        internal void Reset()
        {
            count = 0;
            next = 0;
            Clear();
        }

        private void Clear()
        {
            Growing = false;
            From = 0.0;
            To = 0.0;
            SinceSeconds = 0.0;
            samplesSinceHit = 0;
            hitFloor = 0.0;
        }
        internal void Feed(double perFrame, double seconds)
        {
            if (perFrame <= 0.0)
            {
                Reset();
                return;
            }

            values[next] = perFrame;
            times[next] = seconds;
            next = (next + 1) % Capacity;
            if (count < Capacity)
                count++;
            bool longHit = Climbing(LongWindows, LongRises, 0.0, out double longFrom, out double longSince);
            bool shortHit = Climbing(ShortWindows, ShortRises, ShortRelativeRise, out double shortFrom, out double shortSince);

            if (longHit || shortHit)
            {
                if (!Growing)
                {
                    Growing = true;
                    From = longHit ? longFrom : shortFrom;
                    SinceSeconds = longHit ? longSince : shortSince;
                }
                To = perFrame;
                hitFloor = Floor(WindowSamples, 0);
                samplesSinceHit = 0;
                return;
            }

            if (!Growing)
                return;
            samplesSinceHit++;
            if (perFrame < hitFloor - Eps || samplesSinceHit > HoldSamples)
                Clear();
            else
                To = perFrame;
        }

        private bool Climbing(int windows, int rises, double relative, out double from, out double since)
        {
            from = 0.0;
            since = 0.0;
            int span = windows * WindowSamples;
            if (count < span)
                return false;

            double first = 0.0;
            double previous = 0.0;
            int up = 0;
            for (int w = 0; w < windows; w++)
            {
                double floor = Floor(span, w * WindowSamples);
                if (w == 0)
                {
                    first = floor;
                    since = TimeAt(span, 0);
                }
                else
                {
                    if (floor < previous - Eps)
                        return false;
                    if (floor > previous + Eps)
                        up++;
                }
                previous = floor;
            }
            from = first;
            return up >= rises && previous - first >= Math.Max(MinRise, relative * first);
        }
        private double Floor(int span, int offset)
        {
            double floor = double.MaxValue;
            for (int s = 0; s < WindowSamples; s++)
                floor = Math.Min(floor, ValueAt(span, offset + s));
            return floor;
        }
        private double ValueAt(int span, int i)
        {
            return values[(next - span + i + Capacity * 2) % Capacity];
        }

        private double TimeAt(int span, int i)
        {
            return times[(next - span + i + Capacity * 2) % Capacity];
        }
    }
}
