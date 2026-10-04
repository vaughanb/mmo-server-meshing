using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipClassTests
    {
        private static readonly Vector3 Torques = new Vector3(23f, 23f, 30f);

        [Test]
        public void Constructor_NullName_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipClass(null, 400f, 800f, 3f, 8f, Torques, 2500));

            Assert.That(thrown.ParamName, Is.EqualTo("name"));
        }

        private static IEnumerable<TestCaseData> BadFieldCases()
        {
            yield return new TestCaseData("", 400f, 800f, 3f, 8f, Torques, 2500, "name").SetName("Constructor_EmptyName_ThrowsNamingIt");
            yield return new TestCaseData("  ", 400f, 800f, 3f, 8f, Torques, 2500, "name").SetName("Constructor_BlankName_ThrowsNamingIt");
            foreach (float bad in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                string shown = float.IsNaN(bad) ? "NaN" : float.IsInfinity(bad) ? "Infinite" : bad == 0f ? "Zero" : "Negative";
                yield return new TestCaseData("x", bad, 800f, 3f, 8f, Torques, 2500, "force").SetName($"Constructor_{shown}Force_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, bad, 3f, 8f, Torques, 2500, "boostForce").SetName($"Constructor_{shown}BoostForce_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, 800f, bad, 8f, Torques, 2500, "linearDrag").SetName($"Constructor_{shown}LinearDrag_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, 800f, 3f, bad, Torques, 2500, "angularDrag").SetName($"Constructor_{shown}AngularDrag_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, 800f, 3f, 8f, new Vector3(bad, 23f, 30f), 2500, "steeringTorques").SetName($"Constructor_{shown}PitchTorque_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, 800f, 3f, 8f, new Vector3(23f, bad, 30f), 2500, "steeringTorques").SetName($"Constructor_{shown}YawTorque_ThrowsNamingIt");
                yield return new TestCaseData("x", 400f, 800f, 3f, 8f, new Vector3(23f, 23f, bad), 2500, "steeringTorques").SetName($"Constructor_{shown}RollTorque_ThrowsNamingIt");
            }
            yield return new TestCaseData("x", 400f, 800f, 3f, 8f, Torques, 0, "hull").SetName("Constructor_ZeroHull_ThrowsNamingIt");
            yield return new TestCaseData("x", 400f, 800f, 3f, 8f, Torques, -1, "hull").SetName("Constructor_NegativeHull_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(BadFieldCases))]
        public void Constructor_BadField_ThrowsNamingIt(string name, float force, float boostForce, float linearDrag, float angularDrag, Vector3 steeringTorques, int hull, string field)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMeshShipClass(name, force, boostForce, linearDrag, angularDrag, steeringTorques, hull));

            Assert.That(thrown.ParamName, Is.EqualTo(field));
        }

        [Test]
        public void Base_IsShipPrefab()
        {
            ServerMeshShipClass shipClass = ServerMeshShipClass.Base;

            Assert.That(shipClass.Name, Is.EqualTo("base"));
            Assert.That(shipClass.Force, Is.EqualTo(400f));
            Assert.That(shipClass.BoostForce, Is.EqualTo(800f));
            Assert.That(shipClass.LinearDrag, Is.EqualTo(3f));
            Assert.That(shipClass.AngularDrag, Is.EqualTo(8f));
            Assert.That(shipClass.SteeringTorques, Is.EqualTo(new Vector3(23f, 23f, 30f)));
            Assert.That(shipClass.Hull, Is.EqualTo(2500));
        }

        [Test]
        public void Enemy_IsShipEnemyPrefabWithTheEngineAngularDrag()
        {
            ServerMeshShipClass shipClass = ServerMeshShipClass.Enemy;

            Assert.That(shipClass.Name, Is.EqualTo("enemy"));
            Assert.That(shipClass.Force, Is.EqualTo(300f));
            Assert.That(shipClass.BoostForce, Is.EqualTo(600f));
            Assert.That(shipClass.LinearDrag, Is.EqualTo(3f));
            Assert.That(shipClass.AngularDrag, Is.EqualTo(8f));
            Assert.That(shipClass.SteeringTorques, Is.EqualTo(new Vector3(8f, 8f, 10f)));
            Assert.That(shipClass.Hull, Is.EqualTo(1500));
        }

        [Test]
        public void Constructor_ValidFields_KeepsEach()
        {
            ServerMeshShipClass shipClass = new ServerMeshShipClass("probe", 1f, 2f, 0.5f, 0.25f, new Vector3(1f, 2f, 3f), 1);

            Assert.That(shipClass.Name, Is.EqualTo("probe"));
            Assert.That(shipClass.Force, Is.EqualTo(1f));
            Assert.That(shipClass.BoostForce, Is.EqualTo(2f));
            Assert.That(shipClass.LinearDrag, Is.EqualTo(0.5f));
            Assert.That(shipClass.AngularDrag, Is.EqualTo(0.25f));
            Assert.That(shipClass.SteeringTorques, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(shipClass.Hull, Is.EqualTo(1));
        }
    }
}
