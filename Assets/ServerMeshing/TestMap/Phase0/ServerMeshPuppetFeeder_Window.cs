// Server Meshing test rig — in-process puppets fed at a fixed rate, window part (Phase 0 step 7): the Feeder setting and
// window lines. Rig instrumentation; the space game (commit 1a3a58d9) has no counterpart.
using System;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshPuppetFeeder
    {
        private void StartWindow()
        {
            _lastWorkWindow = _mesh.WorkWindowCount;
            _windowStart = Time.unscaledTimeAsDouble;
            _gcBase = GC.CollectionCount(0);
            _snapshots = _encodeTicks = _decodeTicks = _acceptTicks = _poseTicks = _totalTicks = 0;
        }

        private void WindowIfDue()
        {
            if (_mesh.WorkWindowCount == _lastWorkWindow)
                return;
            int live = 0, observed = 0;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                LiteNetLibIdentity identity = _puppets[i].identity;
                if (identity == null || identity.IsDestroyed)
                    continue;
                live++;
                if (identity.CountSubscribers() > 0)
                    observed++;
            }
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Logging.Log("Mesh", WindowLine(Time.unscaledTimeAsDouble - _windowStart, _ships, live, observed, _snapshots,
                _encodeTicks * toMs, _decodeTicks * toMs, _acceptTicks * toMs, _poseTicks * toMs, _totalTicks * toMs, GC.CollectionCount(0) - _gcBase));
            StartWindow();
        }

        /// <summary>SettingLine is the feeder's setting line: <c>Feeder: ship 200 @ 20 Hz on cell C</c>.</summary>
        public static string SettingLine(bool ships, int count, int hz, string cell)
        {
            return "Feeder: " + (ships ? "ship " : "character ") + Math.Max(0, count).ToString(CultureInfo.InvariantCulture)
                + " @ " + Math.Max(0, hz).ToString(CultureInfo.InvariantCulture) + " Hz on cell " + Printable(cell);
        }

        /// <summary>
        /// WindowLine is the Feeder window line: per-stage and total ms/s, microseconds per snapshot (total over snapshots),
        /// gc0; zeros when empty, rates 0 when the window has no length, invariant and without braces.
        /// </summary>
        public static string WindowLine(double seconds, bool ships, int count, int observed, long snapshots, double encodeMs, double decodeMs,
            double acceptMs, double poseMs, double totalMs, long gc0)
        {
            double s = Positive(seconds);
            StringBuilder b = new StringBuilder(192);
            b.Append("Feeder window ").Append(Fixed(s, "0.00")).Append(" s: ").Append(ships ? "ship " : "character ").Append(Count(count));
            b.Append(" observed ").Append(Count(observed)).Append(" | snapshots ").Append(Count(snapshots));
            b.Append(" | ms/s encode ").Append(Fixed(Rate(encodeMs, s), "0.00")).Append(" decode ").Append(Fixed(Rate(decodeMs, s), "0.00"));
            b.Append(" accept ").Append(Fixed(Rate(acceptMs, s), "0.00")).Append(" pose ").Append(Fixed(Rate(poseMs, s), "0.00"));
            b.Append(" total ").Append(Fixed(Rate(totalMs, s), "0.00"));
            b.Append(" | us/snapshot ").Append(Fixed(snapshots > 0 ? Positive(totalMs) * 1000.0 / snapshots : 0, "0.000"));
            b.Append(" | gc0 ").Append(Count(gc0));
            return b.ToString();
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text);

        private static double Rate(double amount, double seconds) => seconds > 0 ? Positive(amount) / seconds : 0;

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) ? value : 0;

        private static string Fixed(double value, string format) => Positive(value).ToString(format, CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
