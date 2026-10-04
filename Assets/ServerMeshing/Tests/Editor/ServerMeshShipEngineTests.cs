using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipEngineTests
    {
        private static IEnumerable<TestCaseData> BadArgumentCases()
        {
            foreach (float bad in new[] { 0f, -1f, float.NaN })
            {
                string shown = float.IsNaN(bad) ? "NaN" : bad == 0f ? "Zero" : "Negative";
                yield return new TestCaseData(bad, 3f, 0.02f, 1f, "force").SetName($"TerminalSpeed_{shown}Force_ThrowsNamingIt");
                yield return new TestCaseData(400f, bad, 0.02f, 1f, "drag").SetName($"TerminalSpeed_{shown}Drag_ThrowsNamingIt");
                yield return new TestCaseData(400f, 3f, bad, 1f, "fixedDeltaTime").SetName($"TerminalSpeed_{shown}DeltaTime_ThrowsNamingIt");
                yield return new TestCaseData(400f, 3f, 0.02f, bad, "mass").SetName($"TerminalSpeed_{shown}Mass_ThrowsNamingIt");
            }
        }

        [TestCaseSource(nameof(BadArgumentCases))]
        public void TerminalSpeed_NonPositiveArgument_ThrowsNamingIt(float force, float drag, float dt, float mass, string name)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipEngine.TerminalSpeed(force, drag, dt, mass));
            ArgumentOutOfRangeException steady = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipEngine.SteadySpeed(force, drag, dt, mass, 10));

            Assert.That(thrown.ParamName, Is.EqualTo(name));
            Assert.That(steady.ParamName, Is.EqualTo(name));
        }

        [Test]
        public void SteadySpeed_NegativeSteps_ThrowsNamingThem()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshShipEngine.SteadySpeed(400f, 3f, 0.02f, 1f, -1));

            Assert.That(thrown.ParamName, Is.EqualTo("steps"));
        }

        [Test]
        public void Constructor_NullScale_ThrowsNamingIt()
        {
            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => new ServerMeshShipEngine(null));

            Assert.That(thrown.ParamName, Is.EqualTo("scale"));
        }

        [TestCase(0f)]
        [TestCase(-0.02f)]
        public void Step_NonPositiveDeltaTime_ThrowsNamingIt(float dt)
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(1f));

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => engine.Step(Vector3.zero, Vector3.forward, false, 0f, dt, out _, out _));

            Assert.That(thrown.ParamName, Is.EqualTo("fixedDeltaTime"));
        }

        private static IEnumerable<TestCaseData> TerminalCases()
        {
            yield return new TestCaseData(400f, 0.02f, 125.333f).SetName("TerminalSpeed_SpaceRates_Is125_333");
            yield return new TestCaseData(400f * 0.03f, 0.02f, 3.760f).SetName("TerminalSpeed_SpaceRatesAtScale0_03_Is3_760");
            yield return new TestCaseData(400f, 0.03f, 121.333f).SetName("TerminalSpeed_RigStep_Is121_333");
            yield return new TestCaseData(800f, 0.02f, 250.667f).SetName("TerminalSpeed_Boost_Is250_667");
        }

        [TestCaseSource(nameof(TerminalCases))]
        public void TerminalSpeed_SpaceFormula(float force, float dt, float expected)
        {
            Assert.That(ServerMeshShipEngine.TerminalSpeed(force, 3f, dt, 1f), Is.EqualTo(expected).Within(0.001f));
        }

        [TestCase(0.02f)]
        [TestCase(0.03f)]
        public void SteadySpeed_ConvergesToTheTerminalSpeed(float dt)
        {
            float terminal = ServerMeshShipEngine.TerminalSpeed(400f, 3f, dt, 1f);

            float steady = ServerMeshShipEngine.SteadySpeed(400f, 3f, dt, 1f, 2000);

            Assert.That(Math.Abs(steady - terminal) / terminal, Is.LessThanOrEqualTo(0.0001f));
        }

        [Test]
        public void TopSpeed_IsTheTerminalSpeedOfTheScaledForce()
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(0.03f));

            Assert.That(engine.TopSpeed(0.02f, false), Is.EqualTo(3.760f).Within(0.001f));
            Assert.That(engine.TopSpeed(0.02f, true), Is.EqualTo(7.520f).Within(0.001f));
        }

        [Test]
        public void Step_ForceLerp_Reaches99PercentWithinTheBound()
        {
            const float Dt = 0.02f;
            int bound = (int)Math.Ceiling(Math.Log(100d) / (ServerMeshShipEngine.ResponseSpeed * Dt));
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(1f));
            int first = -1;

            for (int step = 1; step <= bound; ++step)
            {
                engine.Step(Vector3.zero, Vector3.forward, false, 0f, Dt, out _, out Vector3 force);
                if (first < 0 && force.z >= 0.99f * 400f)
                    first = step;
            }

            Assert.That(bound, Is.EqualTo(39));
            Assert.That(first, Is.EqualTo(37));
        }

        [Test]
        public void Step_Boost_RaisesTheForwardTargetAndHalvesSteering()
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(1f));

            for (int i = 0; i < 500; ++i)
                engine.Step(Vector3.zero, Vector3.zero, true, 0f, 0.02f, out _, out _);
            engine.Step(new Vector3(1f, 0f, 0f), Vector3.zero, true, 0f, 0.02f, out Vector3 torque, out Vector3 force);

            Assert.That(force.z, Is.EqualTo(800f).Within(0.01f));
            Assert.That(torque.x, Is.EqualTo(0.5f * 23f).Within(1e-4f));
        }

        private static IEnumerable<TestCaseData> CurveCases()
        {
            yield return new TestCaseData(0f, 1f).SetName("Step_AtRest_SteersAtFullAuthority");
            yield return new TestCaseData(0.5f, 0.9f).SetName("Step_AtHalfTopSpeed_SteersAtNinetyPercent");
            yield return new TestCaseData(1f, 0.8f).SetName("Step_AtTopSpeed_SteersAtEightyPercent");
            yield return new TestCaseData(2f, 0.8f).SetName("Step_BeyondTopSpeed_StaysAtEightyPercent");
        }

        [TestCaseSource(nameof(CurveCases))]
        public void Step_SteeringFollowsTheSpeedCurve(float shareOfTopSpeed, float multiplier)
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(1f));
            float top = engine.TopSpeed(0.02f, false);

            engine.Step(new Vector3(1f, -1f, 2f), Vector3.zero, false, shareOfTopSpeed * top, 0.02f, out Vector3 torque, out _);

            Assert.That(torque.x, Is.EqualTo(multiplier * 23f).Within(1e-4f));
            Assert.That(torque.y, Is.EqualTo(-multiplier * 23f).Within(1e-4f));
            Assert.That(torque.z, Is.EqualTo(multiplier * 30f).Within(1e-4f));
        }

        [Test]
        public void Reset_ZeroesTheAppliedForce()
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(1f));
            for (int i = 0; i < 10; ++i)
                engine.Step(Vector3.zero, Vector3.forward, false, 0f, 0.02f, out _, out _);

            engine.Reset();
            engine.Step(Vector3.zero, Vector3.zero, false, 0f, 0.02f, out _, out Vector3 force);

            Assert.That(force, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void Step_ScaledForce_FollowsTheScale()
        {
            ServerMeshShipEngine engine = new ServerMeshShipEngine(new ServerMeshShipScale(0.03f));

            for (int i = 0; i < 1000; ++i)
                engine.Step(Vector3.zero, new Vector3(0f, 0f, 3f), false, 0f, 0.02f, out _, out _);
            engine.Step(Vector3.zero, new Vector3(0f, 0f, 3f), false, 0f, 0.02f, out _, out Vector3 force);

            Assert.That(force.z, Is.EqualTo(400f * 0.03f).Within(1e-3f));
        }
    }
}
