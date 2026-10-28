using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The network half of a <see cref="SceneryDoor"/>: whether it is open, as the server has it,
    /// synced to every client in range. The door's leaf, sound and collision follow it on every
    /// copy, the server's included (see <see cref="SceneryDoor"/>).
    ///
    /// **On a child of the doorway, with its own <see cref="LiteNetLibIdentity"/>, never on the
    /// doorway itself.** LiteNetLib switches a scene object off on a client until the server has
    /// spawned it to them, and off again whenever they move out of its range. On the doorway that
    /// would take the hinge and the leaf with it - a house seen from a hill would have a hole
    /// where its door is. Here only this empty object comes and goes; the door keeps the pose it
    /// last had, and is put right the moment the player is back in range and the state arrives.
    ///
    /// The identity is a scene object, so its `sceneObjectId` must be unique and the same in the
    /// server's build and the client's: the demo pins `Door@&lt;doorway path&gt;`
    /// (<c>DemoVillageBuilder.NetworkDoors</c>).
    /// </summary>
    [DisallowMultipleComponent]
    public class SceneryDoorSync : LiteNetLibBehaviour
    {
        [SerializeField]
        protected SyncFieldBool isOpen = new SyncFieldBool();

        private SceneryDoor _door;

        /// <summary>The door this is the network half of: the one above it.</summary>
        public SceneryDoor Door
        {
            get
            {
                if (_door == null)
                    _door = GetComponentInParent<SceneryDoor>(true);
                return _door;
            }
        }

        public override void OnSetup()
        {
            base.OnSetup();
            isOpen.syncMode = LiteNetLibSyncFieldMode.ServerToClients;
            isOpen.onChange -= OnIsOpenChange;
            isOpen.onChange += OnIsOpenChange;
        }

        private void OnDestroy()
        {
            isOpen.onChange -= OnIsOpenChange;
        }

        private void OnIsOpenChange(bool initial, bool wasOpen, bool open)
        {
            if (Door != null)
                Door.ShowState(open, initial);
        }

        /// <summary>Opens or shuts the door for everyone. Server only.</summary>
        public void ServerSetOpen(bool open)
        {
            if (IsServer)
                isOpen.Value = open;
        }
    }
}
