using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshCrossingCoastTests
    {
        private static MeshCrossingCoast Coast()
        {
            return new MeshCrossingCoast(new Vector3(1f, 0f, 0f), new Vector3(4f, 0f, 0f), 10f, 2f, 0.1f, true);
        }

        [Test]
        public void TryFeed_WaitsAnIntervalAfterTheFirstSample()
        {
            Assert.That(Coast().TryFeed(10.05f, out _), Is.False);
        }

        [Test]
        public void TryFeed_PlacesTheSampleOnTheTrack()
        {
            Assert.That(Coast().TryFeed(10.5f, out Vector3 position), Is.True);
            Assert.That(position.x, Is.EqualTo(3f).Within(0.001f));
        }

        [Test]
        public void TryFeed_ComesAtTheInterval()
        {
            MeshCrossingCoast coast = Coast();
            Assert.That(coast.TryFeed(10.15f, out _), Is.True);
            Assert.That(coast.TryFeed(10.2f, out _), Is.False);
            Assert.That(coast.TryFeed(10.26f, out _), Is.True);
        }

        [Test]
        public void TryFeed_StopsAtTheEndOfTheCoast()
        {
            Assert.That(Coast().TryFeed(99f, out Vector3 position), Is.True);
            Assert.That(position.x, Is.EqualTo(9f).Within(0.001f));
        }

        private const float Frame = 1f / 60f;

        private static MeshCrossingVelocity Walked(MeshCrossingVelocity tracked, int frames, int firstFrame, float metresPerFrame, int framesPerState)
        {
            for (int frame = firstFrame; frame < firstFrame + frames; ++frame)
            {
                float seen = (frame - frame % framesPerState) * metresPerFrame;
                tracked = tracked.Next(new Vector3(seen, 0f, 0f), frame * Frame, 15f, 0.15f);
            }
            return tracked;
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(5)]
        [TestCase(10)]
        public void Velocity_ReadsAWalkerWhoseStatesComeInBunches(int framesPerState)
        {
            MeshCrossingVelocity tracked = Walked(default, 120, 0, 4f * Frame, framesPerState);
            float low = float.MaxValue;
            float high = float.MinValue;
            for (int frame = 120; frame < 600; ++frame)
            {
                tracked = Walked(tracked, 1, frame, 4f * Frame, framesPerState);
                low = Mathf.Min(low, tracked.velocity.x);
                high = Mathf.Max(high, tracked.velocity.x);
            }
            Assert.That(low, Is.EqualTo(4f).Within(0.3f));
            Assert.That(high, Is.EqualTo(4f).Within(0.3f));
        }

        [Test]
        public void Velocity_FollowsAChangeWithinItsTime()
        {
            MeshCrossingVelocity tracked = Walked(default, 30, 0, 4f * Frame, 1);
            Assert.That(tracked.velocity.x, Is.GreaterThan(3.5f));
        }

        [Test]
        public void Velocity_FallsWhenTheCharacterStands()
        {
            MeshCrossingVelocity tracked = Walked(default, 120, 0, 4f * Frame, 1);
            Vector3 stood = tracked.position;
            for (int frame = 120; frame < 180; ++frame)
                tracked = tracked.Next(stood, frame * Frame, 15f, 0.15f);
            Assert.That(tracked.velocity.magnitude, Is.LessThan(1f));
        }

        [TestCase(0.2f)]
        [TestCase(0.26f)]
        [TestCase(0.43f)]
        [TestCase(0.8f)]
        public void Velocity_AHoleInTheStatesIsNotAStop(float holeSeconds)
        {
            MeshCrossingVelocity tracked = Walked(default, 120, 0, 4f * Frame, 1);
            Vector3 last = tracked.position;
            float at = tracked.time;
            int frames = Mathf.RoundToInt(holeSeconds / Frame);
            for (int frame = 1; frame < frames; ++frame)
                tracked = tracked.Next(last, at + frame * Frame, 15f, 0.15f);
            tracked = tracked.Next(last + new Vector3(4f * holeSeconds, 0f, 0f), at + holeSeconds, 15f, 0.15f);
            Assert.That(tracked.velocity.x, Is.EqualTo(4f).Within(0.6f));
        }

        [Test]
        public void Capped_HoldsAVelocityToWhatTheCharacterCanMove()
        {
            Vector3 capped = MeshCrossingVelocity.Capped(new Vector3(8.3f, 0f, 0f), 4f);
            Assert.That(capped.x, Is.EqualTo(4f * MeshCrossingVelocity.MoveSpeedMargin).Within(0.001f));
        }

        [Test]
        public void Capped_LeavesASpeedItCanMove()
        {
            Assert.That(MeshCrossingVelocity.Capped(new Vector3(0f, 0f, -3.5f), 4f).z, Is.EqualTo(-3.5f).Within(0.001f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        public void Capped_AnUnknownSpeedCapsNothing(float moveSpeed)
        {
            Assert.That(MeshCrossingVelocity.Capped(new Vector3(8.3f, 0f, 0f), moveSpeed).x, Is.EqualTo(8.3f).Within(0.001f));
        }

        [Test]
        public void Velocity_ATeleportCountsAsStanding()
        {
            MeshCrossingVelocity tracked = Walked(default, 120, 0, 4f * Frame, 1);
            float before = tracked.velocity.x;
            tracked = tracked.Next(new Vector3(500f, 0f, 0f), tracked.time + 0.1f, 15f, 0.15f);
            Assert.That(tracked.velocity.x, Is.LessThan(before));
            Assert.That(tracked.position.x, Is.EqualTo(500f));
        }

        [Test]
        public void Ended_AfterItsTime()
        {
            MeshCrossingCoast coast = Coast();
            Assert.That(coast.Ended(11.9f), Is.False);
            Assert.That(coast.Ended(12f), Is.True);
        }
    }
}
