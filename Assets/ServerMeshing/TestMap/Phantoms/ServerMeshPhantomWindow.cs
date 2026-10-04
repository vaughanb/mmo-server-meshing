// Server Meshing add-on — rig phantom window: the per-window sampler and the "Phantom window" line the binder logs with every Work window.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomWindow gathers one window of phantom figures and prints them as one invariant line. The binder sets
    /// the counts, the feeder adds uploads, posErr samples and its time, and the sampler adds V_move and down samples over a
    /// rotating tenth of the phantoms. Pure: it reads no Unity state, so the line and the sampler are testable as they are.
    /// Its lists keep their capacity across windows.
    /// </summary>
    public sealed class ServerMeshPhantomWindow
    {
        /// <summary>SampleShare is the share of phantoms the rotating sample takes each time.</summary>
        public const double SampleShare = 0.1;

        private static readonly string[] s_deliveryNames = { "ru", "seq", "ro", "rs", "u", "other" };

        private readonly List<int> _vmove = new List<int>(64);
        private readonly List<float> _posErr = new List<float>(4096);
        private readonly List<float> _viewErr = new List<float>(4096);
        private readonly List<double> _down = new List<double>(256);
        private readonly long[] _swallowedCalls = new long[ServerMeshPhantomTransport.DeliverySlots];
        private readonly long[] _swallowedBytes = new long[ServerMeshPhantomTransport.DeliverySlots];
        private readonly StringBuilder _line = new StringBuilder(512);

        /// <summary>Seconds is the window's length.</summary>
        public double Seconds;
        /// <summary>BoundVirtual is the virtual phantoms whose object is spawned and alive.</summary>
        public int BoundVirtual;
        /// <summary>BoundSink is the sink phantoms whose object is spawned and alive.</summary>
        public int BoundSink;
        /// <summary>Ready is the bound phantoms whose kit player is ready.</summary>
        public int Ready;
        /// <summary>Filler is the live server-owned filler movers.</summary>
        public int Filler;
        /// <summary>EyeSeen is the eyes with a subscriber other than their owner (0 when every eye stays hidden).</summary>
        public int EyeSeen;
        /// <summary>ShowFighting prints the fighting count (MESH_PHANTOM_FIGHT is set).</summary>
        public bool ShowFighting;
        /// <summary>ShowViewErr prints the viewErr group (the binder sets it when it starts the view sampler).</summary>
        public bool ShowViewErr;
        /// <summary>Fighting is the phantoms that fight.</summary>
        public int Fighting;
        /// <summary>StepIndex is the current step from 1, or 0 when MESH_PHANTOM_STEPS is unset.</summary>
        public int StepIndex;
        /// <summary>StepCount is the number of steps, or 0 when MESH_PHANTOM_STEPS is unset.</summary>
        public int StepCount;
        /// <summary>Target is how many phantoms should be bound now.</summary>
        public int Target;
        /// <summary>Uploads is the frames the feeder queued in the window.</summary>
        public long Uploads;
        /// <summary>Frames is the frames in the window.</summary>
        public int Frames;
        /// <summary>FeederMs is the feeder's time in the window.</summary>
        public double FeederMs;
        /// <summary>BinderMs is the binder's (and the fighter's) time in the window.</summary>
        public double BinderMs;
        /// <summary>SamplerMs is the sampler's time in the window.</summary>
        public double SamplerMs;

        /// <summary>VMoveSamples is how many V_move samples the window holds.</summary>
        public int VMoveSamples => _vmove.Count;
        /// <summary>PosErrSamples is how many posErr samples the window holds.</summary>
        public int PosErrSamples => _posErr.Count;
        /// <summary>ViewErrSamples is how many viewErr samples the window holds.</summary>
        public int ViewErrSamples => _viewErr.Count;
        /// <summary>DownSamples is how many down samples the window holds.</summary>
        public int DownSamples => _down.Count;

        /// <summary>AddVMove adds one phantom's V_move.</summary>
        public void AddVMove(int movers) => _vmove.Add(movers);

        /// <summary>AddPosErr adds one posErr sample in metres.</summary>
        public void AddPosErr(float metres) => _posErr.Add(metres);

        /// <summary>AddViewErr adds one viewErr sample in metres: an observer's newest received position of an object against the object's server position.</summary>
        public void AddViewErr(float metres) => _viewErr.Add(metres);

        /// <summary>AddDown adds one phantom's swallowed payload rate in kB/s.</summary>
        public void AddDown(double kiloBytesPerSecond) => _down.Add(kiloBytesPerSecond);

        /// <summary>SetSwallowed sets a delivery slot's swallowed calls and bytes for the window (window deltas).</summary>
        public void SetSwallowed(int deliverySlot, long calls, long bytes)
        {
            _swallowedCalls[deliverySlot] = calls;
            _swallowedBytes[deliverySlot] = bytes;
        }

        /// <summary>Reset empties the window for the next one; list capacity is kept.</summary>
        public void Reset()
        {
            _vmove.Clear();
            _posErr.Clear();
            _viewErr.Clear();
            _down.Clear();
            Array.Clear(_swallowedCalls, 0, _swallowedCalls.Length);
            Array.Clear(_swallowedBytes, 0, _swallowedBytes.Length);
            Seconds = 0;
            BoundVirtual = BoundSink = Ready = Filler = EyeSeen = Fighting = StepIndex = StepCount = Target = Frames = 0;
            Uploads = 0;
            FeederMs = BinderMs = SamplerMs = 0;
        }

        /// <summary>
        /// CountMovers is a phantom's V_move: the ids it subscribes that have a movement handler, less its own object (the
        /// kit always subscribes a player to its own objects).
        /// </summary>
        public static int CountMovers<T>(HashSet<uint>.Enumerator subscribing, IReadOnlyDictionary<uint, T> handlers, uint ownObjectId)
        {
            if (handlers == null)
                throw new ArgumentNullException(nameof(handlers));
            int movers = 0;
            while (subscribing.MoveNext())
            {
                uint id = subscribing.Current;
                if (id != ownObjectId && handlers.ContainsKey(id))
                    ++movers;
            }
            return movers;
        }

        /// <summary>IsSeenByOthers is true when an identity with subscriberCount subscribers has one besides its owner.</summary>
        public static bool IsSeenByOthers(int subscriberCount, bool ownerSubscribes) => subscriberCount - (ownerSubscribes ? 1 : 0) > 0;

        /// <summary>SampleSize is the rotating sample's size for count phantoms: a tenth, at least 1 (0 for none).</summary>
        public static int SampleSize(int count) => count <= 0 ? 0 : Math.Max(1, (int)Math.Ceiling(count * SampleShare));

        /// <summary>SampleStart is where sample number round starts over count phantoms, so successive samples rotate through all of them.</summary>
        public static int SampleStart(int round, int count)
        {
            if (count <= 0)
                return 0;
            long start = (long)Math.Max(0, round) * SampleSize(count);
            return (int)(start % count);
        }

        /// <summary>Percentile is the nearest-rank percentile (0 to 1) of an ascending list, or 0 when it is empty.</summary>
        public static T Percentile<T>(List<T> ascending, double share)
        {
            if (ascending.Count == 0)
                return default;
            int rank = (int)Math.Ceiling(share * ascending.Count) - 1;
            return ascending[Math.Min(ascending.Count - 1, Math.Max(0, rank))];
        }

        /// <summary>
        /// ToLine is the window's line: every field always printed, zeros when empty, rates 0 when the window has no length,
        /// no NaN or negatives; the fighting and viewErr groups only while ShowFighting and ShowViewErr are set. It sorts the
        /// sample lists in place.
        /// </summary>
        public string ToLine()
        {
            double seconds = Seconds > 0 && !double.IsInfinity(Seconds) ? Seconds : 0;
            _vmove.Sort();
            _posErr.Sort();
            _viewErr.Sort();
            _down.Sort();
            StringBuilder b = _line.Clear();
            b.Append("Phantom window ").Append(Fixed(seconds, "0.00")).Append(" s: bound virtual ").Append(Count(BoundVirtual));
            b.Append(" sink ").Append(Count(BoundSink)).Append(" ready ").Append(Count(Ready)).Append(" filler ").Append(Count(Filler));
            b.Append(" eyeSeen ").Append(Count(EyeSeen));
            if (ShowFighting)
                b.Append(" fighting ").Append(Count(Fighting));
            b.Append(" | step ").Append(Count(StepIndex)).Append('/').Append(Count(StepCount)).Append(" target ").Append(Count(Target));
            b.Append(" | vmove n ").Append(_vmove.Count).Append(" mean ").Append(Fixed(Mean(_vmove), "0.0"));
            b.Append(" p95 ").Append(Count(Percentile(_vmove, 0.95))).Append(" min ").Append(Count(_vmove.Count > 0 ? _vmove[0] : 0));
            b.Append(" | uploads ").Append(Fixed(Rate(Uploads, seconds), "0.0")).Append("/s");
            b.Append(" | posErr n ").Append(_posErr.Count).Append(" p50 ").Append(Fixed(Percentile(_posErr, 0.5), "0.00"));
            b.Append(" p95 ").Append(Fixed(Percentile(_posErr, 0.95), "0.00")).Append(" max ").Append(Fixed(_posErr.Count > 0 ? _posErr[_posErr.Count - 1] : 0f, "0.00")).Append(" m");
            if (ShowViewErr)
            {
                b.Append(" | viewErr n ").Append(_viewErr.Count).Append(" p50 ").Append(Fixed(Percentile(_viewErr, 0.5), "0.00"));
                b.Append(" p95 ").Append(Fixed(Percentile(_viewErr, 0.95), "0.00")).Append(" max ").Append(Fixed(_viewErr.Count > 0 ? _viewErr[_viewErr.Count - 1] : 0f, "0.00")).Append(" m");
            }
            b.Append(" | down n ").Append(_down.Count).Append(" mean ").Append(Fixed(Mean(_down), "0.0"));
            b.Append(" p95 ").Append(Fixed(Percentile(_down, 0.95), "0.0")).Append(" max ").Append(Fixed(_down.Count > 0 ? _down[_down.Count - 1] : 0.0, "0.0")).Append(" kB/s");
            b.Append(" | swallowed");
            for (int i = 0; i < s_deliveryNames.Length; ++i)
                b.Append(' ').Append(s_deliveryNames[i]).Append(' ').Append(Count(_swallowedCalls[i])).Append(' ').Append(Count(_swallowedBytes[i]));
            double rigMs = Positive(FeederMs) + Positive(BinderMs) + Positive(SamplerMs);
            b.Append(" | frames ").Append(Count(Frames)).Append(" rig feeder ").Append(Fixed(Rate(FeederMs, seconds), "0.00"));
            b.Append(" binder ").Append(Fixed(Rate(BinderMs, seconds), "0.00")).Append(" sampler ").Append(Fixed(Rate(SamplerMs, seconds), "0.00"));
            b.Append(" ms/s ").Append(Fixed(Frames > 0 ? rigMs / Frames : 0.0, "0.000")).Append(" ms/frame");
            return b.ToString();
        }

        private static double Mean(List<int> values)
        {
            if (values.Count == 0)
                return 0;
            double sum = 0;
            for (int i = 0; i < values.Count; ++i)
                sum += values[i];
            return sum / values.Count;
        }

        private static double Mean(List<double> values)
        {
            if (values.Count == 0)
                return 0;
            double sum = 0;
            for (int i = 0; i < values.Count; ++i)
                sum += values[i];
            return sum / values.Count;
        }

        private static double Rate(double amount, double seconds) => seconds > 0 ? Positive(amount) / seconds : 0;

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) ? value : 0;

        private static string Fixed(double value, string format) => Positive(value).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
