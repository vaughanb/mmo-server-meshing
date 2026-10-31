// Server Meshing add-on — the attack or skill in flight at a crossing (DESIGN.md 1.06).
//
// For a client-owned character the kit applies an action's damage when the client's trigger arrives (CmdSimulate-
// ActionTrigger), checked against the hit validation the server prepared at the action's start, keyed by the entity's
// object id and the action's seed. That validation lived only on the origin, so a trigger the client sent after the
// hop reached a destination that dropped it, and the origin's demoted ghost (owned by the server now) applied the
// swing itself, on the wrong cell and unseen by the destination's players. The origin now sends what it prepared with
// the handoff state; the destination prepares the same validation for the arriving character, and the origin cancels
// its ghost's routine. Damage amounts are recomputed on the destination from the handed-off character, which is the
// one the origin computed them from.
//
// DESIGN 1.35: on a client-owned character the server's own routine pays for an action trigger by trigger (ammunition, a
// skill's resources) and takes a skill item after the last, and the demotion cancels that routine, so what it had not
// reached was never paid: a skill item cast across a line was free. The origin now settles the rest as the state is
// copied. A charge in progress crosses too (MeshCharacterChargeComponent). Not settled: a skill's OnAttack that overrides
// the default attack, in which case the kit takes no ammunition and this does.
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    public enum MeshCarriedActionKind : byte { None = 0, Attack = 1, Skill = 2, Charge = 3 }

    public struct MeshCarriedAction : INetSerializable
    {
        public MeshCarriedActionKind kind;
        public int seed;
        public byte weaponHandlingState;
        public float[] triggerDurations;
        public byte fireSpread;
        public int skillDataId;  // skills: the skill, its level, and the usable item that cast it (0 for none)
        public int skillLevel;
        public int itemDataId;

        public bool IsSet => kind == MeshCarriedActionKind.Charge || (kind != MeshCarriedActionKind.None && triggerDurations != null && triggerDurations.Length > 0);

        /// <summary>TriggersAfter is how many of the action's triggers come after the first <paramref name="paid"/>: what the origin still owes for.</summary>
        public int TriggersAfter(int paid)
        {
            return System.Math.Max(0, (triggerDurations?.Length ?? 0) - paid);
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)kind);
            if (kind == MeshCarriedActionKind.None)
                return;
            writer.Put(seed);
            writer.Put(weaponHandlingState);
            writer.PutArray(triggerDurations ?? new float[0]);
            writer.Put(fireSpread);
            writer.Put(skillDataId);
            writer.Put(skillLevel);
            writer.Put(itemDataId);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = (MeshCarriedActionKind)reader.GetByte();
            if (kind == MeshCarriedActionKind.None)
                return;
            seed = reader.GetInt();
            weaponHandlingState = reader.GetByte();
            triggerDurations = reader.GetFloatArray();
            fireSpread = reader.GetByte();
            skillDataId = reader.GetInt();
            skillLevel = reader.GetInt();
            itemDataId = reader.GetInt();
        }

        public override string ToString() => kind == MeshCarriedActionKind.None ? "none"
            : $"{kind} seed={seed} triggers={triggerDurations?.Length ?? 0}{(kind == MeshCarriedActionKind.Skill ? $" skill={skillDataId} lv{skillLevel}{(itemDataId != 0 ? $" item={itemDataId}" : string.Empty)}" : string.Empty)}";
    }
}
