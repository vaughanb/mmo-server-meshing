// Server Meshing test rig — the synthetic adopter (Slice 2b, slices/slice2b_design.md decision 13): lets a cell hand the
// load generator's synthetic players to the cell that takes the rectangle they stand in. The owner captures one (id,
// name, appearance, level, motion, handoff count, its own zone), the mesh despawns it and sends the bytes, and the new
// owner spawns it at the same pose with its brain re-seeded (seed = hash(id, handoffs)) and its wander area clamped to
// that cell's owned rectangle. On a Dynamic map, a cell whose owned rectangle shrinks also confines the wander areas of
// the synthetic players it keeps, so they do not walk into the part it gave away. Registered on every map server
// (Kind "synthetic"); the mesh only asks it during a layout change, so Static maps never see it act.
using System.Collections.Generic;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshSyntheticAdopter moves the load generator's synthetic players across a layout change.</summary>
    public class ServerMeshSyntheticAdopter : MonoBehaviour, IMeshServerCharacterAdopter
    {
        /// <summary>AdopterKind is the adopter's Kind on the wire.</summary>
        public const string AdopterKind = "synthetic";
        private const float PollSeconds = 0.5f;

        private sealed class Adopted
        {
            public int handoffs;
            public ServerMeshLoadMotionKind motion;
            public bool fight;
        }

        // Players this cell adopted (by character id), and how many handoffs each has had.
        private readonly Dictionary<string, Adopted> _adopted = new Dictionary<string, Adopted>();
        private ServerMeshKinematicWalk _walk;
        private bool _walkFight = ServerMeshLoadMotion.DefaultFight;
        private bool _registered;
        private float _nextPoll;
        private bool _hasOwned;
        private MeshRect _lastOwned;

        public string Kind => AdopterKind;

#if !EXCLUDE_SERVER_CODES
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (FindFirstObjectByType<ServerMeshSyntheticAdopter>() != null)
                return;
            GameObject go = new GameObject("[Mesh] SyntheticAdopter");
            DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshSyntheticAdopter>();
        }
