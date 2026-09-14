// Server Meshing add-on — MeshMonsterCharacterEntity (DESIGN.md §12, actions across the edge).
// A puppet monster must not take buffs/debuffs locally: the kit's ApplyBuff on a ghost is forwarded to the owner.
// Add-on only: the demo enemy prefab uses this class instead of MonsterCharacterEntity.
namespace MultiplayerARPG.MMO.Mesh
{
    public class MeshMonsterCharacterEntity : MonsterCharacterEntity
    {
        public override void ApplyBuff(int dataId, BuffType type, int level, EntityInfo buffApplier, CharacterItem buffApplierWeapon)
        {
            if (IsServer && MeshIdentity.IsGhostEntity(this))
            {
                MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
                if (mesh != null && mesh.ForwardGhostAction(this, GhostActionKind.ApplyBuff, buffApplier,
                        writer => MeshMapNetworkManager.WriteApplyBuffPayload(writer, dataId, type, level, buffApplierWeapon)))
                    return;
            }
            base.ApplyBuff(dataId, type, level, buffApplier, buffApplierWeapon);
        }
    }
}
