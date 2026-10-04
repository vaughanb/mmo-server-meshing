// Server Meshing test rig — monsters placed at seeded points in discs, window part (Phase 0 step 8a): the Monsters
// setting and window lines. Rig instrumentation.
using System;
using System.Globalization;
using System.Text;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshMonsterPlacer
    {
        private int _lastWorkWindow = -1;
        private double _windowStart;
        private int _gcBase;
        private long _healed, _respawned;

        private void StartWindow()
        {
            _lastWorkWindow = _mesh.WorkWindowCount;
            _windowStart = Time.unscaledTimeAsDouble;
            _gcBase = GC.CollectionCount(0);
            _healed = _respawned = 0;
        }

        private void WindowIfDue()
        {
            if (_mesh.WorkWindowCount == _lastWorkWindow)
                return;
            int alive = 0, awake = 0;
            for (int i = 0; i < _placed.Count; ++i)
            {
                BaseMonsterCharacterEntity monster = _placed[i].entity;
                if (monster == null || monster.Identity == null || monster.Identity.IsDestroyed || monster.IsDead() || MeshIdentity.IsGhostEntity(monster))
                    continue;
                alive++;
                if (monster.Identity.CountSubscribers() > 0)
                    awake++;
            }
            Logging.Log("Mesh", WindowLine(Time.unscaledTimeAsDouble - _windowStart, _mesh.OwnZoneId, _placedCount, alive, awake, _healed, _respawned,
                GC.CollectionCount(0) - _gcBase));
            StartWindow();
        }

        /// <summary>
        /// SettingLine is the placer's setting line: <c>Monsters: placed 200 in 200@0,-40,30 on cell C</c>, placed being the
        /// monsters this cell placed successfully at its start (the window line's placed), the discs this cell placed
        /// joined by semicolons as the setting gave them ("-" for none).
        /// </summary>
        public static string SettingLine(int placed, string discs, string cell)
        {
            return "Monsters: placed " + Count(placed) + " in " + Printable(discs) + " on cell " + Printable(cell);
        }

        /// <summary>
        /// WindowLine is the Monsters window line: the monsters the cell placed successfully at its start (the setting line's
        /// placed; re-placements count in respawned, and a slot that never placed is not counted), its live non-ghost placed
        /// monsters (alive), those
        /// with a network subscriber (awake), the heals and re-placements of the window and gc0; zeros for negatives,
        /// invariant and without braces.
        /// </summary>
        public static string WindowLine(double seconds, string cell, int placed, int alive, int awake, long healed, long respawned, long gc0)
        {
            StringBuilder b = new StringBuilder(128);
            b.Append("Monsters window ").Append(Fixed(seconds, "0.00")).Append(" s: cell ").Append(Printable(cell));
            b.Append(" placed ").Append(Count(placed)).Append(" alive ").Append(Count(alive)).Append(" awake ").Append(Count(awake));
            b.Append(" healed ").Append(Count(healed)).Append(" respawned ").Append(Count(respawned));
            b.Append(" | gc0 ").Append(Count(gc0));
            return b.ToString();
        }

        private static string Printable(string text) => string.IsNullOrEmpty(text) ? "-" : MeshStateSender.CleanForLog(text).Replace(' ', '_');

        private static string Fixed(double value, string format) => (value > 0 && !double.IsInfinity(value) ? value : 0).ToString(format, CultureInfo.InvariantCulture);

        private static string Metres(float value) => float.IsNaN(value) || float.IsInfinity(value) ? "0" : value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Count(long value) => Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
    }
}
