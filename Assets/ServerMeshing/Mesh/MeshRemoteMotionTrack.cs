// Server Meshing add-on — a remote character drawn from its states' server timestamps, not from when they arrived (DESIGN.md 1.17).
//
// The kit moves a remote character from where it is to each new state over one fixed tick (LogicUpdater.DeltaTimeF),
// whenever that state arrives. Its states are not evenly spaced in time: a cell running two kit ticks in one frame
// writes the same position twice and the next tick's state carries two ticks of walking (at 60 Hz, about one frame in
// sixteen), and the transport sends what was queued every 15 ms. Each uneven state showed as a stand of a tick or more
// and then a lunge (21 stalls of 30 ms or more in 180 s of one character walking past on the observer's own cell).
//
// This keeps the states with their server timestamps and draws the character at a render time a little behind the
// newest one: the trajectory between samples, whatever their spacing. Two states written within MergeMilliseconds are
// one sample (the later wins). The render time is the newest arrival's clock less the smallest transit seen less a
// delay of DelayIntervals sample intervals; it runs at the frame's rate and is slewed at most SlewFraction of a frame
// towards that target, so it never jumps unless it is ResetMilliseconds off. Past the newest sample it extrapolates
// that sample's velocity for at most MaxExtrapolationMilliseconds. Whatever a new sample or a reset moves under the
// render time becomes an offset that decays at CorrectionRate, so the drawn position never steps.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed class MeshRemoteMotionTrack
    {
        private struct Sample
        {
            public double stamp;
            public Vector3 position;
        }

        /// <summary>States written this close together (ms) are one sample: two kit ticks in one frame.</summary>
        public const double MergeMilliseconds = 2.0;
        /// <summary>A sample interval longer than this (ms) is a pause, not a rate; nothing is extrapolated across it.</summary>
        public const double StaleMilliseconds = 250.0;
        /// <summary>The smallest transit seen rises this many ms per second, so a lasting change of route is followed.</summary>
        public const double BaseDriftPerSecond = 5.0;
        public const double SlewFraction = 0.1;
        public const double ResetMilliseconds = 150.0;
        public const int MaxSamples = 32;

        private readonly List<Sample> _samples = new List<Sample>(MaxSamples);
        private readonly double _delayIntervals;
        private readonly double _minDelayMilliseconds;
        private readonly double _maxExtrapolationMilliseconds;
        private readonly float _correctionRate;
        private double _base;
        private bool _hasBase;
        private double _interval;
        private double _render;
        private bool _rendering;
        private Vector3 _offset;

        public MeshRemoteMotionTrack(double delayIntervals = 1.5, double minDelayMilliseconds = 20.0, double maxExtrapolationMilliseconds = 100.0, float correctionRate = 15f)
        {
            if (!(delayIntervals >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(delayIntervals), delayIntervals, "delay must not be negative");
            if (!(minDelayMilliseconds >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(minDelayMilliseconds), minDelayMilliseconds, "minimum delay must not be negative");
            if (!(maxExtrapolationMilliseconds >= 0.0))
                throw new ArgumentOutOfRangeException(nameof(maxExtrapolationMilliseconds), maxExtrapolationMilliseconds, "maximum extrapolation must not be negative");
            if (!(correctionRate > 0f))
                throw new ArgumentOutOfRangeException(nameof(correctionRate), correctionRate, "correction rate must be positive");
            _delayIntervals = delayIntervals;
            _minDelayMilliseconds = minDelayMilliseconds;
            _maxExtrapolationMilliseconds = maxExtrapolationMilliseconds;
            _correctionRate = correctionRate;
        }

        /// <summary>Count is the number of samples kept.</summary>
        public int Count => _samples.Count;

        /// <summary>IsDrawing is true once TryPose has started drawing, until the next Reset.</summary>
        public bool IsDrawing => _rendering;

        /// <summary>Interval is the smoothed spacing of samples, in ms (0 before the second sample).</summary>
        public double Interval => _interval;

        /// <summary>Delay is how far behind the newest sample's clock the render time aims, in ms.</summary>
        public double Delay => Math.Max(_minDelayMilliseconds, _interval * _delayIntervals);

        /// <summary>Reset forgets everything: the next pose starts again from two samples.</summary>
        public void Reset()
        {
            _samples.Clear();
            _hasBase = false;
            _interval = 0.0;
            _rendering = false;
            _offset = Vector3.zero;
        }

        /// <summary>
        /// Add keeps the state <paramref name="position"/> the server stamped <paramref name="stampMs"/>, which arrived at
        /// <paramref name="arrivalMs"/> on this client's clock. An older state than the newest is dropped (false). A snap
        /// (a teleport, or a jump the kit snaps) forgets the earlier samples first.
        /// </summary>
        public bool Add(double stampMs, double arrivalMs, Vector3 position, bool snap)
        {
            if (snap)
                Reset();
            int count = _samples.Count;
            if (count > 0 && stampMs < _samples[count - 1].stamp)
                return false;
            Vector3 before = _rendering ? Raw(_render) : Vector3.zero;

            double transit = arrivalMs - stampMs;
            if (!_hasBase || transit < _base)
            {
                _base = transit;
                _hasBase = true;
            }
            if (count > 0 && stampMs - _samples[count - 1].stamp < MergeMilliseconds)
            {
                _samples[count - 1] = new Sample { stamp = stampMs, position = position };
            }
            else
            {
                if (count > 0)
                {
                    double gap = Math.Min(stampMs - _samples[count - 1].stamp, StaleMilliseconds);
                    _interval = _interval > 0.0 ? _interval + (gap - _interval) * 0.1 : gap;
                }
                if (count == MaxSamples)
                    _samples.RemoveAt(0);
                _samples.Add(new Sample { stamp = stampMs, position = position });
            }
            // Samples the render time has passed are kept to one before it.
            while (_rendering && _samples.Count > 2 && _samples[1].stamp <= _render)
                _samples.RemoveAt(0);

            if (_rendering)
                _offset += before - Raw(_render);
            return true;
        }

        /// <summary>
        /// TryPose is where to draw the character at <paramref name="nowMs"/> (this client's clock), <paramref name="deltaSeconds"/>
        /// after the last call; <paramref name="current"/> is where it is drawn now, which the first pose starts from.
        /// False until two samples are kept: the kit draws it meanwhile.
        /// </summary>
        public bool TryPose(double nowMs, float deltaSeconds, Vector3 current, out Vector3 pose)
        {
            if (_samples.Count < 2 || !(_interval > 0.0))
            {
                pose = current;
                return false;
            }
            double deltaMs = Math.Max(0.0, deltaSeconds * 1000.0);
            _base += deltaMs * BaseDriftPerSecond / 1000.0;
            double target = nowMs - _base - Delay;
            if (!_rendering)
            {
                _render = target;
                _offset = current - Raw(_render);
                _rendering = true;
            }
            else
            {
                _render += deltaMs;
                double error = target - _render;
                if (Math.Abs(error) > ResetMilliseconds)
                {
                    Vector3 before = Raw(_render);
                    _render = target;
                    _offset += before - Raw(_render);
                }
                else
                {
                    double slew = SlewFraction * deltaMs;
                    _render += Math.Max(-slew, Math.Min(slew, error));
                }
                _offset *= Mathf.Exp(-_correctionRate * deltaSeconds);
            }
            pose = Raw(_render) + _offset;
            return true;
        }

        /// <summary>Raw is the trajectory at <paramref name="time"/>: between samples, or extrapolated past the newest one.</summary>
        private Vector3 Raw(double time)
        {
            int count = _samples.Count;
            if (count == 0)
                return Vector3.zero;
            if (time <= _samples[0].stamp)
                return _samples[0].position;
            for (int i = 1; i < count; ++i)
            {
                Sample next = _samples[i];
                if (time <= next.stamp)
                {
                    Sample prior = _samples[i - 1];
                    return Vector3.Lerp(prior.position, next.position, (float)((time - prior.stamp) / (next.stamp - prior.stamp)));
                }
            }
            Sample last = _samples[count - 1];
            if (count < 2)
                return last.position;
            Sample previous = _samples[count - 2];
            double span = last.stamp - previous.stamp;
            if (span >= StaleMilliseconds)
                return last.position;
            double horizon = Math.Min(time - last.stamp, _maxExtrapolationMilliseconds);
            return last.position + (last.position - previous.position) * (float)(horizon / span);
        }
    }
}
