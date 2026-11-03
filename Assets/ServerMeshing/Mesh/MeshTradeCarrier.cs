// Server Meshing add-on — a trader's answers on exchanges across a line cross a hop with it (DESIGN.md 1.49).
//
// The cell of the trader who did the second half of an exchange across a line keeps its answer by trade id (committed,
// with what it gave, or failed), so that the escrow's cell, asking again, gets the same answer
// (MeshMapNetworkManager_Social). The answer belongs to the trader: when the trader hops after the exchange, a question
// goes to its new cell, which must know it. This carrier, built in for every player (MeshHandoffCarriers.BuiltIn), takes
// the departing trader's recent answers along.
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed class MeshTradeCarrier : IMeshHandoffCarrier
    {
        public const string Key = "mesh.trade";

        public string CarrierKey => Key;

        private static MeshMapNetworkManager Mesh => BaseGameNetworkManager.Singleton as MeshMapNetworkManager;

        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            List<MeshMapNetworkManager.DealDecision> decisions = player != null && Mesh != null ? Mesh.DealDecisionsOf(player.Id) : null;
            if (decisions == null)
                return false;
            writer.Put(decisions.Count);
            foreach (MeshMapNetworkManager.DealDecision d in decisions)
            {
                writer.Put(d.tradeId ?? string.Empty);
                writer.Put(d.otherId ?? string.Empty);
                writer.Put(d.otherZone ?? string.Empty);
                writer.Put(d.committed);
                writer.Put(d.reason);
                writer.Put(d.taken.Count);
                for (int i = 0; i < d.taken.Count; ++i)
                    writer.Put(d.taken[i]);
                writer.Put(d.goldGiven);
                writer.Put(Time.unscaledTime - d.at);
            }
            return true;
        }

        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            int count = reader.GetInt();
            for (int i = 0; i < count; ++i)
            {
                MeshMapNetworkManager.DealDecision d = new MeshMapNetworkManager.DealDecision
                {
                    tradeId = reader.GetString(),
                    otherId = reader.GetString(),
                    otherZone = reader.GetString(),
                    committed = reader.GetBool(),
                    reason = reader.GetUShort(),
                };
                int taken = reader.GetInt();
                for (int t = 0; t < taken; ++t)
                    d.taken.Add(reader.Get<CharacterItem>());
                d.goldGiven = reader.GetInt();
                float age = reader.GetFloat();
                Mesh?.ImportDealDecision(player.Id, d, age);
            }
            if (count > 0)
                Logging.Log("Mesh", $"Trade: {player.CharacterName}'s answers on {count} recent exchange(s) carried with the handoff");
        }
    }
}
