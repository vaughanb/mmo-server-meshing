using System.Collections.Generic;
using LiteNetLibManager;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Step 8b decision 16: the stand-in ships' IsAtRest follows the space game's (commit 771ad38c): linear speed under
    /// 0.01 m/s, angular speed under 0.001 rad/s, and no teleport pending.
    /// </summary>
    public class ServerMeshShipRestTests
    {
        private const uint Id = 78;

        private MeshFakeIdentities _identities;
        private LiteNetLibIdentity _identity;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
            _identity = _identities.New(Id);
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private static IEnumerable<TestCaseData> AtRestCases()
        {
            yield return new TestCaseData(Vector3.zero, Vector3.zero, true, false).SetName("AtRest_StillButTeleportPending_No");
            yield return new TestCaseData(new Vector3(0.011f, 0f, 0f), Vector3.zero, false, false).SetName("AtRest_LinearOverOneCentimetre_No");
            yield return new TestCaseData(new Vector3(0.008f, 0f, 0.008f), Vector3.zero, false, false).SetName("AtRest_LinearOverOneCentimetreAcrossAxes_No");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, 0.0011f, 0f), false, false).SetName("AtRest_AngularOverAMilliradian_No");
            yield return new TestCaseData(Vector3.zero, new Vector3(0f, 0.5f, 0f), false, false).SetName("AtRest_Turning_No");
            yield return new TestCaseData(new Vector3(0.009f, 0f, 0f), Vector3.zero, false, true).SetName("AtRest_LinearUnderOneCentimetre_Yes");
            yield return new TestCaseData(new Vector3(0.005f, 0f, 0f), Vector3.zero, false, true).SetName("AtRest_AboveStep7sMillimetreThreshold_Yes");
            yield return new TestCaseData(Vector3.zero, new Vector3(0.0009f, 0f, 0f), false, true).SetName("AtRest_AngularUnderAMilliradian_Yes");
            yield return new TestCaseData(Vector3.zero, Vector3.zero, false, true).SetName("AtRest_Still_Yes");
        }

        [TestCaseSource(nameof(AtRestCases))]
        public void AtRest(Vector3 velocity, Vector3 angularVelocity, bool teleportPending, bool expected)
        {
            Assert.That(ServerMeshShipMovement.AtRest(velocity, angularVelocity, teleportPending), Is.EqualTo(expected));
        }

        [Test]
        public void Thresholds_AreTheSpaceGamesSquared()
        {
            Assert.That(ServerMeshShipMovement.RestSpeedSquared, Is.EqualTo(0.01f * 0.01f).Within(1e-9f));
            Assert.That(ServerMeshShipMovement.RestAngularSpeedSquared, Is.EqualTo(0.001f * 0.001f).Within(1e-12f));
        }

        [Test]
        public void PuppetBody_NoSampleYet_AtRest()
        {
            Assert.That(NewBody().IsAtRest, Is.True);
        }

        [Test]
        public void PuppetBody_StillButTurningSample_NotAtRest()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(Vector3.zero, new Vector3(0f, 0.5f, 0f));
            body.Accept(in sample, 0);

            Assert.That(body.IsAtRest, Is.False);
        }

        [Test]
        public void PuppetBody_SlowSample_AtRest()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(0.005f, 0f, 0f), new Vector3(0f, 0.0005f, 0f));
            body.Accept(in sample, 0);

            Assert.That(body.IsAtRest, Is.True);
        }

        [Test]
        public void PuppetBody_MovingSample_NotAtRest()
        {
            ServerMeshShipPuppetBody body = NewBody();
            ServerMeshShipSample sample = Sample(new Vector3(0f, 0f, 2f), Vector3.zero);
            body.Accept(in sample, 0);

            Assert.That(body.IsAtRest, Is.False);
        }

        private ServerMeshShipPuppetBody NewBody()
        {
            return new ServerMeshShipPuppetBody(Id, _identity, _identity.transform, ServerMeshShipWireKind.Header56, new ServerMeshShipScale(1f));
        }

        private static ServerMeshShipSample Sample(Vector3 velocity, Vector3 angularVelocity)
        {
            return new ServerMeshShipSample
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Velocity = velocity,
                AngularVelocity = angularVelocity,
                TeleportByte = 3,
            };
        }
    }
}
