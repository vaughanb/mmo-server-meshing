using LiteNetLib.Utils;
using System.Collections.Generic;

namespace MultiplayerARPG
{
    public interface IBaseGridManagerComponenent
    {
        bool IsDisabled { get; }
        ushort CellSize { get; }
        CompressionRange CompressionRange { get; }

        GridData GridData { get; }
        void SetupDynamicGrid();

        bool WriteEntityServerState(NetDataWriter writer, MovementResult movementResult, List<EntityMovementForceApplier> forceAppliers);
    }
}
