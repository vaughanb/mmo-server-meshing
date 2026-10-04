using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipPilotDecisionTests
    {
        private const int Seed = 0x5348;

        private static ServerMeshShipPilotDecision NewDecision(float factor = 1f, int seed = Seed)
        {
            return new ServerMeshShipPilotDecision(new Random(seed), new ServerMeshShipScale(factor));
        }

        [Test]
        public void Constructor_NullArguments_ThrowNamingThem()
        {
            Assert.That(Assert.Throws<ArgumentNullException>(() => new ServerMeshShipPilotDecision(null, new ServerMeshShipScale(1f))).ParamName, Is.EqualTo("random"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => new ServerMeshShipPilotDecision(new Random(1), null)).ParamName, Is.EqualTo("scale"));
        }

        private static IEnumerable<TestCaseData> DroppedCases()
        {
            yield return new TestCaseData(1f, false, 100f).SetName("Decide_DeadTarget_IsDropped");
            yield return new TestCaseData(1f, true, 2860.5f).SetName("Decide_TargetBeyondTheDropRange_IsDropped");
            yield return new TestCaseData(0.03f, true, 2860f * 0.03f + 0.01f).SetName("Decide_TargetBeyondTheScaledDropRange_IsDropped");
            yield return new TestCaseData(1f, true, float.NaN).SetName("Decide_TargetAtAnUnknownDistance_IsDropped");
        }

        [TestCaseSource(nameof(DroppedCases))]
        public void Decide_LostTarget_IsDroppedForThatCallAndPatrols(float factor, bool alive, float distance)
        {
            ServerMeshShipPilotDecision decision = NewDecision(factor);

            ServerMeshShipPilotTask task = decision.Decide(1d, false, false, true, alive, distance);

            Assert.That(task, Is.EqualTo(ServerMeshShipPilotTask.Patrol));
            Assert.That(decision.TargetDropped, Is.True);
            Assert.That(decision.Decide(1.1d, false, false, false, false, 0f), Is.EqualTo(ServerMeshShipPilotTask.Patrol));
            Assert.That(decision.TargetDropped, Is.False);
        }

        [Test]
        public void Decide_TargetAtTheScaledDropRange_IsKept()
        {
            ServerMeshShipPilotDecision decision = NewDecision(0.03f);

            ServerMeshShipPilotTask task = decision.Decide(1d, false, false, true, true, 2860f * 0.03f);

            Assert.That(decision.TargetDropped, Is.False);
            Assert.That(task, Is.Not.EqualTo(ServerMeshShipPilotTask.Patrol));
        }

        [Test]
        public void Decide_NoTarget_Patrols()
        {
            ServerMeshShipPilotDecision decision = NewDecision();

            Assert.That(decision.Decide(0d, false, false, false, true, 10f), Is.EqualTo(ServerMeshShipPilotTask.Patrol));
            Assert.That(decision.TargetDropped, Is.False);
        }

        private static IEnumerable<TestCaseData> PriorityCases()
        {
            yield return new TestCaseData(true, true, true, ServerMeshShipPilotTask.Avoid).SetName("Decide_ObstacleAhead_BeatsEverything");
            yield return new TestCaseData(true, false, false, ServerMeshShipPilotTask.Avoid).SetName("Decide_ObstacleAheadWithoutATarget_Avoids");
            yield return new TestCaseData(false, true, true, ServerMeshShipPilotTask.Return).SetName("Decide_OutsideTheRectangle_BeatsCombat");
            yield return new TestCaseData(false, true, false, ServerMeshShipPilotTask.Return).SetName("Decide_OutsideTheRectangle_BeatsPatrol");
        }

        [TestCaseSource(nameof(PriorityCases))]
        public void Decide_Priority(bool obstacle, bool outside, bool target, ServerMeshShipPilotTask expected)
        {
            Assert.That(NewDecision().Decide(5d, obstacle, outside, target, true, 500f), Is.EqualTo(expected));
        }

        [Test]
        public void Decide_FirstCombatFrame_TurnsToEvadeAsTheSpacePilotDoes()
        {
            Assert.That(NewDecision().Decide(0d, false, false, true, true, 500f), Is.EqualTo(ServerMeshShipPilotTask.Evade));
        }

        [Test]
        public void Decide_TargetUnderTheEngageDistance_ForcesEvade()
        {
            ServerMeshShipPilotDecision decision = NewDecision(0.03f);
            decision.Decide(0d, false, false, true, true, 500f * 0.03f);
            Assert.That(decision.Decide(10.5d, false, false, true, true, 500f * 0.03f), Is.EqualTo(ServerMeshShipPilotTask.Attack));

            ServerMeshShipPilotTask task = decision.Decide(10.6d, false, false, true, true, 150f * 0.03f - 0.01f);

            Assert.That(task, Is.EqualTo(ServerMeshShipPilotTask.Evade));
            Assert.That(decision.Decide(10.7d, false, false, true, true, 500f * 0.03f), Is.EqualTo(ServerMeshShipPilotTask.Evade));
        }

        private static List<(ServerMeshShipPilotTask task, float yaw, float pitch)> Run(int seed)
        {
            ServerMeshShipPilotDecision decision = NewDecision(1f, seed);
            List<(ServerMeshShipPilotTask, float, float)> tasks = new List<(ServerMeshShipPilotTask, float, float)>();
            for (int step = 0; step <= 600; ++step)
                tasks.Add((decision.Decide(step * 0.1d, false, false, true, true, 500f), decision.EvadeYaw, decision.EvadePitch));
            return tasks;
        }

        [Test]
        public void Decide_OneSeed_GivesIdenticalSequencesOverSixtySeconds()
        {
            Assert.That(Run(Seed), Is.EqualTo(Run(Seed)));
            Assert.That(Run(Seed), Is.Not.EqualTo(Run(Seed + 1)));
        }

        [Test]
        public void Decide_CombatTimers_StayInTheirRanges()
        {
            List<(ServerMeshShipPilotTask task, float yaw, float pitch)> tasks = Run(Seed);
            int runStart = 0;
            int completed = 0;
            for (int i = 1; i < tasks.Count; ++i)
            {
                if (tasks[i].task == tasks[i - 1].task)
                    continue;
                double seconds = (i - runStart) * 0.1d;
                if (tasks[i - 1].task == ServerMeshShipPilotTask.Attack)
                    Assert.That(seconds, Is.InRange(8d - 0.11d, 10d + 0.11d), $"attack ending at step {i}");
                else
                    Assert.That(seconds, Is.InRange(5d - 0.11d, 10d + 0.11d), $"evade ending at step {i}");
                runStart = i;
                completed++;
            }
            Assert.That(completed, Is.GreaterThanOrEqualTo(4));
            foreach ((ServerMeshShipPilotTask task, float yaw, float pitch) in tasks)
            {
                if (task != ServerMeshShipPilotTask.Evade)
                    continue;
                Assert.That(Math.Abs(yaw), Is.InRange(60f, 120f));
                Assert.That(pitch, Is.InRange(-30f, 30f));
            }
        }

        [Test]
        public void Decide_EvadeDirection_IsRerolledEveryOneToFourSeconds()
        {
            ServerMeshShipPilotDecision decision = NewDecision();
            decision.Decide(0d, false, false, true, true, 500f);
            float yaw = decision.EvadeYaw;
            int rolls = 0;
            double lastRoll = 0d;
            for (int step = 1; step <= 50; ++step)
            {
                double now = step * 0.1d;
                if (decision.Decide(now, false, false, true, true, 500f) != ServerMeshShipPilotTask.Evade)
                    break;
                if (decision.EvadeYaw != yaw)
                {
                    Assert.That(now - lastRoll, Is.InRange(1d - 0.11d, 4d + 0.11d));
                    yaw = decision.EvadeYaw;
                    lastRoll = now;
                    rolls++;
                }
            }
            Assert.That(rolls, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Bursting_OnAndOffDurations_StayInRange()
        {
            ServerMeshShipPilotDecision decision = NewDecision();
            bool previous = decision.Bursting(0d);
            Assert.That(previous, Is.True);
            int runStart = 0;
            int on = 0, off = 0;
            for (int step = 1; step <= 12000; ++step)
            {
                bool bursting = decision.Bursting(step * 0.01d);
                if (bursting == previous)
                    continue;
                double seconds = (step - runStart) * 0.01d;
                if (previous)
                {
                    Assert.That(seconds, Is.InRange(1d - 0.011d, 3d + 0.011d));
                    on++;
                }
                else
                {
                    Assert.That(seconds, Is.InRange(0.5d - 0.011d, 2d + 0.011d));
                    off++;
                }
                runStart = step;
                previous = bursting;
            }
            Assert.That(on, Is.GreaterThan(10));
            Assert.That(off, Is.GreaterThan(10));
        }

        [TestCase(1f, 0f, 0.35f)]
        [TestCase(1f, 200f, 0.5f)]
        [TestCase(1f, 400f, 1f)]
        [TestCase(1f, 5000f, 1f)]
        [TestCase(0.03f, 6f, 0.5f)]
        [TestCase(1f, float.NaN, 0.35f)]
        public void AttackThrottle_IsTheClampedScaledShare(float factor, float distance, float expected)
        {
            Assert.That(NewDecision(factor).AttackThrottle(distance), Is.EqualTo(expected).Within(1e-5f));
        }

        [Test]
        public void FireCone_IsSevenDegrees()
        {
            Assert.That(ServerMeshShipPilotDecision.FireConeDegrees, Is.EqualTo(7f));
        }
    }
}
