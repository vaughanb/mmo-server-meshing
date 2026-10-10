// Server Meshing add-on — one cell runs every monster (rig key MESH_MONSTER_CELL=<cell>; Phase 0 step 9 decision 6).
// When set, the named cell owns every MonsterSpawnArea and every other cell owns none (harvestable and item drop areas
// keep their geographic owner); monster handoff is off, so a monster stays with that cell wherever it walks; and every
// other cell streams every character it owns to that cell, whatever the distance, so the monster cell's monsters see
// the other cells' players (AI awake, aggro, rewards). The monster cell's own monsters reach the other cells through the
// ordinary band test, which measures to the neighbour's interest rectangle and reads 0 inside it. Logged once per cell.
// Default off: unset, ownership, handoff and streaming are exactly as before.
using System;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>ENV_MONSTER_CELL names the one cell that owns every monster spawn area (rig key; unset = geographic ownership).</summary>
        public const string ENV_MONSTER_CELL = "MESH_MONSTER_CELL";

        /// <summary>
        /// TryParseMonsterCell reads MESH_MONSTER_CELL: null or blank is unset (true, cell null); a trimmed id of letters,
        /// digits, '_' or '-' is the cell (true); anything else is false with an error naming the key, cell null.
        /// </summary>
        public static bool TryParseMonsterCell(string text, out string cell, out string error)
        {
            cell = null;
            error = null;
            string value = text == null ? string.Empty : text.Trim();
            if (value.Length == 0)
                return true;
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-')
                {
                    error = $"{ENV_MONSTER_CELL}={PrintableCell(value)} is not a cell id (letters, digits, _ and - only)";
                    return false;
                }
            }
            cell = value;
            return true;
        }

        /// <summary>
        /// OwnsMonsterArea is the spawn-area ownership rule: with no monster cell, the geographic owner decides; with one,
        /// only the cell whose id equals it (ordinal, as cell ids are) owns a monster spawn area, wherever the area lies.
        /// </summary>
        public static bool OwnsMonsterArea(string monsterCell, string ownCellId, bool geographicOwner)
        {
            if (string.IsNullOrEmpty(monsterCell))
                return geographicOwner;
            return string.Equals(monsterCell, ownCellId, StringComparison.Ordinal);
        }

        /// <summary>
        /// StreamsEveryCharacterTo is true when a cell must stream every owned character to the neighbour
        /// <paramref name="neighbourCellId"/> regardless of the band: a monster cell is set, this cell is not it, and the
        /// neighbour is.
        /// </summary>
        public static bool StreamsEveryCharacterTo(string monsterCell, string ownCellId, string neighbourCellId)
        {
            return !string.IsNullOrEmpty(monsterCell)
                && !string.Equals(ownCellId, monsterCell, StringComparison.Ordinal)
                && string.Equals(neighbourCellId, monsterCell, StringComparison.Ordinal);
        }

        /// <summary>
        /// MonsterCellLine is the once line: <c>MESH_MONSTER_CELL=E: cell E owns every monster spawn area</c> on the monster
        /// cell, <c>MESH_MONSTER_CELL=E: this cell owns no monster spawn area</c> on the others.
        /// </summary>
        public static string MonsterCellLine(string monsterCell, bool isMonsterCell)
        {
            string cell = PrintableCell(monsterCell);
            return isMonsterCell
                ? $"{ENV_MONSTER_CELL}={cell}: cell {cell} owns every monster spawn area"
                : $"{ENV_MONSTER_CELL}={cell}: this cell owns no monster spawn area";
        }

        private static string PrintableCell(string text) => string.IsNullOrEmpty(text) ? "-" : text.Replace('{', '(').Replace('}', ')').Replace('\n', ' ').Replace('\r', ' ');

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        private string _monsterCell;
        private bool _monsterCellLogged;

        /// <summary>Reads MESH_MONSTER_CELL when the server starts; when set, monster handoff is switched off on this cell.</summary>
        private void MonsterCellOnStartServer()
        {
            _monsterCell = null;
            _monsterCellLogged = false;
            string value = Environment.GetEnvironmentVariable(ENV_MONSTER_CELL);
            if (string.IsNullOrEmpty(value))
                return;
            if (!TryParseMonsterCell(value, out string cell, out string error))
            {
                Logging.LogError("Mesh", $"Monster cell error: {error}; monster spawn areas keep their geographic owner");
                return;
            }
            if (cell == null)
                return;
            _monsterCell = cell;
            handoffMonsters = false; // a monster stays with the monster cell wherever it chases
            LogMonsterCellOnce();
        }

        /// <summary>Whether this cell runs the monster spawn area at <paramref name="areaPosition"/> (scene frame).</summary>
        private bool MonsterAreaOwnedHere(Vector3 areaPosition)
        {
            if (_monsterCell == null)
                return MeshCell.Same(world.OwnerOf(OwnCell, areaPosition), OwnCell);
            LogMonsterCellOnce();
            return OwnsMonsterArea(_monsterCell, OwnZoneId, false);
        }

        /// <summary>Whether every owned character goes to band neighbour <paramref name="neighbour"/> whatever its distance.</summary>
        private bool StreamsEveryCharacterTo(int neighbour)
        {
            return _monsterCell != null && StreamsEveryCharacterTo(_monsterCell, OwnZoneId, OwnCell.bandNeighbours[neighbour].id);
        }

        private void LogMonsterCellOnce()
        {
            if (_monsterCellLogged || !HasOwnZone)
                return;
            _monsterCellLogged = true;
            if (world != null && world.GetCell(OwnMapId, _monsterCell) == null)
                Logging.LogError("Mesh", $"Monster cell error: {ENV_MONSTER_CELL}={PrintableCell(_monsterCell)} names no cell of map {PrintableCell(OwnMapId)}; no cell owns a monster spawn area");
            bool isMonsterCell = OwnsMonsterArea(_monsterCell, OwnZoneId, false);
            Logging.Log("Mesh", MonsterCellLine(_monsterCell, isMonsterCell));
            if (!isMonsterCell && OwnCell != null && !IsBandNeighbour(_monsterCell))
                Logging.LogWarning("Mesh", $"{ENV_MONSTER_CELL}={PrintableCell(_monsterCell)}: cell {PrintableCell(OwnZoneId)} has no band link to it, so its characters are not streamed there");
        }

        private bool IsBandNeighbour(string cellId)
        {
            for (int i = 0; i < OwnCell.bandNeighbours.Count; ++i)
            {
                if (string.Equals(OwnCell.bandNeighbours[i].id, cellId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
#endif
    }
}
