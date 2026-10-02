using System;
using System.Reflection;
using System.Runtime.Serialization;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshKitEditTests
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string FlagsRow = "CE-12: one-shot movement flags reach every receiver and are consumed once per tick (DESIGN.md §17)";
        private const string ReadRow = "CE-13: every server state read resumes at its length (DESIGN.md §17)";
        private const byte TrailerByte = 0xBB;
        private const int StateStart = 3;
        private const int StateLength = 8;

        [Test]
        public void CharacterDataCache_HasNoFinalizer()
        {
            MethodInfo finalizer = typeof(CharacterDataCache).GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            Assert.That(finalizer, Is.Null, "CE-11: a CharacterDataCache finalizer releases pooled collections from the GC thread (DESIGN.md §17)");
        }

        [TestCase(typeof(BuiltInEntityMovementFunctions3D))]
        [TestCase(typeof(NavMeshEntityMovement))]
        public void KitMovementHandler_ImplementsServerStateFlags(Type handlerType)
        {
            Assert.That(typeof(IEntityMovementServerStateFlags).IsAssignableFrom(handlerType), Is.True, FlagsRow);
        }

        [Test]
        public void FlagsPassRecord_NullHandler_RecordsNothing()
        {
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();

            pass.Record(null);

            Assert.That(pass.RecordedCount, Is.EqualTo(0), FlagsRow);
        }

        [Test]
        public void FlagsPassRecord_HandlerWithoutFlags_RecordsNothing()
        {
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();

            pass.Record(new MeshFakeKitHandler(1));

            Assert.That(pass.RecordedCount, Is.EqualTo(0), FlagsRow);
        }

        [Test]
        public void FlagsPassBegin_DropsUnconsumedRecords()
        {
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();
            MeshFakeServerStateHandler handler = new MeshFakeServerStateHandler(1);
            pass.Record(handler);

            pass.Begin();
            pass.ConsumeAll();

            Assert.That(handler.consumes, Is.EqualTo(0), FlagsRow);
        }

        [Test]
        public void FlagsPassRecord_SameHandlerTwice_RecordsItOnce()
        {
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();
            MeshFakeServerStateHandler handler = new MeshFakeServerStateHandler(1);

            pass.Record(handler);
            pass.Record(handler);

            Assert.That(pass.RecordedCount, Is.EqualTo(1), FlagsRow);
        }

        [Test]
        public void FlagsPassConsumeAll_ConsumesEachRecordedHandlerOnceAndClears()
        {
            EntityMovementServerStateFlagsPass pass = new EntityMovementServerStateFlagsPass();
            MeshFakeServerStateHandler first = new MeshFakeServerStateHandler(1);
            MeshFakeServerStateHandler second = new MeshFakeServerStateHandler(2);
            pass.Begin();
            pass.Record(first);
            pass.Record(new MeshFakeKitHandler(3));
            pass.Record(first);
            pass.Record(second);
            pass.Record(first);

            pass.ConsumeAll();
            pass.ConsumeAll();

            Assert.That(pass.RecordedCount, Is.EqualTo(0), FlagsRow);
            Assert.That(first.consumes, Is.EqualTo(1), FlagsRow);
            Assert.That(second.consumes, Is.EqualTo(1), FlagsRow);
        }

        [Test]
        public void BuiltInConsumeServerStateFlags_ClearsTheOneShotFlagsAndKeepsTheTeleportConfirm()
        {
            object handler = FormatterServices.GetUninitializedObject(typeof(BuiltInEntityMovementFunctions3D));
            string[] oneShot = { "_sendingJump", "_sendingDash", "_isTeleporting", "_stillMoveAfterTeleport" };
            string[] confirm = { "_isServerWaitingTeleportConfirm", "_isClientConfirmingTeleport" };
            foreach (string field in oneShot)
                SetFlag(handler, field, true);
            foreach (string field in confirm)
                SetFlag(handler, field, true);

            ((IEntityMovementServerStateFlags)handler).ConsumeServerStateFlags();

            foreach (string field in oneShot)
                Assert.That(GetFlag(handler, field), Is.False, $"{FlagsRow}: {field}");
            foreach (string field in confirm)
                Assert.That(GetFlag(handler, field), Is.True, $"{FlagsRow}: {field} is the owner's confirm, not a one-shot flag");
        }

        [Test]
        public void NavMeshConsumeServerStateFlags_ClearsTheOneShotFlagsAndTheDash()
        {
            object handler = FormatterServices.GetUninitializedObject(typeof(NavMeshEntityMovement));
            string[] oneShot = { "_sendingDash", "_isTeleporting", "_stillMoveAfterTeleport" };
            foreach (string field in oneShot)
                SetFlag(handler, field, true);

            ((IEntityMovementServerStateFlags)handler).ConsumeServerStateFlags();

            foreach (string field in oneShot)
                Assert.That(GetFlag(handler, field), Is.False, $"{FlagsRow}: {field}");
        }

        [TestCase(2, true, false, TestName = "ReadServerEntityState_HandlerThrows_ReturnsFalseAtStateEnd")]
        [TestCase(StateLength + 2, false, true, TestName = "ReadServerEntityState_HandlerOverReads_SeeksBackToStateEnd")]
        [TestCase(3, false, true, TestName = "ReadServerEntityState_HandlerUnderReads_SeeksToStateEnd")]
        [TestCase(StateLength, false, true, TestName = "ReadServerEntityState_HandlerReadsTheState_EndsAtStateEnd")]
        public void ReadServerEntityState_LeavesTheReaderAtTheStateEnd(int bytesToRead, bool throws, bool expected)
        {
            NetDataReader reader = StateReader();
            MeshFakeServerStateHandler handler = new MeshFakeServerStateHandler(1) { bytesToRead = bytesToRead, throwAfterReading = throws };

            bool read = BaseGameNetworkManager.ReadServerEntityState(handler, 1000, reader, StateLength);

            Assert.That(read, Is.EqualTo(expected), ReadRow);
            Assert.That(reader.Position, Is.EqualTo(StateStart + StateLength), ReadRow);
            Assert.That(reader.GetByte(), Is.EqualTo(TrailerByte), ReadRow);
        }

        private static NetDataReader StateReader()
        {
            NetDataWriter writer = new NetDataWriter();
            for (int i = 0; i < StateStart; ++i)
                writer.Put((byte)0xAA);
            for (int i = 0; i < StateLength; ++i)
                writer.Put((byte)i);
            for (int i = 0; i < 4; ++i)
                writer.Put(TrailerByte);
            NetDataReader reader = new NetDataReader(writer);
            reader.SetPosition(StateStart);
            return reader;
        }

        private static FieldInfo Flag(object handler, string name)
        {
            FieldInfo field = handler.GetType().GetField(name, InstanceFields);
            Assert.That(field, Is.Not.Null, $"{handler.GetType().Name} has no field {name}");
            Assert.That(field.FieldType, Is.EqualTo(typeof(bool)), $"{handler.GetType().Name}.{name} is not a bool");
            return field;
        }

        private static void SetFlag(object handler, string name, bool value)
        {
            Flag(handler, name).SetValue(handler, value);
        }

        private static bool GetFlag(object handler, string name)
        {
            return (bool)Flag(handler, name).GetValue(handler);
        }
    }
}
