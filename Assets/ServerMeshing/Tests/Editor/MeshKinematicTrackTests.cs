using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshKinematicTrackTests
    {
        private const float Frame = 1f / 60f;

        private static IEnumerable<TestCaseData> ConstructorFailureCases()
        {
            yield return new TestCaseData(0f, 0.3f, 4f, "blendRate").SetName("Constructor_ZeroBlendRate_Throws");
            yield return new TestCaseData(-1f, 0.3f, 4f, "blendRate").SetName("Constructor_NegativeBlendRate_Throws");
            yield return new TestCaseData(float.NaN, 0.3f, 4f, "blendRate").SetName("Constructor_NaNBlendRate_Throws");
            yield return new TestCaseData(15f, 0f, 4f, "maxExtrapolationSeconds").SetName("Constructor_ZeroExtrapolation_Throws");
            yield return new TestCaseData(15f, -0.3f, 4f, "maxExtrapolationSeconds").SetName("Constructor_NegativeExtrapolation_Throws");
            yield return new TestCaseData(15f, float.NaN, 4f, "maxExtrapolationSeconds").SetName("Constructor_NaNExtrapolation_Throws");
            yield return new TestCaseData(15f, 0.3f, 0f, "snapDistance").SetName("Constructor_ZeroSnapDistance_Throws");
            yield return new TestCaseData(15f, 0.3f, -4f, "snapDistance").SetName("Constructor_NegativeSnapDistance_Throws");
            yield return new TestCaseData(15f, 0.3f, float.NaN, "snapDistance").SetName("Constructor_NaNSnapDistance_Throws");
        }

        [TestCaseSource(nameof(ConstructorFailureCases))]
        public void Constructor_Invalid(float blendRate, float maxExtrapolationSeconds, float snapDistance, string expectedParam)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshKinematicTrack(blendRate, maxExtrapolationSeconds, snapDistance));

            Assert.That(thrown.ParamName, Is.EqualTo(expectedParam));
        }

        [Test]
        public void DefaultTrack_IsNotConfigured_AndItsMethodsThrowNamingTheStruct()
        {
            MeshKinematicTrack track = default;

            Assert.That(track.IsConfigured, Is.False);
            Assert.That(new MeshKinematicTrack().IsConfigured, Is.False);
            Assert.That(Assert.Throws<InvalidOperationException>(() => track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 1f)).Message, Does.Contain("MeshKinematicTrack"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => track.Predict(1f, 0.1f)).Message, Does.Contain("MeshKinematicTrack"));
            Assert.That(Assert.Throws<InvalidOperationException>(() => track.Pose(1f, 0f, out _)).Message, Does.Contain("MeshKinematicTrack"));
        }

        private static IEnumerable<TestCaseData> RejectedSampleCases()
        {
            yield return new TestCaseData(new Vector3(float.NaN, 0f, 0f), Vector3.zero, 0f, 1f).SetName("TryAccept_NaNPosition_Refused");
            yield return new TestCaseData(new Vector3(0f, float.PositiveInfinity, 0f), Vector3.zero, 0f, 1f).SetName("TryAccept_InfinitePosition_Refused");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, 0f, float.NaN), 0f, 1f).SetName("TryAccept_NaNVelocity_Refused");
            yield return new TestCaseData(Vector3.zero, new Vector3(float.NegativeInfinity, 0f, 0f), 0f, 1f).SetName("TryAccept_InfiniteVelocity_Refused");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, float.NaN, 1f).SetName("TryAccept_NaNYaw_Refused");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, float.PositiveInfinity, 1f).SetName("TryAccept_InfiniteYaw_Refused");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, 0f, float.NaN).SetName("TryAccept_NaNReceiveTime_Refused");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, 0f, float.PositiveInfinity).SetName("TryAccept_InfiniteReceiveTime_Refused");
        }

        [TestCaseSource(nameof(RejectedSampleCases))]
        public void TryAccept_NotANumber(Vector3 position, Vector3 velocity, float yaw, float receivedAt)
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            bool accepted = track.TryAccept(position, velocity, yaw, true, receivedAt);

            Assert.That(accepted, Is.False);
            Assert.That(track.HasSample, Is.False);
        }

        [Test]
        public void TryAccept_RefusedSample_ChangesNothing()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(new Vector3(1f, 2f, 3f), new Vector3(1f, 0f, 0f), 45f, false, 10f);

            bool accepted = track.TryAccept(new Vector3(float.NaN, 0f, 0f), new Vector3(9f, 9f, 9f), 90f, true, 11f);

            Assert.That(accepted, Is.False);
            Assert.That(track.Predict(10f, 0f), Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(track.Pose(10f, 0f, out float yaw), Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(yaw, Is.EqualTo(45f));
            Assert.That(track.Velocity, Is.EqualTo(new Vector3(1f, 0f, 0f)));
            Assert.That(track.YawDegrees, Is.EqualTo(45f));
            Assert.That(track.LastAcceptSnapped, Is.False);
        }

        [Test]
        public void TryAccept_OlderThanTheLastAccepted_Refused()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(new Vector3(1f, 0f, 0f), Vector3.zero, 0f, false, 10f);

            bool accepted = track.TryAccept(new Vector3(2f, 0f, 0f), Vector3.zero, 0f, false, 9.99f);

            Assert.That(accepted, Is.False);
            Assert.That(track.Predict(10f, 0f), Is.EqualTo(new Vector3(1f, 0f, 0f)));
        }

        [Test]
        public void TryAccept_SameReceiveTime_Accepted()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(new Vector3(1f, 0f, 0f), Vector3.zero, 0f, false, 10f);

            bool accepted = track.TryAccept(new Vector3(2f, 0f, 0f), Vector3.zero, 0f, false, 10f);

            Assert.That(accepted, Is.True);
            Assert.That(track.Predict(10f, 0f), Is.EqualTo(new Vector3(2f, 0f, 0f)));
        }

        [Test]
        public void Predict_BeforeTheFirstSample_Throws()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            Assert.Throws<InvalidOperationException>(() => track.Predict(1f, 0.1f));
        }

        [Test]
        public void Pose_BeforeTheFirstSample_Throws()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            Assert.Throws<InvalidOperationException>(() => track.Pose(1f, 0f, out _));
        }

        [TestCase(-90f, 270f)]
        [TestCase(725f, 5f)]
        [TestCase(360f, 0f)]
        public void TryAccept_Yaw_IsNormalised(float yaw, float expected)
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            track.TryAccept(Vector3.zero, Vector3.zero, yaw, false, 1f);

            Assert.That(track.YawDegrees, Is.EqualTo(expected).Within(1e-4f));
        }

        [Test]
        public void Predict_ExtrapolatesByAgePlusLead()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(new Vector3(1f, 0f, 0f), new Vector3(2f, 0f, -4f), 0f, false, 10f);

            Vector3 predicted = track.Predict(10.05f, 0.1f);

            Assert.That(Vector3.Distance(predicted, new Vector3(1.3f, 0f, -0.6f)), Is.LessThan(1e-4f));
            Assert.That(track.Velocity, Is.EqualTo(new Vector3(2f, 0f, -4f)));
        }

        [Test]
        public void Predict_LongAfterTheSample_IsCappedAtTheMaximumExtrapolation()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, new Vector3(10f, 0f, 0f), 0f, false, 10f);

            Vector3 predicted = track.Predict(15f, 0.1f);

            Assert.That(Vector3.Distance(predicted, new Vector3(3f, 0f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void Predict_BeforeTheSampleTime_NeverLooksBackwards()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(new Vector3(1f, 0f, 0f), new Vector3(10f, 0f, 0f), 0f, false, 10f);

            Assert.That(track.Predict(9f, 0f), Is.EqualTo(new Vector3(1f, 0f, 0f)));
            Assert.That(track.Predict(10f, -0.5f), Is.EqualTo(new Vector3(1f, 0f, 0f)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TryAccept_FirstSnapshot_SetsThePoseExactlyAndNeverSnaps(bool discontinuity)
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);

            track.TryAccept(new Vector3(1f, 2f, 3f), new Vector3(2f, 0f, -4f), 30f, discontinuity, 10f);

            Vector3 pose = track.Pose(10f, 0f, out float yaw);
            Assert.That(track.LastAcceptSnapped, Is.False);
            Assert.That(pose, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(yaw, Is.EqualTo(30f));
            Assert.That(Vector3.Distance(track.Pose(10.1f, 0f, out _), new Vector3(1.2f, 2f, 2.6f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void TryAccept_SnapshotOneMetreOff_GlidesFromTheShownPoseToUnderOneCentimetreInHalfASecond()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 0f);
            Vector3 shown = track.Pose(0.1f, 0f, out _);
            Vector3 snapshot = new Vector3(1f, 0f, 0f);

            track.TryAccept(snapshot, Vector3.zero, 0f, false, 0.1f);

            Assert.That(track.LastAcceptSnapped, Is.False);
            Assert.That(track.Pose(0.1f, 0f, out _), Is.EqualTo(shown), "a new snapshot does not make the puppet jump");
            float gap = Vector3.Distance(shown, snapshot);
            for (int frame = 1; frame <= 30; ++frame)
            {
                float next = Vector3.Distance(track.Pose(0.1f + frame * Frame, 0f, out _), snapshot);
                Assert.That(next, Is.LessThan(gap), "the gap closes every frame");
                gap = next;
            }
            float settled = Vector3.Distance(track.Pose(0.6f, 0f, out _), snapshot);
            Assert.That(settled, Is.EqualTo(Mathf.Exp(-7.5f)).Within(1e-5f));
            Assert.That(settled, Is.LessThan(0.01f));
        }

        private static IEnumerable<TestCaseData> MissCases()
        {
            yield return new TestCaseData(3.99f, false, Vector3.zero, 0f).SetName("TryAccept_MissJustUnderTheSnapDistance_GlidesFromTheShownPose");
            yield return new TestCaseData(4.01f, true, new Vector3(4.01f, 0f, 0f), 30f).SetName("TryAccept_MissPastTheSnapDistance_SnapsToTheSnapshot");
        }

        [TestCaseSource(nameof(MissCases))]
        public void TryAccept_MissAgainstTheSnapDistance(float miss, bool snaps, Vector3 expectedPose, float expectedYaw)
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 0f);

            track.TryAccept(new Vector3(miss, 0f, 0f), Vector3.zero, 30f, false, 0.1f);

            Vector3 pose = track.Pose(0.1f, 0f, out float yaw);
            Assert.That(track.LastAcceptSnapped, Is.EqualTo(snaps));
            Assert.That(pose, Is.EqualTo(expectedPose));
            Assert.That(yaw, Is.EqualTo(expectedYaw));
        }

        [Test]
        public void TryAccept_DiscontinuityWithASmallMiss_SnapsToTheSnapshot()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 0f);

            track.TryAccept(new Vector3(0.5f, 0f, 0f), Vector3.zero, 90f, true, 0.1f);

            Vector3 pose = track.Pose(0.1f, 0f, out float yaw);
            Assert.That(track.LastAcceptSnapped, Is.True);
            Assert.That(pose, Is.EqualTo(new Vector3(0.5f, 0f, 0f)));
            Assert.That(yaw, Is.EqualTo(90f));
        }

        [Test]
        public void TryAccept_AfterASnap_TheNextSnapshotGlidesAgain()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, Vector3.zero, 0f, false, 0f);
            track.TryAccept(new Vector3(0.5f, 0f, 0f), Vector3.zero, 90f, true, 0.1f);

            track.TryAccept(new Vector3(0.75f, 0f, 0f), Vector3.zero, 90f, false, 0.2f);

            Assert.That(track.LastAcceptSnapped, Is.False);
            Assert.That(track.Pose(0.2f, 0f, out _), Is.EqualTo(new Vector3(0.5f, 0f, 0f)));
        }

        private static IEnumerable<TestCaseData> ConstantVelocityCases()
        {
            yield return new TestCaseData(60).SetName("Pose_ConstantVelocityAtSixtyFps_StaysWithinOneCentimetreOfTheOwner");
            yield return new TestCaseData(30).SetName("Pose_ConstantVelocityAtThirtyFps_StaysWithinOneCentimetreOfTheOwner");
        }

        [TestCaseSource(nameof(ConstantVelocityCases))]
        public void Pose_ConstantVelocity(int fps)
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            Vector3 start = new Vector3(1f, 0f, -2f);
            Vector3 velocity = new Vector3(3f, 0f, 4f);
            int framesPerSnapshot = fps / 10;
            float worst = 0f;

            for (int frame = 0; frame <= 2 * fps; ++frame)
            {
                float now = frame / (float)fps;
                Vector3 owner = start + velocity * now;
                if (frame % framesPerSnapshot == 0)
                    Assert.That(track.TryAccept(owner, velocity, 0f, false, now), Is.True);
                worst = Mathf.Max(worst, Vector3.Distance(track.Pose(now, 0f, out _), owner));
            }

            Assert.That(worst, Is.LessThan(0.01f));
        }

        [Test]
        public void Pose_YawFrom359To1_TurnsTheShortWayThroughNorth()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, Vector3.zero, 359f, false, 0f);

            track.TryAccept(Vector3.zero, Vector3.zero, 1f, false, 0.1f);

            track.Pose(0.1f, 0f, out float yaw);
            Assert.That(yaw, Is.EqualTo(359f).Within(1e-4f), "a new snapshot does not make the puppet turn at once");
            float turned = 0f;
            for (int frame = 1; frame <= 60; ++frame)
            {
                float previous = yaw;
                track.Pose(0.1f + frame * Frame, 0f, out yaw);
                float step = Mathf.DeltaAngle(previous, yaw);
                Assert.That(yaw, Is.GreaterThanOrEqualTo(0f).And.LessThan(360f));
                Assert.That(step, Is.GreaterThanOrEqualTo(0f), "the turn from 359 to 1 goes through north");
                turned += step;
            }
            Assert.That(turned, Is.EqualTo(2f).Within(1e-3f));
            Assert.That(yaw, Is.EqualTo(1f).Within(1e-3f));
        }

        [Test]
        public void Pose_AtZeroVelocity_DoesNotDrift()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            Vector3 target = new Vector3(5f, 1f, -3f);
            track.TryAccept(target, Vector3.zero, 90f, false, 0f);
            track.TryAccept(target, Vector3.zero, 90f, false, 0.1f);
            float drift = 0f;
            float yaw = 0f;

            for (int frame = 1; frame <= 600; ++frame)
                drift = Mathf.Max(drift, Vector3.Distance(track.Pose(0.1f + frame * Frame, 0f, out yaw), target));

            Assert.That(drift, Is.EqualTo(0f));
            Assert.That(yaw, Is.EqualTo(90f));
        }

        [Test]
        public void Pose_WhenSnapshotsStop_IsCappedAtTheMaximumExtrapolation()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            track.TryAccept(Vector3.zero, new Vector3(10f, 0f, 0f), 0f, false, 0f);
            float worst = 0f;

            for (int frame = 0; frame <= 300; ++frame)
                worst = Mathf.Max(worst, Vector3.Distance(track.Pose(0.3f + frame * Frame, 0f, out _), new Vector3(3f, 0f, 0f)));

            Assert.That(worst, Is.LessThan(1e-4f));
        }

        [Test]
        public void Copy_IsIndependentOfTheOriginal()
        {
            MeshKinematicTrack track = new MeshKinematicTrack(15f, 0.3f, 4f);
            MeshKinematicTrack copy = track;

            copy.TryAccept(Vector3.one, Vector3.zero, 0f, false, 1f);

            Assert.That(copy.HasSample, Is.True);
            Assert.That(track.HasSample, Is.False, "a track read out of a collection or a readonly field is a copy");
        }
    }
}
