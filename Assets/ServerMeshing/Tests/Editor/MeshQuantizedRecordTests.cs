using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The kit's quantized positions (DefaultGridManagerComponent enabled): the add-on's kit records must be the bytes the
    /// kit's own quantized writer makes, read back by the kit's client reader, and the kit must keep calling WriteServerState
    /// on the add-on's handlers.
    /// </summary>
    public class MeshQuantizedRecordTests
    {
        private const uint Id = 42;
        private const float CellSize = 128f;

        private static readonly PropertyInfo s_instanceProperty = typeof(DefaultGridManagerComponent).GetProperty(nameof(DefaultGridManagerComponent.Instance), BindingFlags.Public | BindingFlags.Static);
        private static readonly FieldInfo s_isDisabledField = typeof(DefaultGridManagerComponent).GetField("isDisabled", BindingFlags.NonPublic | BindingFlags.Instance);

        private IBaseGridManagerComponenent _previousInstance;
        private GameObject _gameObject;
        private DefaultGridManagerComponent _grid;
        private MeshFakeIdentities _identities;

        [SetUp]
        public void SetUp()
        {
            _previousInstance = DefaultGridManagerComponent.Instance;
            _gameObject = new GameObject("MeshQuantizedRecordTests") { hideFlags = HideFlags.HideAndDontSave };
            _identities = new MeshFakeIdentities();
        }

        [TearDown]
        public void TearDown()
        {
            s_instanceProperty.SetValue(null, _previousInstance);
            _identities.Dispose();
            Object.DestroyImmediate(_gameObject);
        }

        private void EnableGrid(bool disabled = false)
        {
            _grid = _gameObject.AddComponent<DefaultGridManagerComponent>();
            // Set up while disabled, as the kit's own wire tests do: an enabled setup logs, and the kit's logger is not
            // initialized in EditMode. Setup sets the instance either way.
            s_isDisabledField.SetValue(_grid, true);
            _grid.SetupDynamicGrid();
            s_isDisabledField.SetValue(_grid, disabled);
        }

        private static float Step => QuantizedPosition.GetStep(CellSize, 16);

        [Test]
        public void QuantizedCellSize_NoGridManager_IsZero()
        {
            s_instanceProperty.SetValue(null, null);

            Assert.That(MeshKinematicCharacterBody.QuantizedCellSize, Is.EqualTo(0f));
        }

        [Test]
        public void QuantizedCellSize_GridEnabled_IsTheKitCellSize()
        {
            EnableGrid();

            Assert.That(MeshKinematicCharacterBody.QuantizedCellSize, Is.EqualTo(CellSize));
        }

        [Test]
        public void QuantizedCellSize_GridDisabled_IsZero()
        {
            EnableGrid(disabled: true);

            Assert.That(MeshKinematicCharacterBody.QuantizedCellSize, Is.EqualTo(0f));
        }

        [Test]
        public void QuantizedCellSize_DestroyedGridManager_IsZero()
        {
            EnableGrid();
            Object.DestroyImmediate(_grid);

            Assert.That(MeshKinematicCharacterBody.QuantizedCellSize, Is.EqualTo(0f));
        }

        [Test]
        public void QuantizedMode_IsTheFinest()
        {
            Assert.That(MeshKinematicCharacterBody.QuantizedMode, Is.EqualTo(QuantizedPosition.MaxMode));
        }

        private static IEnumerable<TestCaseData> PoseCases()
        {
            yield return new TestCaseData(17u, (byte)0, new Vector3(1.5f, -2.5f, 3.25f), 90f).SetName("WriteKitTransform_Quantized_NearTheOrigin");
            yield return new TestCaseData(241u, (byte)2, new Vector3(-3071.3f, 12.04f, 20481.7f), 359.5f).SetName("WriteKitTransform_Quantized_NegativeAndFarCells");
            yield return new TestCaseData(128u, (byte)1, new Vector3(8191.99f, 400f, -0.01f), -90f).SetName("WriteKitTransform_Quantized_CellEdgesAndNegativeYaw");
        }

        [TestCaseSource(nameof(PoseCases))]
        public void WriteKitTransform_Quantized_MatchesTheKitsQuantizedWriter(uint flags, byte extra, Vector3 position, float yaw)
        {
            EnableGrid();
            NetDataWriter kit = new NetDataWriter();
            MovementResult result = new MovementResult
            {
                objectId = Id,
                movementState = flags,
                extraMovementState = extra,
                position = QuantizedPosition.Encode(position.x, position.y, position.z, CellSize, QuantizedPosition.MaxMode),
                compressedYAngle = GridUtility.CompressAngle(yaw),
            };
            Assert.That(_grid.WriteEntityServerState(kit, result, new List<EntityMovementForceApplier>()), Is.True);
            NetDataWriter writer = new NetDataWriter();

            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, extra, position, yaw);

            Assert.That(writer.CopyData(), Is.EqualTo(kit.CopyData()));
        }

        [TestCaseSource(nameof(PoseCases))]
        public void WriteKitTransform_Quantized_RoundTripsThroughTheKitClientReader(uint flags, byte extra, Vector3 position, float yaw)
        {
            EnableGrid();
            NetDataWriter writer = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(writer, flags, extra, position, yaw);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            reader.ClientReadSyncTransformMessage3D(out MovementState state, out ExtraMovementState readExtra, out Vector3 read, out float readYaw, out List<EntityMovementForceApplier> forceAppliers);

            Assert.That((uint)state, Is.EqualTo(flags));
            Assert.That((byte)readExtra, Is.EqualTo(extra));
            Assert.That(Mathf.Abs(read.x - position.x), Is.LessThanOrEqualTo(Step));
            Assert.That(Mathf.Abs(read.y - position.y), Is.LessThanOrEqualTo(Step));
            Assert.That(Mathf.Abs(read.z - position.z), Is.LessThanOrEqualTo(Step));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(readYaw, yaw)), Is.LessThanOrEqualTo(360f / 255f));
            Assert.That(forceAppliers, Is.Empty);
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void WriteKitTransform_ExplicitZeroCellSize_IsRawEvenWithTheGridOn()
        {
            EnableGrid();
            NetDataWriter raw = new NetDataWriter();
            NetDataWriter writer = new NetDataWriter();
            s_instanceProperty.SetValue(null, null);
            MeshKinematicCharacterBody.WriteKitTransform(raw, 17u, 0, new Vector3(1.5f, -2.5f, 3.25f), 90f);
            s_instanceProperty.SetValue(null, _grid);

            MeshKinematicCharacterBody.WriteKitTransform(writer, 17u, 0, new Vector3(1.5f, -2.5f, 3.25f), 90f, 0f);

            Assert.That(writer.CopyData(), Is.EqualTo(raw.CopyData()));
            Assert.That(writer.Length, Is.EqualTo(22));
        }

        [Test]
        public void Body_ReadServerStateAtClient_ConsumesExactlyOneQuantizedRecord()
        {
            EnableGrid();
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, null, _gameObject.transform, new MeshFakePassKitHandler(Id), false, new MeshKinematicTrack(snapDistance: 5f));
            NetDataWriter writer = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(writer, 241u, 2, new Vector3(-3071.3f, 12.04f, 20481.7f), 10f);
            writer.Put((byte)0xBB);
            NetDataReader reader = new NetDataReader(writer.CopyData());

            body.ReadServerStateAtClient(0, reader);

            Assert.That(reader.GetByte(), Is.EqualTo((byte)0xBB));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void Body_WriteServerState_GridOn_WritesTheQuantizedRecordOfTheTransform()
        {
            EnableGrid();
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(Id, null, _gameObject.transform, new MeshFakePassKitHandler(Id), false, new MeshKinematicTrack(snapDistance: 5f));
            body.Accept(new Vector3(9f, 9f, 9f), Vector3.zero, 10f, 17, 0, false, 1f);
            _gameObject.transform.SetPositionAndRotation(new Vector3(-300.5f, 4f, 7000.25f), Quaternion.Euler(0f, 45f, 0f));
            NetDataWriter expected = new NetDataWriter();
            MeshKinematicCharacterBody.WriteKitTransform(expected, 17u, 0, new Vector3(-300.5f, 4f, 7000.25f), _gameObject.transform.eulerAngles.y, CellSize);
            NetDataWriter writer = new NetDataWriter();

            Assert.That(body.WriteServerState(100, writer, out bool reliable), Is.True);

            Assert.That(writer.CopyData(), Is.EqualTo(expected.CopyData()));
            Assert.That(reliable, Is.False);
        }

        [Test]
        public void Source_WriteState_GridOn_WritesTheQuantizedRecordFromCreateMovementData()
        {
            EnableGrid();
            FakeQuantizedHandler kit = new FakeQuantizedHandler { reliable = true };
            MeshKitMovementStateSource source = new MeshKitMovementStateSource(kit, _identities.New(Id, 7), _ => { });
            NetDataWriter kitWriter = new NetDataWriter();
            MovementResult result = new MovementResult
            {
                objectId = Id,
                movementState = kit.flags,
                extraMovementState = kit.extra,
                position = QuantizedPosition.Encode(kit.position.x, kit.position.y, kit.position.z, CellSize, QuantizedPosition.MaxMode),
                compressedYAngle = GridUtility.CompressAngle(kit.yaw),
            };
            _grid.WriteEntityServerState(kitWriter, result, kit.forceAppliers);
            NetDataWriter writer = new NetDataWriter();

            bool wrote = source.WriteState(1000, writer, out bool reliable);

            Assert.That(wrote, Is.True);
            Assert.That(reliable, Is.True);
            Assert.That(writer.CopyData(), Is.EqualTo(kitWriter.CopyData()));
            Assert.That(kit.creates, Is.EqualTo(1));
            Assert.That(kit.writes, Is.EqualTo(0));
        }

        [Test]
        public void Source_WriteState_GridOnBeforeStart_WritesNothing()
        {
            EnableGrid();
            FakeQuantizedHandler kit = new FakeQuantizedHandler { canWrite = false, reliable = true };
            MeshKitMovementStateSource source = new MeshKitMovementStateSource(kit, _identities.New(Id, 7), _ => { });
            NetDataWriter writer = new NetDataWriter();

            bool wrote = source.WriteState(1000, writer, out bool reliable);

            Assert.That(wrote, Is.False);
            Assert.That(reliable, Is.False);
            Assert.That(writer.Length, Is.EqualTo(0));
            Assert.That(kit.creates + kit.writes, Is.EqualTo(0));
        }

        [Test]
        public void Source_WriteState_GridOff_UsesTheInnerWriteServerState()
        {
            EnableGrid(disabled: true);
            FakeQuantizedHandler kit = new FakeQuantizedHandler();
            MeshKitMovementStateSource source = new MeshKitMovementStateSource(kit, _identities.New(Id, 7), _ => { });

            Assert.That(source.WriteState(1000, new NetDataWriter(), out _), Is.True);

            Assert.That(kit.writes, Is.EqualTo(1));
            Assert.That(kit.creates, Is.EqualTo(0));
        }

        [Test]
        public void Source_WriteState_GridOnWithAHandlerOfItsOwnFormat_UsesItsWriteServerState()
        {
            EnableGrid();
            MeshFakePassKitHandler kit = new MeshFakePassKitHandler(Id);
            MeshKitMovementStateSource source = new MeshKitMovementStateSource(kit, _identities.New(Id, 7), _ => { });

            Assert.That(source.WriteState(1000, new NetDataWriter(), out _), Is.True);

            Assert.That(kit.writes, Is.EqualTo(1));
        }

        [TestCase(typeof(BuiltInEntityMovementFunctions3D), true)]
        [TestCase(typeof(NavMeshEntityMovement), true)]
        [TestCase(typeof(MeshKitMovementStateSource), false)]
        [TestCase(typeof(MeshKinematicCharacterBody), false)]
        public void KitQuantizedPath_CallsWriteServerStateOnlyOnTheAddOnsHandlers(Type handlerType, bool quantized)
        {
            // The kit's quantized send loop takes CreateMovementData from IQuantizedEntityMovementDataHandler handlers and
            // calls WriteServerState on every other one, which is how the add-on's handlers stay silent or write themselves.
            Assert.That(typeof(IQuantizedEntityMovementDataHandler).IsAssignableFrom(handlerType), Is.EqualTo(quantized));
        }

        private sealed class FakeQuantizedHandler : IEntityMovementDataHandler, IQuantizedEntityMovementDataHandler
        {
            public bool canWrite = true;
            public bool reliable;
            public uint flags = 17;
            public byte extra = 2;
            public Vector3 position = new Vector3(-3071.3f, 12.04f, 20481.7f);
            public float yaw = 200f;
            public List<EntityMovementForceApplier> forceAppliers = new List<EntityMovementForceApplier>();
            public int creates;
            public int writes;

            public uint ObjectId => Id;
            public long ConnectionId => -1;
            public bool CanWriteQuantizedServerState => canWrite;

            public MovementData CreateMovementData(out List<EntityMovementForceApplier> appliers)
            {
                creates++;
                appliers = forceAppliers;
                return new MovementData
                {
                    movementState = flags,
                    extraMovementState = extra,
                    worldPosition = position,
                    yAngle = yaw,
                    shouldSendReliably = reliable,
                };
            }

            public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
            {
            }

            public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
            {
            }

            public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
            {
                shouldSendReliably = false;
                return false;
            }

            public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
            {
                writes++;
                shouldSendReliably = false;
                MeshKinematicCharacterBody.WriteKitTransform(writer, flags, extra, position, yaw, 0f);
                return true;
            }
        }
    }
}
