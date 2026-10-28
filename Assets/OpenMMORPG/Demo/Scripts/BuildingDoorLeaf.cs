using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Swings the leaf of a door a player has built.
    ///
    /// The village's doors (<see cref="SceneryDoor"/>) are presentation only: each client opens
    /// its own copy and it shuts itself a few seconds later. A built door cannot work that
    /// way, because it is the kit's <see cref="DoorEntity"/> - it can be locked with a
    /// password, and whether it is open is a synced field the server owns. So this does not
    /// decide anything. It only listens: the entity's `onOpen`/`onClose` events swing the
    /// leaf, and `onInitialOpen`/`onInitialClose` - fired once when a player arrives and the
    /// door is already in some state - set it there without the swing. The swing, its sounds
    /// and the leaf's collision are <see cref="DoorSwing"/>'s, which ticks only while the leaf
    /// moves.
    ///
    /// Wired as persistent listeners on the prefab by DemoHomesteadBuilder, so there is no
    /// code path from the entity to this; the four methods take no argument so the events
    /// can call them directly.
    /// </summary>
    [DisallowMultipleComponent]
    public class BuildingDoorLeaf : DoorSwing
    {
        public void Open()
        {
            SwingTo(openAngle);
        }

        public void Close()
        {
            SwingTo(0f);
        }

        public void OpenAtOnce()
        {
            SetAtOnce(openAngle);
        }

        public void CloseAtOnce()
        {
            SetAtOnce(0f);
        }
    }
}
