using Unity.Collections;
using Unity.Jobs;

namespace MultiplayerARPG
{
    public struct MovementJob : IJob
    {
        public NativeHashMap<uint, byte> previousModes;
        [ReadOnly] public GridData gridData;
        [ReadOnly] public NativeArray<uint> objectIds;
        [ReadOnly] public NativeHashMap<uint, MovementData> entityData;
        [ReadOnly] public UnityEngine.Vector3 receivingPlayerPosition;

        // output results to be sent to clients after processing all entities in the job.
        public NativeList<MovementResult> movementResults;
        public NativeHashMap<uint, byte> ModesResults;

        public void Execute()
        {
            for (int i = 0; i < objectIds.Length; i++)
            {
                uint objectId = objectIds[i];

                if (!entityData.TryGetValue(objectId, out var entity))
                    continue;

                //Calculate distance to player to determine compression mode.
                float dx = receivingPlayerPosition.x - entity.worldPosition.x;
                float dz = receivingPlayerPosition.z - entity.worldPosition.z;
                float distSq = dx * dx + dz * dz;

                // Fewer bits for far entities to save bandwidth, more for near ones so they stay accurate
                int previousMode = previousModes.ContainsKey(objectId) ? previousModes[objectId] : 0;
                int compressionMode = GridUtility.GetCompressionMode(distSq, previousMode, gridData.range);
                ModesResults[objectId] = (byte)compressionMode;

                movementResults.Add(new MovementResult
                {
                    objectId = objectId,
                    reliably = entity.shouldSendReliably,
                    movementState = entity.movementState,
                    extraMovementState = entity.extraMovementState,
                    position = QuantizedPosition.Encode(entity.worldPosition.x, entity.worldPosition.y, entity.worldPosition.z, gridData.cellSize, compressionMode),
                    compressedYAngle = GridUtility.CompressAngle(entity.yAngle)
                });
            }
        }
    }
}
