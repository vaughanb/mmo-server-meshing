// Server Meshing add-on — rig phantom slot: one phantom index's state, shared by the binder, the feeder and the fighter.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>ServerMeshPhantomSlot is one phantom index's state, shared by the binder, the feeder and the fighter.</summary>
    internal sealed class ServerMeshPhantomSlot
    {
        public int index;
        public long connectionId = -1;
        public bool connecting;
        public bool bound;
        public bool sink;
        public BasePlayerCharacterEntity entity;
        public LiteNetLibIdentity identity;
        public ServerMeshPhantomEye eye;
        public ServerMeshShipEntity ship;
        public int flightPath = -1;
        public ServerMeshShipFlightPath shipPath;
        public byte shipSequence;
        public uint objectId;
        public Vector2 spawn;
        public Rect region;
        public int group;
        public bool standing;
        public bool fighting;
        public bool chasing;
        public BaseCharacterEntity chaseTarget;
        public double nextChaseLook;
        public bool input;
        public Vector3 walkPosition;
        public Vector2 heading = Vector2.up;
        public double nextTurn;
        public System.Random random;
        public double nextDue;
        public long lastTimestamp;
        public bool stopSent;
        public bool teleportConfirmed;
        public Vector3 teleportDestination;
        public bool teleportPending;
        public bool outsideWarned;
        public Vector3 lastQueuedPosition;
        public int lastQueuedFrame = -1;
        public long lastDownBytes;

        /// <summary>FedHere is true for a bound virtual character phantom: the feeder walks and uploads for it.</summary>
        public bool FedHere => bound && !sink && entity != null;

        /// <summary>ShipFedHere is true for a bound virtual ship phantom: the feeder uploads its flight path.</summary>
        public bool ShipFedHere => bound && !sink && ship != null;

        public void Release()
        {
            connectionId = -1;
            connecting = false;
            bound = false;
            sink = false;
            entity = null;
            identity = null;
            eye = null;
            ship = null;
            flightPath = -1;
            shipPath = null;
            shipSequence = 0;
            objectId = 0;
            lastTimestamp = 0;
            stopSent = false;
            teleportConfirmed = false;
            teleportPending = false;
            outsideWarned = false;
            lastQueuedFrame = -1;
            lastDownBytes = 0;
        }
    }
}
