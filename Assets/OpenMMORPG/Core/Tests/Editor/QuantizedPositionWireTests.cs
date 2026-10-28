using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MultiplayerARPG.Tests
{
    /// <summary>
    /// The quantized server state on the wire: the position helpers, the grid manager's write against the
    /// client's read, the movement job, and the compression mode ranges.
    /// </summary>
    public class QuantizedPositionWireTests
    {
        private const ushort CellSize = 128;

        private static readonly int[] s_modes = { 3, 4, 5, 6 };
        private static IEnumerable<int> Modes => s_modes;

        private static readonly PropertyInfo s_instanceProperty = typeof(DefaultGridManagerComponent).GetProperty(nameof(DefaultGridManagerComponent.Instance), BindingFlags.Public | BindingFlags.Static);
        private static readonly FieldInfo s_isDisabledField = typeof(DefaultGridManagerComponent).GetField("isDisabled", BindingFlags.NonPublic | BindingFlags.Instance);

        private IBaseGridManagerComponenent _previousInstance;
        private GameObject _gridManagerObject;

        [SetUp]
        public void SetUp()
        {
            _previousInstance = DefaultGridManagerComponent.Instance;
        }

        [TearDown]
        public void TearDown()
        {
            s_instanceProperty.SetValue(null, _previousInstance);
            if (_gridManagerObject != null)
                Object.DestroyImmediate(_gridManagerObject);
        }

        private DefaultGridManagerComponent CreateGridManager(bool enabled)
        {
            _gridManagerObject = new GameObject(nameof(QuantizedPositionWireTests));
            DefaultGridManagerComponent gridManager = _gridManagerObject.AddComponent<DefaultGridManagerComponent>();
            // Set up while disabled, then enable: an enabled setup logs, and the kit's logger isn't
            // initialized in edit mode tests. Setup sets the grid data and the instance either way.
            s_isDisabledField.SetValue(gridManager, true);
            gridManager.SetupDynamicGrid();
            s_isDisabledField.SetValue(gridManager, !enabled);
            return gridManager;
        }

        private static NetDataReader Reader(NetDataWriter writer) => new NetDataReader(writer.CopyData());

        [Test]
        public void Position_RoundTripsThroughTheWire_AndReadsExactlyItsBytes([ValueSource(nameof(Modes))] int mode)
        {
            Vector3[] positions =
            {
                Vector3.zero,
                new Vector3(-1000f, -50f, 5000f),
                new Vector3(959.9f, 127.9f, -960.1f),
                new Vector3(-20000.5f, 3000f, 20000.5f),
            };
            QuantizedPosition.GetBits(mode, out int xzBits, out int yBits);
            foreach (Vector3 position in positions)
            {
                EncodedPosition encoded = QuantizedPosition.Encode(position.x, position.y, position.z, CellSize, mode);
                NetDataWriter writer = new NetDataWriter();
                writer.PutQuantizedPosition(encoded);
                NetDataReader reader = Reader(writer);
                Vector3 decoded = reader.GetQuantizedPosition(CellSize);
                Assert.IsTrue(reader.EndOfData, $"mode {mode} at {position} left bytes unread");
                Assert.AreEqual(position.x, decoded.x, QuantizedPosition.GetStep(CellSize, xzBits) * 0.5f + Mathf.Abs(position.x) * 1.2e-7f + 1e-6f);
                Assert.AreEqual(position.y, decoded.y, QuantizedPosition.GetStep(CellSize, yBits) * 0.5f + Mathf.Abs(position.y) * 1.2e-7f + 1e-6f);
                Assert.AreEqual(position.z, decoded.z, QuantizedPosition.GetStep(CellSize, xzBits) * 0.5f + Mathf.Abs(position.z) * 1.2e-7f + 1e-6f);
            }
        }

        [Test]
        public void Position_NearTheOrigin_TakesSevenBytesInModeThree()
        {
            EncodedPosition encoded = QuantizedPosition.Encode(-500f, 10f, 500f, CellSize, 3);
            NetDataWriter writer = new NetDataWriter();
            writer.PutQuantizedPosition(encoded);
            // Three 1-byte cells and 4 bytes of offsets, against 12 for three floats
            Assert.AreEqual(7, writer.Length);
        }

        [Test]
        public void ServerState_RoundTripsFromGridManagerToClientRead()
        {
            DefaultGridManagerComponent gridManager = CreateGridManager(true);
            Vector3 position = new Vector3(-1234.5f, -12.25f, 2345.75f);
            MovementResult movementResult = new MovementResult
            {
                objectId = 42,
                reliably = false,
                movementState = (uint)(MovementState.Forward | MovementState.IsGrounded),
                extraMovementState = (byte)ExtraMovementState.IsSprinting,
                position = QuantizedPosition.Encode(position.x, position.y, position.z, CellSize, 6),
                compressedYAngle = GridUtility.CompressAngle(90f),
            };

            NetDataWriter writer = new NetDataWriter();
            Assert.IsTrue(gridManager.WriteEntityServerState(writer, movementResult, new List<EntityMovementForceApplier>()));

            NetDataReader reader = Reader(writer);
            reader.ClientReadSyncTransformMessage3D(out MovementState movementState, out ExtraMovementState extraMovementState, out Vector3 readPosition, out float yAngle, out List<EntityMovementForceApplier> forceAppliers);

            Assert.IsTrue(reader.EndOfData);
            Assert.AreEqual(MovementState.Forward | MovementState.IsGrounded, movementState);
            Assert.AreEqual(ExtraMovementState.IsSprinting, extraMovementState);
            Assert.AreEqual(position.x, readPosition.x, 0.002f);
            Assert.AreEqual(position.y, readPosition.y, 0.002f);
            Assert.AreEqual(position.z, readPosition.z, 0.002f);
            Assert.AreEqual(90f, yAngle, 360f / 255f);
            Assert.AreEqual(0, forceAppliers.Count);
        }

        [Test]
        public void ServerState_IsNotWritten_WhenQuantizationIsDisabled()
        {
            DefaultGridManagerComponent gridManager = CreateGridManager(false);
            NetDataWriter writer = new NetDataWriter();
            Assert.IsFalse(gridManager.WriteEntityServerState(writer, new MovementResult(), new List<EntityMovementForceApplier>()));
            Assert.AreEqual(0, writer.Length);
        }

        [Test]
        public void GridManager_UsesTheDefaultRanges_WhenAddedAtRuntime()
        {
            // The kit adds the grid manager at runtime, so its inspector fields are never set. All-zero
            // ranges put every entity in mode 3 at every distance.
            DefaultGridManagerComponent gridManager = CreateGridManager(true);
            CompressionRange range = gridManager.GridData.range;
            Assert.AreEqual(CompressionRange.Default.HighestPrecisionRange, range.HighestPrecisionRange);
            Assert.AreEqual(CompressionRange.Default.LowPrecisionRange, range.LowPrecisionRange);
            Assert.AreEqual(CellSize, gridManager.GridData.cellSize);
        }

        [Test]
        public void DefaultRanges_AreOrderedFromFinestToCoarsest()
        {
            CompressionRange range = CompressionRange.Default;
            Assert.Greater(range.HighestPrecisionRange, 0f);
            Assert.Less(range.HighestPrecisionRange, range.HighPrecisionRange);
            Assert.Less(range.HighPrecisionRange, range.MediumPrecisionRange);
            Assert.Less(range.MediumPrecisionRange, range.LowPrecisionRange);
        }

        private static int SettleMode(float distance, int startMode)
        {
            int mode = startMode;
            for (int i = 0; i < 10; ++i)
                mode = GridUtility.GetCompressionMode(distance * distance, mode, CompressionRange.Default);
            return mode;
        }

        [Test]
        public void CompressionMode_SettlesByDistance_WithTheDefaultRanges()
        {
            Assert.AreEqual(6, SettleMode(2f, 0));
            Assert.AreEqual(5, SettleMode(12f, 0));
            Assert.AreEqual(4, SettleMode(30f, 0));
            Assert.AreEqual(3, SettleMode(100f, 0));
            Assert.AreEqual(3, SettleMode(100f, 6));
            Assert.AreEqual(6, SettleMode(2f, 3));
        }

        [Test]
        public void CompressionMode_HoldsBetweenItsEnterAndLeaveDistances()
        {
            CompressionRange range = CompressionRange.Default;
            float between = (range.HighestPrecisionRange + range.HighPrecisionRange) * 0.5f;
            // Mode 6 stays until past the high range, mode 5 waits until inside the highest range
            Assert.AreEqual(6, GridUtility.GetCompressionMode(between * between, 6, range));
            Assert.AreEqual(5, GridUtility.GetCompressionMode(between * between, 5, range));
        }

        [Test]
        public void MovementJob_EncodesEachEntity_WithItsDistanceMode()
        {
            NativeHashMap<uint, byte> previousModes = new NativeHashMap<uint, byte>(4, Allocator.TempJob);
            NativeArray<uint> objectIds = new NativeArray<uint>(new uint[] { 1, 2, 3 }, Allocator.TempJob);
            NativeHashMap<uint, MovementData> entityData = new NativeHashMap<uint, MovementData>(4, Allocator.TempJob);
            NativeList<MovementResult> movementResults = new NativeList<MovementResult>(4, Allocator.TempJob);
            NativeHashMap<uint, byte> modesResults = new NativeHashMap<uint, byte>(4, Allocator.TempJob);
            try
            {
                Vector3 receiver = new Vector3(-3000f, 0f, -3000f);
                Vector3 near = receiver + new Vector3(3f, -20f, 0f);
                Vector3 far = receiver + new Vector3(500f, 300f, 500f);
                entityData[1] = new MovementData { worldPosition = near, yAngle = 45f, movementState = 1u };
                entityData[2] = new MovementData { worldPosition = far, yAngle = 180f, shouldSendReliably = true };
                previousModes[1] = 5;
                // Object 3 has no data and is skipped

                new MovementJob
                {
                    previousModes = previousModes,
                    gridData = new GridData(CellSize, CompressionRange.Default),
                    objectIds = objectIds,
                    entityData = entityData,
                    receivingPlayerPosition = receiver,
                    movementResults = movementResults,
                    ModesResults = modesResults,
                }.Schedule().Complete();

                Assert.AreEqual(2, movementResults.Length);
                Assert.AreEqual(6, modesResults[1]);
                Assert.AreEqual(3, modesResults[2]);

                MovementResult nearResult = movementResults[0];
                Assert.AreEqual(1u, nearResult.objectId);
                Assert.AreEqual(6, nearResult.position.mode);
                QuantizedPosition.Decode(nearResult.position, CellSize, out float x, out float y, out float z);
                Assert.AreEqual(near.x, x, 0.002f);
                Assert.AreEqual(near.y, y, 0.002f);
                Assert.AreEqual(near.z, z, 0.002f);

                MovementResult farResult = movementResults[1];
                Assert.AreEqual(2u, farResult.objectId);
                Assert.IsTrue(farResult.reliably);
                Assert.AreEqual(3, farResult.position.mode);
                QuantizedPosition.Decode(farResult.position, CellSize, out x, out y, out z);
                Assert.AreEqual(far.x, x, 0.07f);
                Assert.AreEqual(far.y, y, 0.26f);
                Assert.AreEqual(far.z, z, 0.07f);
            }
            finally
            {
                previousModes.Dispose();
                objectIds.Dispose();
                entityData.Dispose();
                movementResults.Dispose();
                modesResults.Dispose();
            }
        }
    }
}
