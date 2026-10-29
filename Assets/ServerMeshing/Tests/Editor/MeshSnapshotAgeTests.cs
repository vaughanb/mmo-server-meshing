using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshSnapshotAgeTests
    {
        [Test]
        public void Transit_AnUnstampedSnapshot_IsNotAged()
        {
            MeshLinkTransit transit = new MeshLinkTransit();

            Assert.That(transit.Age(0.0, 1000.0), Is.EqualTo(0f));
        }

        [Test]
        public void Transit_AgeIsTheLatenessAgainstTheFastest_WhateverTheClocksOffset()
        {
            MeshLinkTransit transit = new MeshLinkTransit();
            // The sender's clock runs 50 000 ms ahead of ours: only the differences count.
            Assert.That(transit.Age(50000.0, 3.0), Is.EqualTo(0f));
            Assert.That(transit.Age(50100.0, 115.0), Is.EqualTo(0.012f).Within(1e-3f));
            Assert.That(transit.Age(50200.0, 201.0), Is.EqualTo(0f), "faster than the fastest: the new fastest");
            Assert.That(transit.Age(50300.0, 330.0), Is.EqualTo(0.029f).Within(1e-3f));
        }

        [Test]
        public void Transit_AStallIsCappedAtTheMaximumAge()
        {
            MeshLinkTransit transit = new MeshLinkTransit();
            transit.Age(1000.0, 5.0);

            Assert.That(transit.Age(1100.0, 600.0), Is.EqualTo(MeshLinkTransit.MaxSnapshotAgeSeconds));
        }

        [Test]
        public void Transit_ALastingSlowerRoute_IsFollowed()
        {
            MeshLinkTransit transit = new MeshLinkTransit();
            transit.Age(1000.0, 1.0);
            float age = 0f;
            // Every snapshot 20 ms slower than the first, for 10 s at 10 Hz.
            for (int i = 1; i <= 100; ++i)
                age = transit.Age(1000.0 + i * 100.0, 21.0 + i * 100.0);

            Assert.That(age, Is.EqualTo(0f), "the fastest rose 5 ms a second and caught up");
        }

        [Test]
        public void Track_ALateSnapshot_IsPredictedFromWhenItWasSampled()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            Vector3 velocity = new Vector3(4f, 0f, 0f);
            track.TryAccept(Vector3.zero, velocity, 0f, false, 1.000f);
            // The next snapshot was sampled at 1.100 s (0.4 m) but arrived 20 ms late.
            Assert.That(track.TryAccept(new Vector3(0.4f, 0f, 0f), velocity, 0f, false, 1.120f, 0.020f), Is.True);

            Vector3 pose = track.Pose(1.120f, 0f, out _);
            Assert.That(pose.x, Is.EqualTo(0.48f).Within(1e-4f), "the pose it showed: the first snapshot at 120 ms");
            Assert.That(track.Pose(1.300f, 0f, out _).x, Is.EqualTo(1.2f).Within(1e-3f), "on the owner's path, no offset to decay");
        }

        [Test]
        public void Track_ALateSnapshot_LeavesThePoseContinuous()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, new Vector3(4f, 0f, 0f), 0f, false, 1.000f);
            Vector3 before = track.Pose(1.130f, 0f, out _);

            track.TryAccept(new Vector3(0.5f, 0f, 0f), new Vector3(3f, 0f, 0f), 0f, false, 1.130f, 0.025f);

            Assert.That(Vector3.Distance(track.Pose(1.130f, 0f, out _), before), Is.LessThan(1e-4f));
        }

        [Test]
        public void Track_ASnapshotClaimingASampleBeforeTheLast_IsTakenAsSampledWithIt()
        {
            // A puppet seeded at a demotion (stamped then), and its owner's first snapshot 10 ms later, aged 50 ms by its link.
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, new Vector3(4f, 0f, 0f), 0f, false, 1.000f, 0f);
            Vector3 shown = track.Pose(1.010f, 0f, out _);

            Assert.That(track.TryAccept(new Vector3(0.05f, 0f, 0f), new Vector3(4f, 0f, 0f), 0f, false, 1.010f, 0.050f), Is.True);
            Assert.That(Vector3.Distance(track.Pose(1.010f, 0f, out _), shown), Is.LessThan(1e-4f), "the pose stays continuous");
            Assert.That(track.Predict(1.110f, 0f).x, Is.EqualTo(0.05f + 4f * 0.110f).Within(1e-4f), "predicted from the seed's sample time");
        }

        [Test]
        public void Track_ANaNAge_IsRefused()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            Assert.That(track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 1f, float.NaN), Is.False);
        }

        [TestCase(3.2f, 4.0f, 4.0f, TestName = "Velocity_ASteadyWalk_IsMeasuredOverTwoSnapshots")]
        [TestCase(4.8f, 4.0f, 4.0f, TestName = "Velocity_ASteadyWalkMeasuredHigh_IsMeasuredOverTwoSnapshots")]
        [TestCase(1.0f, 2.5f, 1.0f, TestName = "Velocity_AStop_IsTheLastInterval")]
        [TestCase(4.0f, 2.0f, 4.0f, TestName = "Velocity_AStart_IsTheLastInterval")]
        [TestCase(0.1f, 2.0f, 0.1f, TestName = "Velocity_Standing_IsTheLastInterval")]
        public void Velocity_PaceRules(float lastSpeed, float twoSpeed, float expected)
        {
            Vector3 v = MeshMapNetworkManager.SnapshotVelocity(new Vector3(lastSpeed, 0f, 0f), new Vector3(twoSpeed, 0f, 0f));

            Assert.That(v.x, Is.EqualTo(expected).Within(1e-5f));
        }

        [Test]
        public void Velocity_ATurn_IsTheLastInterval()
        {
            Vector3 last = Quaternion.Euler(0f, 45f, 0f) * new Vector3(4f, 0f, 0f);

            Assert.That(MeshMapNetworkManager.SnapshotVelocity(last, new Vector3(4f, 0f, 0f)), Is.EqualTo(last));
        }

        [Test]
        public void Snapshot_SampleStamp_RoundTrips_AndAnUnstampedPayloadReadsZero()
        {
            GhostSnapshotMessage msg = new GhostSnapshotMessage { kind = GhostKind.Player, characterId = "c", characterName = "n", sampleMs = 1234.5, ageSeconds = 0.05f };
            LiteNetLib.Utils.NetDataWriter writer = new LiteNetLib.Utils.NetDataWriter();
            msg.Serialize(writer);
            GhostSnapshotMessage read = default;
            read.Deserialize(new LiteNetLib.Utils.NetDataReader(writer.CopyData()));

            Assert.That(read.sampleMs, Is.EqualTo(1234.5));
            Assert.That(read.ageSeconds, Is.EqualTo(0f), "the age is the receiver's, never on the wire");

            byte[] unstamped = new byte[writer.Length - 8];
            System.Array.Copy(writer.Data, unstamped, unstamped.Length);
            GhostSnapshotMessage old = default;
            old.Deserialize(new LiteNetLib.Utils.NetDataReader(unstamped));
            Assert.That(old.sampleMs, Is.EqualTo(0.0));
        }
    }
}
