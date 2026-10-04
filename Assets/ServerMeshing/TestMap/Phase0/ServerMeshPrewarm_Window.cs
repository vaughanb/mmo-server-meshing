// Server Meshing test rig — pre-warm and subscription bursts, setting and measurement part (Phase 0 step 8a): the
// setting's grammar and checks (puppet motion, the burst disc and the group's park out of the eyes' view), the burst's
// frame periods from its first frame to 2 s after its last action, the spawn time of this component's own spawn calls,
// and the Prewarm setting and burst lines. Rig instrumentation.
using System;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshPrewarm
    {
        /// <summary>AfterSeconds is how long after the burst's last action its frames are still measured.</summary>
        public const float AfterSeconds = 2f;
        /// <summary>OverBudgetFactor is the share of the frame budget a frame period must exceed to count as over budget.</summary>
        public const float OverBudgetFactor = 1.05f;

        private bool _measuring;
        private bool _actedThisFrame;
        private long _measureLast;
        private long _lastActionTicks;
        private long _worstTicks;
        private long _spawnTicks;
        private int _actedFrames, _afterFrames, _overBudget, _acted;
        private double _budgetMs;
        private int _gcBurstBase;

        private void BeginMeasure(long ticks)
        {
            _measuring = true;
            _measureLast = ticks;
            _worstTicks = _spawnTicks = 0;
            _actedFrames = _afterFrames = _overBudget = 0;
            _budgetMs = BurstBudgetMs(MeshWorkProbe.BudgetMs, MeshWorkProbe.EffectiveBudgetMs);
            _gcBurstBase = GC.CollectionCount(0);
        }

        private void ActedThisFrame() => _actedThisFrame = true;

        private void EndBurst(long ticks) => _lastActionTicks = ticks;

        /// <summary>MeasureFrame takes the previous frame's period at the start of this frame's Update.</summary>
        private void MeasureFrame(long ticks)
        {
            if (!_measuring)
                return;
            long period = ticks - _measureLast;
            _measureLast = ticks;
            if (_actedThisFrame)
                _actedFrames++;
            else
                _afterFrames++;
            _actedThisFrame = false;
            if (period > _worstTicks)
                _worstTicks = period;
            double periodMs = period * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (_budgetMs > 0 && periodMs > OverBudgetFactor * _budgetMs)
                _overBudget++;
            if (!_burstDone || ticks - _lastActionTicks < (long)(AfterSeconds * System.Diagnostics.Stopwatch.Frequency))
                return;
            _measuring = false;
            bool timed = _kind != ServerMeshPrewarmKind.Range && _kind != ServerMeshPrewarmKind.Group;
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Logging.Log("Mesh", BurstLine(_mesh.OwnZoneId, _kind, _kind == ServerMeshPrewarmKind.Range ? _amount : _acted, _perFrame, _actedFrames, timed, _spawnTicks * toMs, _afterFrames, _overBudget,
                _budgetMs, _worstTicks * toMs, GC.CollectionCount(0) - _gcBurstBase));
        }

        /// <summary>
        /// BurstBudgetMs is the budget a burst's frames are held to: <paramref name="effectiveBudgetMs"/> (the probe's
        /// EffectiveBudgetMs) when the process is capped (<paramref name="cappedBudgetMs"/>, the probe's BudgetMs, above 0
        /// and finite), else 0, uncapped: the burst line then prints budget uncapped and counts no frame over budget, even
        /// when a nominal frame rate gives the Work ticks line a budget.
        /// </summary>
        public static double BurstBudgetMs(float cappedBudgetMs, float effectiveBudgetMs)
        {
            bool capped = cappedBudgetMs > 0f && !float.IsInfinity(cappedBudgetMs);
            return capped && effectiveBudgetMs > 0f && !float.IsInfinity(effectiveBudgetMs) ? effectiveBudgetMs : 0.0;
        }

        /// <summary>KindName is the setting word of <paramref name="kind"/>: steered, kinematic, ship, range or group.</summary>
        public static string KindName(ServerMeshPrewarmKind kind)
        {
            switch (kind)
            {
                case ServerMeshPrewarmKind.Kinematic: return "kinematic";
                case ServerMeshPrewarmKind.Ship: return "ship";
                case ServerMeshPrewarmKind.Range: return "range";
                case ServerMeshPrewarmKind.Group: return "group";
                default: return "steered";
            }
        }

        /// <summary>
        /// SettingLine is the pre-warm's setting line: <c>Prewarm: kind kinematic n 650 at 30.0 s perFrame 50 on cell C</c>
        /// (perFrame 0: all at once; for range n is the new range in metres, up to two decimals).
        /// </summary>
        public static string SettingLine(ServerMeshPrewarmKind kind, double amount, float start, int perFrame, string cell)
        {
            StringBuilder b = new StringBuilder(96);
            b.Append("Prewarm: kind ").Append(KindName(kind)).Append(" n ").Append(Amount(amount)).Append(" at ").Append(Fixed(start, "0.0"));
            b.Append(" s perFrame ").Append(Count(perFrame)).Append(" on cell ").Append(Printable(cell));
            return b.ToString();
        }

        /// <summary>
        /// BurstLine is the burst line: n is the ghosts that actually spawned (the mesh's ghost count rose on their first
        /// snapshot), ships spawned or characters teleported (for range the new range in metres, up to two decimals);
        /// frames is the frames that acted (spawned, changed the range or teleported), the spawn time in
        /// ms of this component's own spawn calls in total and per puppet ("n/a" when <paramref name="spawnTimed"/> is false,
        /// for range and group; 0 per puppet with none), the frames measured after the last acting frame, the frames of the
        /// whole measurement whose period exceeds 1.05 × the budget (budget "uncapped ms" and 0 over when it is not above 0),
        /// and the worst frame period; zeros for negatives and non-finite values, invariant and without braces.
        /// </summary>
        public static string BurstLine(string cell, ServerMeshPrewarmKind kind, double amount, int perFrame, int frames, bool spawnTimed, double spawnMs,
            int afterFrames, int overBudget, double budgetMs, double worstMs, long gc0)
        {
            bool capped = budgetMs > 0 && !double.IsInfinity(budgetMs);
            StringBuilder b = new StringBuilder(192);
            b.Append("Prewarm burst: cell ").Append(Printable(cell)).Append(" kind ").Append(KindName(kind)).Append(" n ").Append(Amount(amount));
            b.Append(" perFrame ").Append(Count(perFrame)).Append(" frames ").Append(Count(frames));
            b.Append(" | spawn ms ");
            if (spawnTimed)
                b.Append(Fixed(spawnMs, "0.000")).Append(" per puppet ").Append(Fixed(amount >= 1 ? Positive(spawnMs) / amount : 0, "0.000"));
            else
                b.Append("n/a per puppet n/a");
            b.Append(" | after frames ").Append(Count(afterFrames)).Append(" over budget ").Append(Count(capped ? overBudget : 0));
            b.Append(" budget ").Append(capped ? Fixed(budgetMs, "0.000") : "uncapped").Append(" ms worst ").Append(Fixed(worstMs, "0.000")).Append(" ms");
            b.Append(" | gc0 ").Append(Count(gc0));
            return b.ToString();
        }

        /// <summary>
        /// TryParseSetting reads <c>&lt;kind&gt;:&lt;N&gt;@&lt;t&gt;[/&lt;perFrame&gt;]</c> (parts trimmed, the kind ignoring
        /// case, numbers invariant): kind steered, kinematic, ship, range or group; <paramref name="amount"/> N, a whole
        /// number from 1 to 10,000 for the spawn kinds, a finite number of metres above 0 and at most 10,000 for range;
        /// <paramref name="start"/> t, finite seconds of at least 0 (for group at least GroupLeadSeconds, since its
        /// characters spawn that long before the burst); perFrame a whole number from 1 to N, refused for range, 0 when
        /// absent (all in one frame). False with zeros and an error naming the key and the bad part otherwise.
        /// </summary>
        public static bool TryParseSetting(string text, out ServerMeshPrewarmKind kind, out float amount, out float start, out int perFrame, out string error)
        {
            kind = ServerMeshPrewarmKind.Steered;
            amount = start = 0f;
            perFrame = 0;
            string value = text == null ? string.Empty : text.Trim();
            int colon = value.IndexOf(':');
            int at = value.IndexOf('@');
            int slash = value.IndexOf('/');
            int end = slash >= 0 ? slash : value.Length;
            if (colon < 0 || at < colon || end < at || value.IndexOf(':', colon + 1) >= 0 || value.IndexOf('@', at + 1) >= 0 || (slash >= 0 && value.IndexOf('/', slash + 1) >= 0))
            {
                error = $"{EnvironmentKey}={Printable(value)} is not <steered|kinematic|ship|range|group>:<N>@<t>[/<perFrame>]";
                return false;
            }
            string kindText = value.Substring(0, colon).Trim();
            string amountText = value.Substring(colon + 1, at - colon - 1).Trim();
            string startText = value.Substring(at + 1, end - at - 1).Trim();
            if (!TryParseKind(kindText, out ServerMeshPrewarmKind parsedKind))
            {
                error = $"{EnvironmentKey}={Printable(value)}: the kind {Printable(kindText)} is not steered, kinematic, ship, range or group";
                return false;
            }
            float n;
            if (parsedKind == ServerMeshPrewarmKind.Range)
            {
                if (!TryFinite(amountText, out n) || n <= 0f || n > MaxRangeMetres)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the range {Printable(amountText)} is not a number of metres above 0 and at most {Amount(MaxRangeMetres)}";
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(amountText, NumberStyles.None, CultureInfo.InvariantCulture, out int whole) || whole < 1 || whole > MaxCount)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: the count {Printable(amountText)} is not a whole number from 1 to {MaxCount}";
                    return false;
                }
                n = whole;
            }
            if (!TryFinite(startText, out float t) || t < 0f)
            {
                error = $"{EnvironmentKey}={Printable(value)}: the time {Printable(startText)} is not a number of seconds of at least 0";
                return false;
            }
            if (parsedKind == ServerMeshPrewarmKind.Group && t < GroupLeadSeconds)
            {
                error = $"{EnvironmentKey}={Printable(value)}: the time {Printable(startText)} is under the group's {Amount(GroupLeadSeconds)} s lead"
                    + $" (its characters spawn {Amount(GroupLeadSeconds)} s before the burst)";
                return false;
            }
            int paced = 0;
            if (slash >= 0)
            {
                string pacedText = value.Substring(slash + 1).Trim();
                if (parsedKind == ServerMeshPrewarmKind.Range)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: perFrame {Printable(pacedText)} is given for range, which changes the range at once";
                    return false;
                }
                if (!int.TryParse(pacedText, NumberStyles.None, CultureInfo.InvariantCulture, out paced) || paced < 1 || paced > n)
                {
                    error = $"{EnvironmentKey}={Printable(value)}: perFrame {Printable(pacedText)} is not a whole number from 1 to the count {Amount(n)}";
                    return false;
                }
            }
            kind = parsedKind;
            amount = n;
            start = t;
            perFrame = paced;
            error = null;
            return true;
        }

        /// <summary>
        /// CheckPuppetMotion is true unless a ghost kind disagrees with the process's MESH_PUPPET_MOTION
        /// (<paramref name="puppetMotion"/>): steered needs it unset, blank or steered, kinematic needs kinematic; otherwise
        /// false with an error naming both keys. Ship, range and group are always true.
        /// </summary>
        public static bool CheckPuppetMotion(ServerMeshPrewarmKind kind, string puppetMotion, out string error)
        {
            error = null;
            if (kind != ServerMeshPrewarmKind.Steered && kind != ServerMeshPrewarmKind.Kinematic)
                return true;
            string trimmed = puppetMotion == null ? string.Empty : puppetMotion.Trim();
            MeshPuppetMotion motion = MeshPuppetMotion.Steered;
            bool known = trimmed.Length == 0 || MeshPuppetMotionSetting.TryParse(trimmed, out motion);
            bool kinematic = kind == ServerMeshPrewarmKind.Kinematic;
            if (known && (motion == MeshPuppetMotion.Kinematic) == kinematic)
                return true;
            error = $"{EnvironmentKey} kind {KindName(kind)} needs {MeshPuppetMotionSetting.EnvironmentKey} {(kinematic ? "kinematic" : "unset or steered")}, but it is "
                + (trimmed.Length == 0 ? "unset" : Printable(trimmed));
            return false;
        }

        /// <summary>
        /// CheckMeshPuppetMotion is true unless a ghost kind disagrees with the map server's own puppet motion
        /// (<paramref name="meshMotion"/>, MeshMapNetworkManager.puppetMotion: MESH_PUPPET_MOTION or the inspector's value):
        /// steered needs Steered, kinematic needs Kinematic; otherwise false with an error naming the key and both motions.
        /// Ship, range and group are always true.
        /// </summary>
        public static bool CheckMeshPuppetMotion(ServerMeshPrewarmKind kind, MeshPuppetMotion meshMotion, out string error)
        {
            error = null;
            if (kind != ServerMeshPrewarmKind.Steered && kind != ServerMeshPrewarmKind.Kinematic)
                return true;
            bool kinematic = kind == ServerMeshPrewarmKind.Kinematic;
            if ((meshMotion == MeshPuppetMotion.Kinematic) == kinematic)
                return true;
            error = $"{EnvironmentKey} kind {KindName(kind)} needs the map server's puppet motion {(kinematic ? "kinematic" : "steered")}"
                + $" ({MeshPuppetMotionSetting.EnvironmentKey} or the inspector's puppetMotion), but it is {(meshMotion == MeshPuppetMotion.Kinematic ? "kinematic" : "steered")}";
            return false;
        }

        /// <summary>
        /// EyesRadius is how far from the burst centre the eyes can stand: they ride load players crowded into
        /// MESH_LOAD_AREA's square of half-size <paramref name="areaHalfSize"/> (its corners √2 of that away), and the burst
        /// disc's 50 m is the least; non-finite or negative half-sizes count as 0.
        /// </summary>
        public static float EyesRadius(float areaHalfSize)
        {
            float half = float.IsNaN(areaHalfSize) || float.IsInfinity(areaHalfSize) ? 0f : Math.Max(0f, areaHalfSize);
            return Math.Max(DiscRadiusMetres, half * (float)Math.Sqrt(2.0));
        }

        /// <summary>
        /// GroupViewRange is the range a group character is seen at, folded into <paramref name="range"/>: the larger of it
        /// and the identity's own <paramref name="identityRange"/> (the interest manager uses a target's own range when
        /// above 0), or infinity for an always-visible identity.
        /// </summary>
        public static float GroupViewRange(float range, float identityRange, bool alwaysVisible)
        {
            if (alwaysVisible)
                return float.PositiveInfinity;
            return identityRange > range ? identityRange : range;
        }

        /// <summary>
        /// ParkOutOfView is true when the park disc (radius GroupParkRadiusMetres about <paramref name="park"/>) is beyond
        /// <paramref name="range"/> of the eyes' disc (<paramref name="eyesRadius"/> about <paramref name="eyesCentre"/>);
        /// <paramref name="gap"/> is the distance between the two discs' nearest points (x is scene x, y is scene z).
        /// </summary>
        public static bool ParkOutOfView(Vector2 park, Vector2 eyesCentre, float eyesRadius, float range, out float gap)
        {
            gap = Vector2.Distance(park, eyesCentre) - GroupParkRadiusMetres - eyesRadius;
            return !float.IsNaN(range) && gap > range;
        }

        /// <summary>
        /// BurstDiscInside is true when the burst disc (DiscRadiusMetres about <paramref name="centre"/>, x is scene x, y is
        /// scene z) lies inside <paramref name="cell"/>, touching an edge included (ServerMeshMonsterPlacer.DiscInside).
        /// </summary>
        public static bool BurstDiscInside(Rect cell, Vector2 centre)
        {
            return ServerMeshMonsterPlacer.DiscInside(cell, new ServerMeshMonsterPlacer.ServerMeshMonsterDisc { Count = 1, X = centre.x, Z = centre.y, Radius = DiscRadiusMetres });
        }

        /// <summary>
        /// CheckGroupOutOfView re-checks the park against the range the spawned characters are seen at: the larger of the
        /// default range and each identity's own. When the park is in view: a Prewarm error, the characters are destroyed
        /// and no burst follows.
        /// </summary>
        private void CheckGroupOutOfView()
        {
            float range = _mesh.InterestManager != null ? _mesh.InterestManager.defaultVisibleRange : 0f;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                LiteNetLibIdentity identity = _puppets[i].identity;
                if (identity != null)
                    range = GroupViewRange(range, identity.VisibleRange, identity.AlwaysVisible);
            }
            if (ParkOutOfView(_park, new Vector2(_centre.x, _centre.z), _eyesRadius, range, out float gap))
                return;
            Logging.LogError("Mesh", $"Prewarm error: cell {Printable(_mesh.OwnZoneId)}: the group's park is {Metres(gap)} m from the eyes' disc, not beyond the {RangeText(range)} visible range of its characters; they are destroyed and no burst");
            _halted = true;
            _burstDone = true;
            for (int i = 0; i < _puppets.Count; ++i)
            {
                LiteNetLibIdentity identity = _puppets[i].identity;
                if (identity != null && !identity.IsDestroyed)
                    identity.NetworkDestroy();
            }
        }

        /// <summary>
        /// TryChooseParkCentre picks where a group waits: the corner of <paramref name="cell"/> (inset by the park disc and
        /// 5 m) farthest from <paramref name="burstCentre"/> (x is scene x, y is scene z). False with the reason when that
        /// park disc's nearest point to the eyes' disc (<paramref name="eyesRadius"/> about the burst centre, see
        /// <see cref="EyesRadius"/>) is not beyond <paramref name="range"/>.
        /// </summary>
        public static bool TryChooseParkCentre(Rect cell, Vector2 burstCentre, float eyesRadius, float range, out Vector2 park, out string error)
        {
            float inset = GroupParkRadiusMetres + 5f;
            float minX = cell.xMin + inset, maxX = cell.xMax - inset, minZ = cell.yMin + inset, maxZ = cell.yMax - inset;
            park = new Vector2(minX, minZ);
            if (minX > maxX || minZ > maxZ)
            {
                error = $"the cell is too small to park a group {Metres(inset)} m inside its edges";
                return false;
            }
            park.x = Math.Abs(minX - burstCentre.x) >= Math.Abs(maxX - burstCentre.x) ? minX : maxX;
            park.y = Math.Abs(minZ - burstCentre.y) >= Math.Abs(maxZ - burstCentre.y) ? minZ : maxZ;
            if (ParkOutOfView(park, burstCentre, eyesRadius, range, out float gap))
            {
                error = null;
                return true;
            }
            error = $"the farthest park point is {Metres(gap)} m from the eyes' disc ({Metres(eyesRadius)} m about the burst centre), not beyond the {RangeText(range)} visible range";
            return false;
        }

        /// <summary>
        /// TryReadAreaCentre reads MESH_LOAD_AREA's centre (x,z of x,z,r, r above 0, invariant): false when the text is not
        /// that. A null or blank text is false with no centre.
        /// </summary>
        public static bool TryReadAreaCentre(string text, out Vector2 centre) => TryReadArea(text, out centre, out _);

        /// <summary>
        /// TryReadArea reads MESH_LOAD_AREA (x,z,r, r above 0, invariant): its centre and <paramref name="halfSize"/> r, the
        /// half-size of the load generator's crowd square. False with zeros when the text is not that (null or blank too).
        /// </summary>
        public static bool TryReadArea(string text, out Vector2 centre, out float halfSize)
        {
            centre = Vector2.zero;
            halfSize = 0f;
            string[] parts = (text ?? string.Empty).Split(',');
            if (parts.Length != 3)
                return false;
            float[] values = new float[3];
            for (int i = 0; i < 3; ++i)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || float.IsNaN(values[i]) || float.IsInfinity(values[i]))
                    return false;
            }
            if (values[2] <= 0f)
                return false;
            centre = new Vector2(values[0], values[1]);
            halfSize = values[2];
            return true;
        }

        private static bool TryParseKind(string text, out ServerMeshPrewarmKind kind)
        {
            for (ServerMeshPrewarmKind k = ServerMeshPrewarmKind.Steered; k <= ServerMeshPrewarmKind.Group; ++k)
            {
                if (string.Equals(text, KindName(k), StringComparison.OrdinalIgnoreCase))
                {
                    kind = k;
                    return true;
                }
            }
            kind = ServerMeshPrewarmKind.Steered;
            return false;
        }

        private static bool TryFinite(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text).Replace(' ', '_');

        private static double Positive(double value) => value > 0 && !double.IsInfinity(value) ? value : 0;

        private static string Fixed(double value, string format) => Positive(value).ToString(format, CultureInfo.InvariantCulture);

        private static string Metres(float value) => float.IsNaN(value) || float.IsInfinity(value) ? "0" : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string RangeText(float range) => float.IsPositiveInfinity(range) ? "unlimited (always visible)" : Metres(range) + " m";

        private static string Amount(double value) => Positive(value).ToString("0.##", CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
