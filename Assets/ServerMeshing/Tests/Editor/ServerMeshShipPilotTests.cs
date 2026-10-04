using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipPilotTests
    {
        private static IEnumerable<TestCaseData> NoSolutionCases()
        {
            yield return new TestCaseData(0f, new Vector3(0f, 0f, 100f), Vector3.zero).SetName("InterceptPoint_ZeroSpeed_NoSolution");
            yield return new TestCaseData(float.PositiveInfinity, new Vector3(0f, 0f, 100f), Vector3.zero).SetName("InterceptPoint_InfiniteSpeed_NoSolution");
            yield return new TestCaseData(float.NaN, new Vector3(0f, 0f, 100f), Vector3.zero).SetName("InterceptPoint_NaNSpeed_NoSolution");
            yield return new TestCaseData(100f, new Vector3(0f, 0f, 100f), new Vector3(0f, 0f, 200f)).SetName("InterceptPoint_TargetOutrunsTheBolt_NoSolution");
            yield return new TestCaseData(100f, new Vector3(0f, 0f, 100f), new Vector3(100f, 0f, 0f)).SetName("InterceptPoint_EqualSpeeds_NoSolution");
        }

        [TestCaseSource(nameof(NoSolutionCases))]
        public void InterceptPoint_NoSolution_ReturnsTheTargetPosition(float speed, Vector3 target, Vector3 velocity)
        {
            bool solved = ServerMeshShipPilot.InterceptPoint(Vector3.zero, speed, target, velocity, out Vector3 point);

            Assert.That(solved, Is.False);
            Assert.That(point, Is.EqualTo(target));
        }

        [Test]
        public void InterceptPoint_StationaryTarget_IsTheTarget()
        {
            Vector3 target = new Vector3(30f, 0f, 40f);

            bool solved = ServerMeshShipPilot.InterceptPoint(new Vector3(1f, 2f, 3f), 800f, target, Vector3.zero, out Vector3 point);

            Assert.That(solved, Is.True);
            Assert.That(Vector3.Distance(point, target), Is.LessThan(1e-4f));
        }

        [Test]
        public void InterceptPoint_CrossingTarget_MeetsTheBoltAtTheSameTime()
        {
            Vector3 shooter = new Vector3(5f, 0f, -5f);
            Vector3 target = new Vector3(0f, 0f, 500f);
            Vector3 velocity = new Vector3(60f, 0f, 0f);

            bool solved = ServerMeshShipPilot.InterceptPoint(shooter, 800f, target, velocity, out Vector3 point);

            float targetTime = Vector3.Distance(point, target) / velocity.magnitude;
            float boltTime = Vector3.Distance(point, shooter) / 800f;
            Assert.That(solved, Is.True);
            Assert.That(point.x, Is.GreaterThan(0f));
            Assert.That(boltTime, Is.EqualTo(targetTime).Within(1e-3f));
        }

        [Test]
        public void ApplyAimError_NullRandom_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipPilot.ApplyAimError(Vector3.forward, null, 2.5f));
            Assert.That(ex.ParamName, Is.EqualTo("random"));
        }

        [Test]
        public void ApplyAimError_SeededRun_StaysWithinTheMaximumAndIsRepeatable()
        {
            System.Random first = new System.Random(ServerMeshShipFlightPath.MixSeed(12345 ^ ServerMeshShipPilot.AimSalt, 3));
            System.Random second = new System.Random(ServerMeshShipFlightPath.MixSeed(12345 ^ ServerMeshShipPilot.AimSalt, 3));
            Vector3[] directions = { Vector3.forward, Vector3.up, new Vector3(1f, 0.2f, -3f), Vector3.down };
            float largest = 0f;
            for (int i = 0; i < 2000; ++i)
            {
                Vector3 direction = directions[i % directions.Length];
                Vector3 bent = ServerMeshShipPilot.ApplyAimError(direction, first, ServerMeshShipPilot.AimErrorDegrees);
                Vector3 again = ServerMeshShipPilot.ApplyAimError(direction, second, ServerMeshShipPilot.AimErrorDegrees);
                float angle = Vector3.Angle(direction, bent);
                largest = Mathf.Max(largest, angle);
                Assert.That(angle, Is.LessThanOrEqualTo(ServerMeshShipPilot.AimErrorDegrees + 0.01f));
                Assert.That(bent.magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(again, Is.EqualTo(bent));
            }
            Assert.That(largest, Is.GreaterThan(2f));
        }

        [Test]
        public void InsetRectangle_ShrinksEachSideByTheShareOfItsAxis()
        {
            Rect inset = ServerMeshShipPilot.InsetRectangle(Rect.MinMaxRect(-300f, -100f, 300f, 100f), 0.1f);

            Assert.That(inset.xMin, Is.EqualTo(-240f).Within(1e-4f));
            Assert.That(inset.xMax, Is.EqualTo(240f).Within(1e-4f));
            Assert.That(inset.yMin, Is.EqualTo(-80f).Within(1e-4f));
            Assert.That(inset.yMax, Is.EqualTo(80f).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> OutsideCases()
        {
            yield return new TestCaseData(new Vector3(120f, 60f, 0f), Vector3.zero, true).SetName("IsOutsideRectangle_PastTheEdge_Outside");
            yield return new TestCaseData(new Vector3(90f, 60f, 0f), new Vector3(5f, 0f, 0f), true).SetName("IsOutsideRectangle_LeavingWithinTheHorizon_Outside");
            yield return new TestCaseData(new Vector3(90f, 60f, 0f), new Vector3(-5f, 0f, 0f), false).SetName("IsOutsideRectangle_InsideHeadingIn_Inside");
            yield return new TestCaseData(new Vector3(0f, 500f, 0f), new Vector3(0f, 50f, 0f), false).SetName("IsOutsideRectangle_AltitudeIgnored_Inside");
        }

        [TestCaseSource(nameof(OutsideCases))]
        public void IsOutsideRectangle(Vector3 position, Vector3 velocity, bool expected)
        {
            Rect rect = Rect.MinMaxRect(-100f, -100f, 100f, 100f);

            Assert.That(ServerMeshShipPilot.IsOutsideRectangle(rect, position, velocity, ServerMeshShipPilot.ReturnHorizonSeconds), Is.EqualTo(expected));
        }
    }
}
