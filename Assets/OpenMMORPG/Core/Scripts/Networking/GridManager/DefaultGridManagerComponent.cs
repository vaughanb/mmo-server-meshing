using LiteNetLib.Utils;
using LiteNetLibManager;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    public class DefaultGridManagerComponent : MonoBehaviour, IBaseGridManagerComponenent
    {

        public static IBaseGridManagerComponenent Instance { get; private set; }

        // When enabled, the server sends entity positions as quantized cells (QuantizedPosition) instead of
        // raw floats. Cells start at the world origin and have no edge, so any map size, negative coordinates
        // and any height work. Movement types whose client can't read that format keep their own writes.
        // Servers and clients must agree on every field here, or clients misread every state. This component
        // is added at runtime via GetOrAddComponent and is not serialized in any scene or prefab, so these
        // defaults apply identically to all servers and clients.
        [SerializeField]
        private bool isDisabled = false;
        public bool IsDisabled => isDisabled;

        [SerializeField]
        [Tooltip("Edge of each cubic cell, in metres. Smaller cells give finer steps for the same bits.")]
        private ushort cellSize = 128;
        public ushort CellSize => cellSize;

        [SerializeField]
        private CompressionRange compressionRange = CompressionRange.Default;

        public CompressionRange CompressionRange
        {
            get { return compressionRange; }
        }

        private GridData gridData;

        public GridData GridData
        {
            get { return gridData; }
        }

        public void SetupDynamicGrid()
        {
            Instance = this;
            gridData = new GridData(cellSize, compressionRange);

            if (IsDisabled)
                return;

            Logging.Log($"[DefaultGridManagerComponent] Quantized positions enabled with {cellSize} m cells.");
        }

        public bool WriteEntityServerState(NetDataWriter writer, MovementResult movementResult, List<EntityMovementForceApplier> forceAppliers)
        {
            if (IsDisabled || writer == null)
                return false;

            // Must mirror the quantized branch of EntityMovementFunctions.ClientReadSyncTransformMessage3D
            writer.PutPackedUInt(movementResult.movementState);
            writer.Put(movementResult.extraMovementState);
            writer.PutQuantizedPosition(movementResult.position);
            writer.Put(movementResult.compressedYAngle);
            writer.PutList(forceAppliers);

            return true;
        }
    }

    /// <summary>
    /// Distances, in metres on the ground plane from the receiving player, that move an entity between
    /// compression modes. Each step up and down uses a different distance, so an entity near one doesn't
    /// flip modes every tick.
    /// </summary>
    [Serializable]
    public struct CompressionRange
    {
        [Tooltip("A mode 5 entity closer than this moves up to mode 6, the finest.")]
        public float HighestPrecisionRange;
        [Tooltip("A mode 6 entity farther than this drops to mode 5. A mode 4 entity closer than this moves up to mode 5.")]
        public float HighPrecisionRange;
        [Tooltip("A mode 5 entity farther than this drops to mode 4. A mode 3 entity closer than this moves up to mode 4.")]
        public float MediumPrecisionRange;
        [Tooltip("A mode 4 entity farther than this drops to mode 3, the coarsest.")]
        public float LowPrecisionRange;

        /// <summary>
        /// The ranges every server and client uses. All zero would put every entity in mode 3 at every distance.
        /// </summary>
        public static CompressionRange Default => new CompressionRange
        {
            HighestPrecisionRange = 8f,
            HighPrecisionRange = 16f,
            MediumPrecisionRange = 40f,
            LowPrecisionRange = 64f,
        };
    }

    public static class GridUtility
    {
        public static byte CompressAngle(float angle)
        {
            // normalize to 0–360
            angle %= 360f;
            if (angle < 0) angle += 360f;

            return (byte)(angle * (255f / 360f));
        }

        public static int GetCompressionMode(float distSq, int previousMode, CompressionRange compressionRange)
        {
            switch (previousMode)
            {
                case 6:
                    if (distSq > compressionRange.HighPrecisionRange * compressionRange.HighPrecisionRange) return 5;
                    return 6;

                case 5:
                    if (distSq < compressionRange.HighestPrecisionRange * compressionRange.HighestPrecisionRange) return 6;
                    if (distSq > compressionRange.MediumPrecisionRange * compressionRange.MediumPrecisionRange) return 4;
                    return 5;

                case 4:
                    if (distSq < compressionRange.HighPrecisionRange * compressionRange.HighPrecisionRange) return 5;
                    if (distSq > compressionRange.LowPrecisionRange * compressionRange.LowPrecisionRange) return 3;
                    return 4;

                default:
                    if (distSq < compressionRange.MediumPrecisionRange * compressionRange.MediumPrecisionRange) return 4;
                    return 3;
            }
        }
    }
}
