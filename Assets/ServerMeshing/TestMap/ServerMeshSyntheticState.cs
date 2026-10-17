// Server Meshing test rig — what a synthetic player needs to be spawned again on another cell (Slice 2b,
// slices/slice2b_design.md decision 13): the bytes ServerMeshSyntheticAdopter sends with a handoff, the re-seed rule
// for its brain, and the wander area on the new owner. Pure: no scene, no network.
using System.IO;
using System.Text;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshSyntheticState is one captured synthetic player.</summary>
    public struct ServerMeshSyntheticState
    {
        /// <summary>Version is the first byte of an encoded state; a state of another version is refused.</summary>
        public const byte Version = 1;
        /// <summary>MaxStringLength bounds every string of a state (ids and names are short rig names).</summary>
        public const int MaxStringLength = 256;

        /// <summary>id is the character id (load-k or load-cell-i).</summary>
        public string id;
        /// <summary>name is the character name (Load0012).</summary>
        public string name;
        /// <summary>classId is the player character data id (the appearance key with entityId).</summary>
        public int classId;
        /// <summary>entityId is the player character entity id the prefab is looked up by.</summary>
        public int entityId;
        /// <summary>level is the character's level.</summary>
        public int level;
        /// <summary>handoffs counts the handoffs of this player including this one (1 on its first).</summary>
        public int handoffs;
        /// <summary>motion is how it moves (steered brain, kinematic walk, inert).</summary>
        public ServerMeshLoadMotionKind motion;
        /// <summary>fight is whether it attacks monsters in reach.</summary>
        public bool fight;
        /// <summary>fromZone is the cell that captured it.</summary>
        public string fromZone;

        /// <summary>Encode writes the state as bytes: the version, then every field in declaration order.</summary>
        public byte[] Encode()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Version);
                writer.Write(id ?? string.Empty);
                writer.Write(name ?? string.Empty);
                writer.Write(classId);
                writer.Write(entityId);
                writer.Write(level);
                writer.Write(handoffs);
                writer.Write((byte)motion);
                writer.Write(fight);
                writer.Write(fromZone ?? string.Empty);
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// TryDecode reads bytes Encode wrote: false for null, truncated, trailing or other-version bytes, an empty id or
        /// name, a string over <see cref="MaxStringLength"/>, a negative handoff count or an unknown motion.
        /// </summary>
        public static bool TryDecode(byte[] bytes, out ServerMeshSyntheticState state)
        {
            state = default;
            if (bytes == null || bytes.Length == 0)
                return false;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadByte() != Version)
                        return false;
                    ServerMeshSyntheticState s = new ServerMeshSyntheticState
                    {
                        id = reader.ReadString(),
                        name = reader.ReadString(),
                        classId = reader.ReadInt32(),
                        entityId = reader.ReadInt32(),
                        level = reader.ReadInt32(),
                        handoffs = reader.ReadInt32(),
                    };
                    byte motion = reader.ReadByte();
                    s.fight = reader.ReadBoolean();
                    s.fromZone = reader.ReadString();
                    if (stream.Position != stream.Length)
                        return false;
                    if (motion > (byte)ServerMeshLoadMotionKind.Inert || s.handoffs < 0)
                        return false;
                    s.motion = (ServerMeshLoadMotionKind)motion;
                    if (string.IsNullOrEmpty(s.id) || string.IsNullOrEmpty(s.name) || s.id.Length > MaxStringLength || s.name.Length > MaxStringLength || s.fromZone.Length > MaxStringLength)
                        return false;
                    state = s;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (System.FormatException)
            {
                // a string length prefix that is not a valid 7-bit encoded integer
                return false;
            }
        }

        /// <summary>
        /// BrainSeed is the brain seed of player id after handoffs handoffs: FNV-1a over the id's UTF-16 code units, the
        /// handoff count folded in, then a final mix. The same on every process (string.GetHashCode is not).
        /// </summary>
        public static int BrainSeed(string id, int handoffs)
        {
            unchecked
            {
                uint h = 2166136261u;
                string s = id ?? string.Empty;
                for (int i = 0; i < s.Length; ++i)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                h ^= (uint)handoffs;
                h *= 16777619u;
                h ^= h >> 16;
                h *= 0x85ebca6bu;
                h ^= h >> 13;
                return (int)h;
            }
        }

        /// <summary>
        /// WanderArea is the wander area on the cell that adopts a player: its owned rectangle (world frame) moved into the
        /// cell's scene frame and kept a hair inside, as the generator keeps its own; empty (false) when nothing is left.
        /// </summary>
        public static bool WanderArea(MeshRect owned, Vector3 frameOrigin, out Rect area)
        {
            area = default;
            if (owned.IsEmpty)
                return false;
            const float inset = 0.01f;
            float minX = owned.minX - frameOrigin.x + inset, maxX = owned.maxX - frameOrigin.x - inset;
            float minZ = owned.minZ - frameOrigin.z + inset, maxZ = owned.maxZ - frameOrigin.z - inset;
            if (!(minX < maxX && minZ < maxZ))
                return false;
            area = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
            return true;
        }

        /// <summary>AdoptedLine is the line the adopting cell logs: Synthetic adopted name zone from->to.</summary>
        public static string AdoptedLine(string name, string fromZone, string toZone)
        {
            return "Synthetic adopted " + Printable(name) + " zone " + Printable(fromZone) + "->" + Printable(toZone);
        }

        /// <summary>Printable keeps a log line on one physical line and free of braces.</summary>
        public static string Printable(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "?";
            return text.Replace('{', '(').Replace('}', ')').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
