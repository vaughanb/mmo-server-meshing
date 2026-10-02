// Server Meshing add-on — a movement-handler layer that gives way when another Mesh layer takes its object's slot.
namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// IMeshReplaceableHandlerLayer is a Mesh movement-handler layer installed as replaceable: MeshMovementHandlerSlots may
    /// install another layer over it, and then tells it so once through OnReplaced. It must also be the
    /// IEntityMovementDataHandler it was installed as.
    /// </summary>
    public interface IMeshReplaceableHandlerLayer
    {
        /// <summary>
        /// OnReplaced is called once, after another layer took this layer's slot; the slot no longer calls this layer. It
        /// may install itself again later (for example when MeshMovementHandlerSlots raises LayerRemoved for its object).
        /// </summary>
        void OnReplaced();
    }
}
