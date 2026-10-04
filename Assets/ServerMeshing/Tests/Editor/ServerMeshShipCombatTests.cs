using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipCombatTests
    {
        private static IEnumerable<TestCaseData> DamageCases()
        {
            yield return new TestCaseData(100f, 2500, -50, 100f, 2500, false).SetName("ApplyDamage_NegativeAmount_ChangesNothing");
            yield return new TestCaseData(0f, 0, 50, 0f, 0, false).SetName("ApplyDamage_HullAlreadyZero_NotDestroyedAgain");
            yield return new TestCaseData(0f, 40, 50, 0f, 0, true).SetName("ApplyDamage_PastTheHull_FloorsAtZeroAndDestroys");
            yield return new TestCaseData(0f, 50, 50, 0f, 0, true).SetName("ApplyDamage_ExactlyTheHull_Destroys");
            yield return new TestCaseData(100f, 2500, 50, 50f, 2500, false).SetName("ApplyDamage_ShieldUp_ShieldTakesItAll");
            yield return new TestCaseData(30f, 2500, 50, 0f, 2480, false).SetName("ApplyDamage_ShieldLow_RemainderOffTheHull");
            yield return new TestCaseData(0f, 2500, 50, 0f, 2450, false).SetName("ApplyDamage_ShieldDown_AllOffTheHull");
        }

        [TestCaseSource(nameof(DamageCases))]
        public void ApplyDamage(float shield, int hull, int amount, float expectedShield, int expectedHull, bool expectedDestroyed)
        {
            bool destroyed = ServerMeshShipCombat.ApplyDamage(shield, hull, amount, out float newShield, out int newHull);

            Assert.That(destroyed, Is.EqualTo(expectedDestroyed));
            Assert.That(newShield, Is.EqualTo(expectedShield));
            Assert.That(newHull, Is.EqualTo(expectedHull));
        }

        private static IEnumerable<TestCaseData> RechargeCases()
        {
            yield return new TestCaseData(1000f, 1.0, 0.1f, 1000f).SetName("RechargeShield_UnderTheDelay_Unchanged");
            yield return new TestCaseData(1000f, 2.0, 0.1f, 1000f).SetName("RechargeShield_AtTheDelay_Unchanged");
            yield return new TestCaseData(2500f, 10.0, 0.1f, 2500f).SetName("RechargeShield_Full_Unchanged");
            yield return new TestCaseData(1000f, 10.0, 0f, 1000f).SetName("RechargeShield_NoTimePassed_Unchanged");
            yield return new TestCaseData(1000f, 2.5, 0.1f, 1010f).SetName("RechargeShield_AfterTheDelay_RisesByRateTimesDt");
            yield return new TestCaseData(2495f, 10.0, 0.1f, 2500f).SetName("RechargeShield_NearFull_CappedAtCapacity");
        }

        [TestCaseSource(nameof(RechargeCases))]
        public void RechargeShield(float shield, double sinceDamage, float dt, float expected)
        {
            float next = ServerMeshShipCombat.RechargeShield(shield, ServerMeshShipCombat.ShieldCapacity, ServerMeshShipCombat.ShieldRechargeRate, sinceDamage, dt);

            Assert.That(next, Is.EqualTo(expected).Within(1e-3f));
        }

        [Test]
        public void RespawnTime_IsTenSecondsAfterTheDestruction()
        {
            Assert.That(ServerMeshShipCombat.RespawnTime(5.0), Is.EqualTo(15.0));
            Assert.That(ServerMeshShipCombat.RespawnDue(14.99, 15.0), Is.False);
            Assert.That(ServerMeshShipCombat.RespawnDue(15.0, 15.0), Is.True);
            Assert.That(ServerMeshShipCombat.RespawnDue(20.0, 15.0), Is.True);
        }

        [Test]
        public void Respawn_LeavesTheEpoch_NothingOnTheServerAdvancesIt()
        {
            Assert.That(typeof(ServerMeshShipMovement).GetMethod("AdvanceEpoch"), Is.Null, "the space epoch stays 0 in Phase 0, so a respawn only teleports");
            Assert.That(typeof(ServerMeshShipMovement).GetProperty(nameof(ServerMeshShipMovement.Epoch)).CanWrite, Is.False);
        }

        [Test]
        public void RateAllows_EnforcesTheFireInterval()
        {
            Assert.That(ServerMeshShipCombat.RateAllows(1.1, 1.0, ServerMeshShipCombat.FireIntervalSeconds), Is.False);
            Assert.That(ServerMeshShipCombat.RateAllows(1.175, 1.0, ServerMeshShipCombat.FireIntervalSeconds), Is.True);
            Assert.That(ServerMeshShipCombat.RateAllows(0.0, double.NegativeInfinity, ServerMeshShipCombat.FireIntervalSeconds), Is.True);
        }

        private static IEnumerable<TestCaseData> SendCases()
        {
            yield return new TestCaseData(-1, false, false, 0).SetName("ExpectedRpcSends_NegativeSubscribers_Zero");
            yield return new TestCaseData(0, false, false, 0).SetName("ExpectedRpcSends_NoOneWatching_Zero");
            yield return new TestCaseData(4, false, false, 4).SetName("ExpectedRpcSends_ServerOwned_OnePerSubscriber");
            yield return new TestCaseData(4, true, true, 4).SetName("ExpectedRpcSends_OwnerSubscribes_NotCountedTwice");
            yield return new TestCaseData(4, true, false, 5).SetName("ExpectedRpcSends_OwnerNotSubscribed_PlusOne");
        }

        [TestCaseSource(nameof(SendCases))]
        public void ExpectedRpcSends(int subscribers, bool hasOwner, bool ownerSubscribes, int expected)
        {
            Assert.That(ServerMeshShipCombat.ExpectedRpcSends(subscribers, hasOwner, ownerSubscribes), Is.EqualTo(expected));
        }

        [Test]
        public void BoltVelocity_StillShooter_FliesAtProjectileSpeedAlongTheAim()
        {
            Vector3 velocity = ServerMeshShipCombat.BoltVelocity(new Vector3(0f, 0f, 500f), 800f, Vector3.zero);

            Assert.That(Vector3.Distance(velocity, new Vector3(0f, 0f, 800f)), Is.LessThan(1e-3f));
        }

        [Test]
        public void BoltVelocity_MovingShooter_KeepsOnlyTheSpaceShipsResidualLateral()
        {
            Vector3 aim = new Vector3(100f, 0f, 400f);
            Vector3 shooter = new Vector3(60f, 0f, 10f);
            Vector3 lateral = Vector3.ProjectOnPlane(shooter, aim.normalized);
            float residual = lateral.magnitude * (1f - 800f / Mathf.Sqrt(800f * 800f + lateral.sqrMagnitude));

            Vector3 velocity = ServerMeshShipCombat.BoltVelocity(aim, 800f, shooter);

            Assert.That(Vector3.ProjectOnPlane(velocity, aim.normalized).magnitude, Is.EqualTo(residual).Within(1e-3f));
            Assert.That(Vector3.Dot(velocity, aim), Is.GreaterThan(0f));
        }
    }
}
