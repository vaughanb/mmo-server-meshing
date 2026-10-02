using System;
using System.Collections.Generic;
using LiteNetLib.Utils;
using LiteNetLibManager;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomUploadTests
    {
        private const string GoldenFrame = "77 00 F7 E0 2A 11 00 00 00 C0 3F 00 00 20 C0 00 00 50 40 FA 20 BF 02";
        private static readonly Vector3 GoldenPosition = new Vector3(1.5f, -2.5f, 3.25f);

        private static string Hex(NetDataWriter writer) => BitConverter.ToString(writer.CopyData()).Replace("-", " ");

        private static NetDataWriter Frame(IServerMeshPhantomPayload payload, long timestamp, bool moving, bool teleport, Vector3 position, float yaw, ref bool stopSent, out bool written, out bool reliable)
        {
            NetDataWriter writer = new NetDataWriter();
            ServerMeshPhantomUploads.WriteFrame(writer, timestamp, 42);
            written = payload.TryWrite(writer, moving, teleport, position, yaw, ref stopSent, out reliable);
            return writer;
        }

        private static NetDataReader ReadHeader(NetDataWriter writer, long timestamp)
        {
            NetDataReader reader = new NetDataReader(writer.CopyData());
            Assert.That(reader.GetPackedUShort(), Is.EqualTo(GameNetworkingConsts.EntityState));
            Assert.That(reader.GetPackedLong(), Is.EqualTo(timestamp));
            Assert.That(reader.GetPackedUInt(), Is.EqualTo(42u));
            return reader;
        }

        [Test]
        public void TransformFrame_Golden23Bytes()
        {
            bool stopSent = false;
            NetDataWriter writer = Frame(ServerMeshPhantomTransformPayload.Instance, 1000, true, false, GoldenPosition, 90f, ref stopSent, out bool written, out bool reliable);

            Assert.That(written, Is.True);
            Assert.That(reliable, Is.False);
            Assert.That(Hex(writer), Is.EqualTo(GoldenFrame));
            Assert.That(writer.Length, Is.EqualTo(23));
        }

        private static IEnumerable<TestCaseData> TransformRoundTripCases()
        {
            yield return new TestCaseData(1000L, 23).SetName("TransformFrame_SmallTimestamp_RoundTripsInTwentyThreeBytes");
            yield return new TestCaseData(1_790_000_000_000L, 27).SetName("TransformFrame_RealisticTimestamp_RoundTripsInTwentySevenBytes");
        }

        [TestCaseSource(nameof(TransformRoundTripCases))]
        public void TransformFrame_RoundTripsThroughTheKitReaders(long timestamp, int length)
        {
            bool stopSent = false;
            NetDataWriter writer = Frame(ServerMeshPhantomTransformPayload.Instance, timestamp, true, false, GoldenPosition, 90f, ref stopSent, out _, out _);
            NetDataReader reader = ReadHeader(writer, timestamp);

            reader.ServerReadSyncTransformMessage3D(out MovementState state, out ExtraMovementState extra, out Vector3 position, out float yaw);

            Assert.That(writer.Length, Is.EqualTo(length));
            Assert.That(state, Is.EqualTo(MovementState.Forward | MovementState.IsGrounded));
            Assert.That(extra, Is.EqualTo(ExtraMovementState.None));
            Assert.That(position, Is.EqualTo(GoldenPosition));
            Assert.That(yaw, Is.EqualTo(90f).Within(0.001f));
            Assert.That(reader.EndOfData, Is.True);
        }

        private static IEnumerable<TestCaseData> TransformStateCases()
        {
            yield return new TestCaseData(true, false, MovementState.Forward | MovementState.IsGrounded, false).SetName("TransformFrame_Moving_IsForwardGrounded");
            yield return new TestCaseData(false, false, MovementState.IsGrounded, false).SetName("TransformFrame_IdleOrStanding_IsGroundedOnly");
            yield return new TestCaseData(true, true, MovementState.Forward | MovementState.IsGrounded | MovementState.IsTeleport, true).SetName("TransformFrame_TeleportConfirm_CarriesIsTeleport_Reliably");
            yield return new TestCaseData(false, true, MovementState.IsGrounded | MovementState.IsTeleport, true).SetName("TransformFrame_StandingTeleportConfirm_CarriesIsTeleport_Reliably");
        }

        [TestCaseSource(nameof(TransformStateCases))]
        public void TransformFrame_State(bool moving, bool teleport, MovementState expected, bool expectReliable)
        {
            bool stopSent = false;
            Vector3 destination = new Vector3(40f, 0f, -12f);
            NetDataWriter writer = Frame(ServerMeshPhantomTransformPayload.Instance, 5000, moving, teleport, destination, 0f, ref stopSent, out bool written, out bool reliable);
            NetDataReader reader = ReadHeader(writer, 5000);

            reader.ServerReadSyncTransformMessage3D(out MovementState state, out _, out Vector3 position, out _);

            Assert.That(written, Is.True);
            Assert.That(reliable, Is.EqualTo(expectReliable));
            Assert.That(state, Is.EqualTo(expected));
            Assert.That(state & (MovementState.IsJump | MovementState.IsDash), Is.EqualTo(MovementState.None));
            Assert.That(position, Is.EqualTo(destination));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void InputFrame_Moving_RoundTripsWithKeyMovement()
        {
            bool stopSent = true;
            NetDataWriter writer = Frame(ServerMeshPhantomInputPayload.Instance, 2000, true, false, GoldenPosition, 270f, ref stopSent, out bool written, out bool reliable);
            NetDataReader reader = ReadHeader(writer, 2000);

            reader.ReadMovementInputMessage3D(out EntityMovementInputState inputState, out EntityMovementInput input);

            Assert.That(written, Is.True);
            Assert.That(reliable, Is.False);
            Assert.That(stopSent, Is.False);
            Assert.That(inputState, Is.EqualTo(EntityMovementInputState.IsKeyMovement | EntityMovementInputState.PositionChanged | EntityMovementInputState.RotationChanged));
            Assert.That(input.MovementState, Is.EqualTo(MovementState.Forward));
            Assert.That(input.ExtraMovementState, Is.EqualTo(ExtraMovementState.None));
            Assert.That(input.Position, Is.EqualTo(GoldenPosition));
            Assert.That(input.YAngle, Is.EqualTo(270f).Within(0.001f));
            Assert.That(reader.EndOfData, Is.True);
        }

        [Test]
        public void InputFrame_Idle_SendsOneReliableStopFrame_ThenNothing_UntilItMovesAgain()
        {
            bool stopSent = false;
            NetDataWriter stop = Frame(ServerMeshPhantomInputPayload.Instance, 3000, false, false, GoldenPosition, 0f, ref stopSent, out bool first, out bool reliable);
            NetDataReader reader = ReadHeader(stop, 3000);
            reader.ReadMovementInputMessage3D(out EntityMovementInputState inputState, out EntityMovementInput input);

            Assert.That(first, Is.True);
            Assert.That(reliable, Is.True);
            Assert.That(inputState, Is.EqualTo(EntityMovementInputState.IsKeyMovement | EntityMovementInputState.IsStopped));
            Assert.That(input.MovementState, Is.EqualTo(MovementState.None));
            Assert.That(reader.EndOfData, Is.True);

            Frame(ServerMeshPhantomInputPayload.Instance, 3001, false, false, GoldenPosition, 0f, ref stopSent, out bool second, out _);
            Assert.That(second, Is.False);

            Frame(ServerMeshPhantomInputPayload.Instance, 3002, true, false, GoldenPosition, 0f, ref stopSent, out bool moved, out _);
            Frame(ServerMeshPhantomInputPayload.Instance, 3003, false, false, GoldenPosition, 0f, ref stopSent, out bool stoppedAgain, out _);
            Assert.That(moved, Is.True);
            Assert.That(stoppedAgain, Is.True);
        }

        [Test]
        public void InputFrame_TeleportConfirm_CarriesIsTeleportAndTheServerPosition_Reliably()
        {
            bool stopSent = true;
            Vector3 destination = new Vector3(-7f, 0f, 9f);
            NetDataWriter writer = Frame(ServerMeshPhantomInputPayload.Instance, 4000, false, true, destination, 45f, ref stopSent, out bool written, out bool reliable);
            NetDataReader reader = ReadHeader(writer, 4000);
            reader.ReadMovementInputMessage3D(out EntityMovementInputState inputState, out EntityMovementInput input);

            Assert.That(written, Is.True);
            Assert.That(reliable, Is.True);
            Assert.That(inputState & EntityMovementInputState.IsKeyMovement, Is.EqualTo(EntityMovementInputState.IsKeyMovement));
            Assert.That(input.MovementState, Is.EqualTo(MovementState.IsTeleport));
            Assert.That(input.Position, Is.EqualTo(destination));
            Assert.That(reader.EndOfData, Is.True);
        }

        private static IEnumerable<TestCaseData> TimestampCases()
        {
            yield return new TestCaseData(new long[] { 1000, 1000, 1000 }, new long[] { 950, 951, 952 }).SetName("NextTimestamp_ServerClockRepeats_StillRisesStrictly");
            yield return new TestCaseData(new long[] { 1000, 400, 300 }, new long[] { 950, 951, 952 }).SetName("NextTimestamp_ServerClockStepsBack_StillRisesStrictly");
            yield return new TestCaseData(new long[] { 1000, 9000, 9001 }, new long[] { 950, 8950, 8951 }).SetName("NextTimestamp_ServerClockJumps_FollowsFiftyMsBehind");
        }

        [TestCaseSource(nameof(TimestampCases))]
        public void NextTimestamp(long[] serverClock, long[] expected)
        {
            long last = 0;
            for (int i = 0; i < serverClock.Length; ++i)
            {
                last = ServerMeshPhantomUploads.NextTimestamp(last, serverClock[i]);
                Assert.That(last, Is.EqualTo(expected[i]));
            }
        }

        [Test]
        public void ShouldConfirmTeleport_OncePerDestination_OnlyWhileWaiting()
        {
            bool confirmed = false;
            Vector3 destination = Vector3.zero;
            Vector3 a = new Vector3(10f, 0f, 10f), b = new Vector3(-20f, 0f, 5f);

            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(false, a, ref confirmed, ref destination), Is.False);
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, a, ref confirmed, ref destination), Is.True);
            Assert.That(destination, Is.EqualTo(a));
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, a, ref confirmed, ref destination), Is.False);
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, a, ref confirmed, ref destination), Is.False);
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, b, ref confirmed, ref destination), Is.True);
            Assert.That(destination, Is.EqualTo(b));
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, b, ref confirmed, ref destination), Is.False);
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(false, b, ref confirmed, ref destination), Is.False);
            Assert.That(ServerMeshPhantomUploads.ShouldConfirmTeleport(true, b, ref confirmed, ref destination), Is.True);
        }

        private static int[] Simulate(int phantoms, double frameSeconds, double hz, double seconds, out int maxPerFrame, out int total)
        {
            double period = 1.0 / hz;
            double[] next = new double[phantoms];
            for (int k = 0; k < phantoms; ++k)
                next[k] = ServerMeshPhantomUploads.FirstDue(0, period, ServerMeshPhantomUploads.Unit(12345, k, 3));
            int windows = (int)Math.Round(seconds);
            int[] perWindowFirst = new int[windows];
            maxPerFrame = 0;
            total = 0;
            int frames = (int)Math.Round(seconds / frameSeconds);
            for (int f = 0; f < frames; ++f)
            {
                double now = f * frameSeconds;
                int window = Math.Min(windows - 1, (int)(now + 1e-9));
                for (int k = 0; k < phantoms; ++k)
                {
                    int calls = 0;
                    if (ServerMeshPhantomUploads.TakeDue(now, period, ref next[k]))
                        ++calls;
                    maxPerFrame = Math.Max(maxPerFrame, calls);
                    total += calls;
                    if (k == 0)
                        perWindowFirst[window] += calls;
                }
            }
            return perWindowFirst;
        }

        private static IEnumerable<TestCaseData> SchedulerCases()
        {
            yield return new TestCaseData(1.0 / 30.0, 20.0, 20).SetName("TakeDue_TwentyHzAtThirtyFps_TwentyPerSecond");
            yield return new TestCaseData(1.0 / 60.0, 20.0, 20).SetName("TakeDue_TwentyHzAtSixtyFps_TwentyPerSecond");
            yield return new TestCaseData(1.0 / 30.0, 60.0, 30).SetName("TakeDue_SixtyHzAtThirtyFps_ThirtyPerSecond");
        }

        [TestCaseSource(nameof(SchedulerCases))]
        public void TakeDue_Scheduler(double frameSeconds, double hz, int perSecond)
        {
            int[] perWindow = Simulate(200, frameSeconds, hz, 60.0, out int maxPerFrame, out int total);

            Assert.That(maxPerFrame, Is.LessThanOrEqualTo(1));
            for (int w = 1; w < perWindow.Length; ++w)
                Assert.That(perWindow[w], Is.InRange(perSecond - 1, perSecond + 1), "window " + w);
            Assert.That(total, Is.InRange(200 * (perSecond * 60 - 1), 200 * (perSecond * 60 + 1)));
        }

        [Test]
        public void TakeDue_AfterAStall_GivesAtMostOne_AndNoBurst()
        {
            double period = 0.05, next = 0.0, now = 0.0;
            for (int f = 0; f < 60; ++f, now += 1.0 / 60.0)
                ServerMeshPhantomUploads.TakeDue(now, period, ref next);
            now += 0.15;

            int atStall = ServerMeshPhantomUploads.TakeDue(now, period, ref next) ? 1 : 0;
            atStall += ServerMeshPhantomUploads.TakeDue(now, period, ref next) ? 1 : 0;
            int after = 0;
            for (int f = 1; f <= 60; ++f)
                after += ServerMeshPhantomUploads.TakeDue(now + f / 60.0, period, ref next) ? 1 : 0;

            Assert.That(atStall, Is.EqualTo(1));
            Assert.That(next, Is.GreaterThan(now));
            Assert.That(after, Is.InRange(19, 21));
        }

        [Test]
        public void Step_StaysWithinSpeedTimesDeltaAndInsideTheRegion()
        {
            Rect region = new Rect(-10f, -10f, 20f, 20f);
            System.Random random = new System.Random(7);
            Vector3 position = new Vector3(3f, 0f, -2f);
            Vector2 heading = new Vector2(0.6f, 0.8f);
            const float speed = 3f, dt = 1f / 30f;
            for (int i = 0; i < 3000; ++i)
            {
                if (i % 90 == 0)
                {
                    float angle = (float)random.NextDouble() * Mathf.PI * 2f;
                    heading = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                }
                Vector3 next = ServerMeshPhantomUploads.Step(position, ref heading, speed * dt, region);
                Assert.That(Vector3.Distance(position, next), Is.LessThanOrEqualTo(speed * dt + 1e-4f));
                Assert.That(next.x, Is.InRange(region.xMin, region.xMax));
                Assert.That(next.z, Is.InRange(region.yMin, region.yMax));
                Assert.That(next.y, Is.EqualTo(position.y));
                position = next;
            }
        }

        [Test]
        public void Step_FromOutsideTheRegion_WalksBackAtSpeed()
        {
            Rect region = new Rect(0f, 0f, 10f, 10f);
            Vector2 heading = Vector2.right;
            Vector3 position = new Vector3(30f, 0f, 5f);

            Vector3 next = ServerMeshPhantomUploads.Step(position, ref heading, 1f, region);

            Assert.That(next.x, Is.EqualTo(29f).Within(1e-4f));
            Assert.That(next.z, Is.EqualTo(5f).Within(1e-4f));
        }

        [Test]
        public void InShare_PicksAboutTheShare_Deterministically()
        {
            int picked = 0;
            for (int k = 0; k < 10000; ++k)
            {
                bool inShare = ServerMeshPhantomUploads.InShare(12345, k, 1, 0.3f);
                Assert.That(ServerMeshPhantomUploads.InShare(12345, k, 1, 0.3f), Is.EqualTo(inShare));
                if (inShare)
                    ++picked;
            }

            Assert.That(picked, Is.InRange(2800, 3200));
            Assert.That(ServerMeshPhantomUploads.InShare(12345, 3, 1, 0f), Is.False);
            Assert.That(ServerMeshPhantomUploads.InShare(12345, 3, 1, 1f), Is.True);
        }
    }
}