#endif

        private void Update()
        {
            if (Time.unscaledTime < _nextPoll)
                return;
            _nextPoll = Time.unscaledTime + PollSeconds;
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null)
                return;
            if (!_registered)
            {
                _registered = true;
                MeshServerCharacterAdopters.Register(this);
            }
            if (mesh.OwnCell.OnDynamicMap)
                WatchOwned(mesh);
        }

        /// <summary>A shrunk owned rectangle confines every wander area on this cell to what it still owns.</summary>
        private void WatchOwned(MeshMapNetworkManager mesh)
        {
            MeshRect owned = mesh.OwnCell.owned;
            if (!_hasOwned || owned.IsEmpty)
            {
                _hasOwned = !owned.IsEmpty;
                _lastOwned = owned;
                return;
            }
            if (owned.Equals(_lastOwned))
                return;
            _lastOwned = owned;
            if (!ServerMeshSyntheticState.WanderArea(owned, mesh.OwnCell.frameOrigin, out Rect area))
                return;
            int confined = 0;
            foreach (SyntheticPlayerBrain brain in FindObjectsByType<SyntheticPlayerBrain>(FindObjectsSortMode.None))
            {
                if (brain == null || !TryIntersect(brain.Area, area, out Rect kept) || kept == brain.Area)
                    continue;
                brain.ConfineTo(kept);
                ++confined;
            }
            ServerMeshLoadGenerator generator = FindFirstObjectByType<ServerMeshLoadGenerator>();
            if (generator != null)
                generator.ConfineTo(area);
            if (_walk != null && TryIntersect(_walk.Area, area, out Rect walkArea))
                _walk.Setup(walkArea, _walkFight);
            if (confined > 0)
                Logging.Log("Mesh", System.FormattableString.Invariant(
                    $"Synthetic wander areas confined on {ServerMeshSyntheticState.Printable(mesh.OwnZoneId)}: {confined} brain(s) to x [{area.xMin:0.##}, {area.xMax:0.##}] z [{area.yMin:0.##}, {area.yMax:0.##}]"));
        }

        public bool TryCapture(BasePlayerCharacterEntity character, out byte[] state)
        {
            state = null;
            if (character == null || !(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh))
                return false;
            ServerMeshLoadGenerator generator = FindFirstObjectByType<ServerMeshLoadGenerator>();
            string id = character.Id;
            bool adopted = !string.IsNullOrEmpty(id) && _adopted.ContainsKey(id);
            if (!adopted && (generator == null || !generator.Owns(character)))
                return false;
            Adopted record = adopted ? _adopted[id] : null;
            SyntheticPlayerBrain brain = character.GetComponent<SyntheticPlayerBrain>();
            ServerMeshLoadMotionKind motion;
            bool fight;
            if (brain != null)
            {
                motion = ServerMeshLoadMotionKind.Steered;
                fight = brain.Fights;
            }
            else if (record != null)
            {
                motion = record.motion;
                fight = record.fight;
            }
            else
            {
                motion = generator.Motion;
                fight = generator.Fight;
            }
            ServerMeshSyntheticState s = new ServerMeshSyntheticState
            {
                id = id,
                name = character.CharacterName,
                classId = character.DataId,
                entityId = character.EntityId,
                level = character.Level,
                handoffs = (record != null ? record.handoffs : 0) + 1,
                motion = motion,
                fight = fight,
                fromZone = mesh.OwnZoneId,
            };
            state = s.Encode();
            if (adopted)
                _adopted.Remove(id);
            return true;
        }

        public bool TryAdopt(byte[] state, Vector3 position, float yaw, MeshRect ownedRect)
        {
            if (!ServerMeshSyntheticState.TryDecode(state, out ServerMeshSyntheticState s))
            {
                Logging.LogWarning("Mesh", $"Synthetic adopt refused: the state ({(state != null ? state.Length : 0)} bytes) does not decode");
                return false;
            }
            if (!(BaseGameNetworkManager.Singleton is MeshMapNetworkManager mesh) || !mesh.IsServer || mesh.OwnCell == null)
            {
                Logging.LogWarning("Mesh", $"Synthetic adopt refused: {ServerMeshSyntheticState.Printable(s.name)} arrived on a process that is not a map server");
                return false;
            }
            if (!ServerMeshSyntheticState.WanderArea(ownedRect, mesh.OwnCell.frameOrigin, out Rect area))
            {
                Logging.LogWarning("Mesh", $"Synthetic adopt refused: {ServerMeshSyntheticState.Printable(s.name)} zone {ServerMeshSyntheticState.Printable(s.fromZone)}->{ServerMeshSyntheticState.Printable(mesh.OwnZoneId)}: this cell owns nothing");
                return false;
            }
            BasePlayerCharacterEntity entity = ServerMeshLoadGenerator.SpawnSynthetic(mesh, s.id, s.name, s.classId, s.entityId, s.level, position, yaw);
            if (entity == null)
            {
                Logging.LogWarning("Mesh", $"Synthetic adopt refused: {ServerMeshSyntheticState.Printable(s.name)}: no player entity {s.entityId} to spawn");
                return false;
            }
            int seed = ServerMeshSyntheticState.BrainSeed(s.id, s.handoffs);
            switch (s.motion)
            {
                case ServerMeshLoadMotionKind.Kinematic:
                    EnsureWalk(area, s.fight).AddWalking(entity, seed);
                    break;
                case ServerMeshLoadMotionKind.Inert:
                    EnsureWalk(area, s.fight).AddStanding(entity, seed);
                    break;
                default:
                    entity.gameObject.AddComponent<SyntheticPlayerBrain>().Setup(entity, area, seed, s.fight);
                    break;
            }
            _adopted[s.id] = new Adopted { handoffs = s.handoffs, motion = s.motion, fight = s.fight };
            Logging.Log("Mesh", ServerMeshSyntheticState.AdoptedLine(s.name, s.fromZone, mesh.OwnZoneId));
            return true;
        }

        /// <summary>The walk loop for adopted brainless players, its area the latest owned rectangle.</summary>
        private ServerMeshKinematicWalk EnsureWalk(Rect area, bool fight)
        {
            if (_walk == null)
                _walk = gameObject.AddComponent<ServerMeshKinematicWalk>();
            _walkFight = fight;
            _walk.Setup(area, fight);
            return _walk;
        }

        /// <summary>TryIntersect is the overlap of two scene-frame rectangles (y is z); false when they do not overlap.</summary>
        public static bool TryIntersect(Rect a, Rect b, out Rect result)
        {
            float minX = Mathf.Max(a.xMin, b.xMin), maxX = Mathf.Min(a.xMax, b.xMax);
            float minZ = Mathf.Max(a.yMin, b.yMin), maxZ = Mathf.Min(a.yMax, b.yMax);
            result = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            return minX < maxX && minZ < maxZ;
        }
    }
}
