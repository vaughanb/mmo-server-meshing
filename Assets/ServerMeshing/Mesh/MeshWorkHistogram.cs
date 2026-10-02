// Server Meshing add-on — a fixed-bin histogram of per-frame durations (DESIGN.md 0.71, the work probe).
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshWorkHistogram accumulates durations in milliseconds into fixed-width bins, so a window's mean and p95 cost
    /// no allocation per frame. Values past the last bin land in an overflow bin reported as the window's maximum.
    /// The bin width is held in whole microseconds and the percentile rank in integers, so a value on a bin edge and
    /// the rank of a percentile do not depend on how the runtime rounds float arithmetic.
    /// </summary>
    public sealed class MeshWorkHistogram
    {
        private readonly int _binUs;
        private readonly double _rangeUs;
        private readonly int[] _bins;
        private int _overflow;
        private int _count;
        private double _sum;
        private float _max;

        /// <summary>
        /// MeshWorkHistogram creates a histogram of <paramref name="binCount"/> bins of <paramref name="binMs"/> each. The
        /// width is rounded to whole microseconds and must be at least one.
        /// </summary>
        public MeshWorkHistogram(float binMs = 0.25f, int binCount = 400)
        {
            if (!(binMs > 0f))
                throw new ArgumentOutOfRangeException(nameof(binMs), binMs, "bin width must be positive");
            if (binCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(binCount), binCount, "bin count must be positive");
            double binUs = Math.Round(binMs * 1000d);
            if (binUs < 1d)
                throw new ArgumentOutOfRangeException(nameof(binMs), binMs, "bin width must be at least one microsecond");
            if (binUs > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(binMs), binMs, "bin width is too large");
            _binUs = (int)binUs;
            _rangeUs = (double)_binUs * binCount;
            _bins = new int[binCount];
        }

        /// <summary>BinMs is the width of one bin in milliseconds, after rounding to whole microseconds.</summary>
        public float BinMs => _binUs / 1000f;

        /// <summary>BinCount is the number of bins below the overflow.</summary>
        public int BinCount => _bins.Length;

        /// <summary>Overflow is the number of values at or past the end of the last bin.</summary>
        public int Overflow => _overflow;

        /// <summary>Count is the number of values added since the last reset, the overflow included.</summary>
        public int Count => _count;

        /// <summary>Mean is the exact mean of the values added since the last reset (not taken from the bins), or 0 when empty.</summary>
        public float Mean => _count > 0 ? (float)(_sum / _count) : 0f;

        /// <summary>Max is the largest value added since the last reset, or 0 when empty.</summary>
        public float Max => _max;

        /// <summary>CountInBin returns how many values fell in <paramref name="bin"/>, which covers [bin, bin + 1) x <see cref="BinMs"/>.</summary>
        public int CountInBin(int bin)
        {
            if (bin < 0 || bin >= _bins.Length)
                throw new ArgumentOutOfRangeException(nameof(bin), bin, $"bin must be in 0..{_bins.Length - 1}");
            return _bins[bin];
        }

        /// <summary>
        /// Add records one duration. Negative values and NaN count as zero; a value at or past the last bin's end, positive
        /// infinity included, goes to the overflow.
        /// </summary>
        public void Add(float ms)
        {
            if (ms < 0f || float.IsNaN(ms))
                ms = 0f;
            _count++;
            _sum += ms;
            if (ms > _max)
                _max = ms;
            double us = ms * 1000d;
            if (us >= _rangeUs)
            {
                _overflow++;
                return;
            }
            int bin = (int)Math.Floor(us / _binUs);
            if (bin >= _bins.Length)
                _overflow++;
            else
                _bins[bin]++;
        }

        /// <summary>
        /// Percentile returns the upper edge of the bin holding the given fraction (0..1) of the values, clamped to the
        /// maximum, or the maximum when that rank falls in the overflow. The rank is the fraction in whole parts per
        /// million times the count, rounded up, so p99 of exactly 100 values is the 99th value's bin.
        /// </summary>
        public float Percentile(float fraction)
        {
            if (_count == 0)
                return 0f;
            double clamped = fraction > 0f ? Math.Min(1d, fraction) : 0d;
            long ppm = (long)Math.Round(clamped * 1000000d);
            long rank = Math.Max(1L, (ppm * _count + 999999L) / 1000000L);
            long seen = 0;
            for (int i = 0; i < _bins.Length; ++i)
            {
                seen += _bins[i];
                if (seen >= rank)
                    return Math.Min((float)((i + 1) * (double)_binUs / 1000d), _max);
            }
            return _max;
        }

        /// <summary>Reset empties the histogram for the next window.</summary>
        public void Reset()
        {
            Array.Clear(_bins, 0, _bins.Length);
            _overflow = 0;
            _count = 0;
            _sum = 0d;
            _max = 0f;
        }
    }
}
