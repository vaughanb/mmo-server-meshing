// Server Meshing add-on — game state a cell holds about a player outside the kit's character data, carried across a
// hop (DESIGN.md 1.22).
//
// The handoff sends the character as the kit saves it (PlayerCharacterData), and what 1.06-1.09 add. Game code can hold
// more about a player on its cell: the demo's combat rule keeps when each character last dealt or took a blow, by
// object id, and drains a warrior's rage and holds back its HP regeneration from that. The destination had none of it,
// so a warrior who crossed mid-fight was out of combat there at once: its rage drained (20 -> 16 in the second after a
// hop, while it was still swinging) and its HP would have regenerated. Anyone watching its bars saw the line.
//
// A gameplay rule, or any component on the player's entity, that implements IMeshHandoffCarrier writes what it needs
// into the handoff state on the origin, and reads it back on the destination when the arrival registers (its object id
// is the destination's then). Each carrier's bytes are kept apart, under its key, so one that misreads cannot shift
// another's, and a key the destination has no carrier for is skipped.
using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// IMeshHandoffCarrier is game state about a player that crosses a hop with it. Implement it on the gameplay rule or on a
    /// component of the player's entity. Both calls run on the server's main thread.
    /// </summary>
    public interface IMeshHandoffCarrier
    {
        /// <summary>CarrierKey names this carrier's bytes; the same carrier on the destination reads them. Unique per carrier.</summary>
        string CarrierKey { get; }

        /// <summary>Origin, as the handoff is sent: write what <paramref name="player"/> needs carried; false when there is nothing.</summary>
        bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer);

        /// <summary>Destination, as the arrival registers: read back what the origin wrote for <paramref name="player"/>.</summary>
        void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader);
    }

    /// <summary>MeshHandoffCarriers collects the carriers' bytes for a handoff and gives them back on arrival.</summary>
    public static class MeshHandoffCarriers
    {
        private static readonly List<IMeshHandoffCarrier> s_found = new List<IMeshHandoffCarrier>();

        /// <summary>The carriers the add-on brings for every player (DESIGN 1.37: the NPC dialog and storages a player has open).</summary>
        public static readonly List<IMeshHandoffCarrier> BuiltIn = new List<IMeshHandoffCarrier> { new MeshInteractionCarrier() };

        /// <summary>The built-in carriers, the gameplay rule if it carries, then the player's carrying components.</summary>
        private static List<IMeshHandoffCarrier> CarriersOf(BasePlayerCharacterEntity player)
        {
            s_found.Clear();
            s_found.AddRange(BuiltIn);
            if (GameInstance.Singleton != null && GameInstance.Singleton.GameplayRule is IMeshHandoffCarrier rule)
                s_found.Add(rule);
            if (player != null)
            {
                foreach (MonoBehaviour behaviour in player.GetComponents<MonoBehaviour>())
                {
                    if (behaviour is IMeshHandoffCarrier carrier)
                        s_found.Add(carrier);
                }
            }
            return s_found;
        }

        /// <summary>
        /// Collect is every carrier's bytes for <paramref name="player"/>: a count, then per carrier its key and its bytes;
        /// null when none had anything. A carrier that throws is left out and reported through <paramref name="warn"/>.
        /// </summary>
        public static byte[] Collect(BasePlayerCharacterEntity player, Action<string> warn)
        {
            return Collect(CarriersOf(player), player, warn);
        }

        /// <summary>Collect with the carriers given (for tests: <paramref name="player"/> may be null).</summary>
        public static byte[] Collect(IList<IMeshHandoffCarrier> carriers, BasePlayerCharacterEntity player, Action<string> warn)
        {
            if (carriers == null || carriers.Count == 0)
                return null;
            NetDataWriter body = new NetDataWriter();
            NetDataWriter one = new NetDataWriter();
            int count = 0;
            foreach (IMeshHandoffCarrier carrier in carriers)
            {
                one.Reset();
                try
                {
                    if (!carrier.WriteHandoff(player, one))
                        continue;
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"handoff carrier {carrier.CarrierKey} threw writing {(player != null ? player.CharacterName : "?")}: {ex.Message}; not carried");
                    continue;
                }
                body.Put(carrier.CarrierKey ?? string.Empty);
                body.PutBytesWithLength(one.CopyData());
                count++;
            }
            if (count == 0)
                return null;
            NetDataWriter writer = new NetDataWriter();
            writer.Put(count);
            writer.Put(body.Data, 0, body.Length);
            return writer.CopyData();
        }

        /// <summary>
        /// Apply gives each carrier on the destination its bytes from <paramref name="carried"/>; returns the keys read. A key
        /// with no carrier here is skipped, and a carrier that throws is reported through <paramref name="warn"/>.
        /// </summary>
        public static string Apply(BasePlayerCharacterEntity player, byte[] carried, Action<string> warn)
        {
            if (player == null)
                return string.Empty;
            return Apply(CarriersOf(player), player, carried, warn);
        }

        /// <summary>Apply with the carriers given (for tests: <paramref name="player"/> may be null).</summary>
        public static string Apply(IList<IMeshHandoffCarrier> carriers, BasePlayerCharacterEntity player, byte[] carried, Action<string> warn)
        {
            if (carried == null || carried.Length == 0 || carriers == null)
                return string.Empty;
            NetDataReader reader = new NetDataReader(carried);
            int count = reader.GetInt();
            List<string> read = new List<string>();
            for (int i = 0; i < count && reader.AvailableBytes > 0; ++i)
            {
                string key = reader.GetString();
                byte[] bytes = reader.GetBytesWithLength();
                IMeshHandoffCarrier carrier = null;
                foreach (IMeshHandoffCarrier candidate in carriers)
                {
                    if (candidate.CarrierKey == key)
                    {
                        carrier = candidate;
                        break;
                    }
                }
                if (carrier == null)
                    continue;
                try
                {
                    carrier.ReadHandoff(player, new NetDataReader(bytes));
                    read.Add(key);
                }
                catch (Exception ex)
                {
                    warn?.Invoke($"handoff carrier {key} threw reading {(player != null ? player.CharacterName : "?")}: {ex.Message}");
                }
            }
            return string.Join(",", read);
        }
    }
}
