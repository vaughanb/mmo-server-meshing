using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipScaleTests
    {
        private static IEnumerable<TestCaseData> BadFactorCases()
        {
            yield return new TestCaseData(0f).SetName("Constructor_Zero_ThrowsNamingTheFactor");
            yield return new TestCaseData(-1f).SetName("Constructor_Negative_ThrowsNamingTheFactor");
            yield return new TestCaseData(float.NaN).SetName("Constructor_NaN_ThrowsNamingTheFactor");
            yield return new TestCaseData(float.PositiveInfinity).SetName("Constructor_Infinity_ThrowsNamingTheFactor");
            yield return new TestCaseData(0.0009f).SetName("Constructor_BelowTheMinimum_ThrowsNamingTheFactor");
            yield return new TestCaseData(11f).SetName("Constructor_AboveTheMaximum_ThrowsNamingTheFactor");
        }

        [TestCaseSource(nameof(BadFactorCases))]
        public void Constructor_BadFactor_ThrowsNamingIt(float factor)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMeshShipScale(factor));

            Assert.That(thrown.ParamName, Is.EqualTo("factor"));
        }

        private static IEnumerable<TestCaseData> BadTextCases()
        {
            foreach (string text in new[] { "x", "0", "-1", "11", "NaN", "1,5", "0.0001", "Infinity" })
                yield return new TestCaseData(text).SetName($"TryParse_{text.Replace(",", "Comma").Replace(".", "_")}_Fails");
        }

        [TestCaseSource(nameof(BadTextCases))]
        public void TryParse_Junk_FailsWithOneAndAnError(string text)
        {
            bool parsed = ServerMeshShipScale.TryParse(text, out float factor, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(factor, Is.EqualTo(1f));
            Assert.That(error, Does.Contain(text).And.Not.Contain("{").And.Not.Contain("Exception"));
        }

        private static IEnumerable<TestCaseData> GoodTextCases()
        {
            yield return new TestCaseData(null, 1f).SetName("TryParse_Null_IsOne");
            yield return new TestCaseData("", 1f).SetName("TryParse_Empty_IsOne");
            yield return new TestCaseData(" 0.03 ", 0.03f).SetName("TryParse_PointZeroThree_IsInvariant");
            yield return new TestCaseData("0.001", 0.001f).SetName("TryParse_TheMinimum_Parses");
            yield return new TestCaseData("10", 10f).SetName("TryParse_TheMaximum_Parses");
        }

        [TestCaseSource(nameof(GoodTextCases))]
        [SetCulture("de-DE")]
        public void TryParse_Valid(string text, float expected)
        {
            Assert.That(ServerMeshShipScale.TryParse(text, out float factor, out string error), Is.True);
            Assert.That(factor, Is.EqualTo(expected));
            Assert.That(error, Is.Null);
        }

        private static IEnumerable<TestCaseData> ScaledCases()
        {
            (string name, Func<ServerMeshShipScale, float> read, float value)[] rows =
            {
                ("VisibleRange", s => s.VisibleRange, 2000f),
                ("DetectionRange", s => s.DetectionRange, 2200f),
                ("TargetDropRange", s => s.TargetDropRange, 2860f),
                ("MaxFiringDistance", s => s.MaxFiringDistance, 1000f),
                ("MinEngageDistance", s => s.MinEngageDistance, 150f),
                ("EvadeDistance", s => s.EvadeDistance, 600f),
                ("AvoidanceSteerDistance", s => s.AvoidanceSteerDistance, 200f),
                ("AvoidanceRadius", s => s.AvoidanceRadius, 8f),
                ("AvoidanceMinDistance", s => s.AvoidanceMinDistance, 30f),
                ("PatrolRadius", s => s.PatrolRadius, 400f),
                ("ProjectileSpeed", s => s.ProjectileSpeed, 800f),
                ("ProjectileRange", s => s.ProjectileRange, 1000f),
                ("SnapDistance", s => s.SnapDistance, 50f),
                ("Force", s => s.Force, 400f),
                ("BoostForce", s => s.BoostForce, 800f),
                ("PathSemiMajorMin", s => s.PathSemiMajorMin, 200f),
                ("PathSemiMajorMax", s => s.PathSemiMajorMax, 280f),
                ("PathSpeedMin", s => s.PathSpeedMin, 50f),
                ("PathSpeedMax", s => s.PathSpeedMax, 125f),
                ("BotPathBudget", s => s.BotPathBudget, 0.5f),
                ("ColliderSizeX", s => s.ColliderSize.x, 8.4f),
                ("ColliderSizeY", s => s.ColliderSize.y, 3f),
                ("ColliderSizeZ", s => s.ColliderSize.z, 11.9f),
                ("ColliderCenterY", s => s.ColliderCenter.y, 0.4f),
                ("ColliderCenterZ", s => s.ColliderCenter.z, 3.4f),
                ("BarrelPositionX", s => s.BarrelPosition.x, 2f),
                ("BarrelPositionZ", s => s.BarrelPosition.z, 6f),
            };
            foreach (float factor in new[] { 0.03f, 1f })
                foreach ((string name, Func<ServerMeshShipScale, float> read, float value) in rows)
                    yield return new TestCaseData(factor, read, value).SetName($"{name}_AtScale{factor.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace(".", "_")}_IsBaseTimesFactor");
        }

        [TestCaseSource(nameof(ScaledCases))]
        public void ScaledQuantity_IsBaseTimesFactor(float factor, Func<ServerMeshShipScale, float> read, float value)
        {
            Assert.That(read(new ServerMeshShipScale(factor)), Is.EqualTo(value * factor).Within(1e-5f * Math.Max(1f, value)));
        }

        [TestCase(0.03f)]
        [TestCase(1f)]
        [TestCase(5f)]
        public void UnscaledQuantities_NeverChange(float factor)
        {
            ServerMeshShipScale scale = new ServerMeshShipScale(factor);

            Assert.That(scale.Factor, Is.EqualTo(factor));
            Assert.That(scale.RectangleInset, Is.EqualTo(0.1f));
            Assert.That(scale.ColliderCenter.x, Is.EqualTo(0f));
            Assert.That(ServerMeshShipScale.FireIntervalSeconds, Is.EqualTo(0.175f));
            Assert.That(ServerMeshShipScale.ScanIntervalSeconds, Is.EqualTo(0.5f));
            Assert.That(ServerMeshShipScale.RespawnSeconds, Is.EqualTo(10f));
            Assert.That(ServerMeshShipScale.SteeringTorques, Is.EqualTo(new Vector3(23f, 23f, 30f)));
            Assert.That(ServerMeshShipScale.LinearDrag, Is.EqualTo(3f));
            Assert.That(ServerMeshShipScale.AngularDrag, Is.EqualTo(8f));
            Assert.That(ServerMeshShipScale.SteeringGain, Is.EqualTo(2.5f));
            Assert.That(ServerMeshShipScale.RollLevelGain, Is.EqualTo(0.6f));
            Assert.That(ServerMeshShipScale.ObserverLerpSpeed, Is.EqualTo(10f));
            Assert.That(ServerMeshShipScale.PatrolRate, Is.EqualTo(0.05f));
            Assert.That(ServerMeshShipScale.PatrolThrottle, Is.EqualTo(0.4f));
            Assert.That(ServerMeshShipScale.AvoidThrottle, Is.EqualTo(0.7f));
            Assert.That(ServerMeshShipScale.ReturnThrottle, Is.EqualTo(0.7f));
            Assert.That(ServerMeshShipScale.Hull, Is.EqualTo(2500));
            Assert.That(ServerMeshShipScale.Shield, Is.EqualTo(2500f));
            Assert.That(ServerMeshShipScale.ShieldRechargeRate, Is.EqualTo(100f));
            Assert.That(ServerMeshShipScale.ShieldRechargeDelaySeconds, Is.EqualTo(2f));
            Assert.That(ServerMeshShipScale.BoltDamage, Is.EqualTo(50));
            Assert.That(ServerMeshShipScale.AimErrorDegrees, Is.EqualTo(2.5f));
            Assert.That(ServerMeshShipFlightPath.AltitudeMetres, Is.EqualTo(60f));
        }
    }
}
