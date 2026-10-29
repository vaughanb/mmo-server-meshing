// Server Meshing test rig — the kit demo's CombatGameplayRule with the add-on's kill credit (MeshGameplayRule's P3
// forwarding: a reward given to an attacker's ghost goes raw to its owner cell). MeshGameplayRule derives from
// SimpleGameplayRule, so the demo copy (ServerMeshDemo8kBuilder) would lose the demo's combat rules on it; this keeps both.
//
// It also carries the demo's combat timer across a hop (DESIGN 1.22, IMeshHandoffCarrier): the demo rule keeps when each
// character last dealt or took a blow, by object id, on each cell, and drains rage and holds HP regeneration from it.
// The origin sends how long ago the last blow was, and the destination sets its own entry that long ago, so a warrior
// who crosses mid-fight is still in combat. The kit demo's table is private; it is read by reflection rather than
// changing the kit's copy.
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    [CreateAssetMenu(fileName = "MeshCombatGameplayRule", menuName = "Open MMORPG/Server Meshing/Mesh Combat Gameplay Rule")]
    public class MeshCombatGameplayRule : CombatGameplayRule, IMeshHandoffCarrier
    {
        private static readonly FieldInfo s_lastCombatTime = typeof(CombatGameplayRule).GetField("_lastCombatTime", BindingFlags.Instance | BindingFlags.NonPublic);

        private Dictionary<uint, float> LastCombatTime => s_lastCombatTime?.GetValue(this) as Dictionary<uint, float>;

        /// <summary>CarrierKey names the combat timer's bytes in a handoff.</summary>
        public string CarrierKey => "demo.combat";

        /// <summary>Origin: how many seconds ago <paramref name="player"/> last dealt or took a blow, while that still counts as in combat.</summary>
        public bool WriteHandoff(BasePlayerCharacterEntity player, NetDataWriter writer)
        {
            Dictionary<uint, float> table = LastCombatTime;
            if (table == null || player == null || !table.TryGetValue(player.ObjectId, out float last))
                return false;
            float ago = Time.unscaledTime - last;
            if (!(ago >= 0f) || ago >= combatSeconds)
                return false;
            writer.Put(ago);
            return true;
        }

        /// <summary>Destination: <paramref name="player"/>'s last blow was that long ago here too.</summary>
        public void ReadHandoff(BasePlayerCharacterEntity player, NetDataReader reader)
        {
            float ago = reader.GetFloat();
            Dictionary<uint, float> table = LastCombatTime;
            if (table == null || player == null || !(ago >= 0f) || ago >= combatSeconds)
                return;
            table[player.ObjectId] = Time.unscaledTime - ago;
        }

        public override bool RewardExp(BaseCharacterEntity character, int exp, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel, out int rewardedExp)
        {
            if (TryForward(character, exp, 0, null, multiplier, rewardGivenType, giverLevel, sourceLevel))
            {
                rewardedExp = 0;
                return false;
            }
            return base.RewardExp(character, exp, multiplier, rewardGivenType, giverLevel, sourceLevel, out rewardedExp);
        }

        public override void RewardGold(BaseCharacterEntity character, int gold, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel, out int rewardedGold)
        {
            if (TryForward(character, 0, gold, null, multiplier, rewardGivenType, giverLevel, sourceLevel))
            {
                rewardedGold = 0;
                return;
            }
            base.RewardGold(character, gold, multiplier, rewardGivenType, giverLevel, sourceLevel, out rewardedGold);
        }

        public override void RewardCurrencies(BaseCharacterEntity character, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel)
        {
            if (TryForward(character, 0, 0, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel))
                return;
            base.RewardCurrencies(character, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel);
        }

        private static bool TryForward(BaseCharacterEntity character, int exp, int gold, IEnumerable<CurrencyAmount> currencies, float multiplier, RewardGivenType rewardGivenType, int giverLevel, int sourceLevel)
        {
            BasePlayerCharacterEntity player = character as BasePlayerCharacterEntity;
            if (player == null || !MeshIdentity.IsGhostEntity(player))
                return false;
            MeshMapNetworkManager manager = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (manager == null)
                return false;
            manager.ForwardGhostReward(player, exp, gold, currencies, multiplier, rewardGivenType, giverLevel, sourceLevel);
            return true;
        }
    }
}
