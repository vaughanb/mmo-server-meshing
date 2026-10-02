// Server Meshing add-on — tests for the rig's AOI timing: the float mirror of the kit grid AOI's countdown, and the timed AOI's shape.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>ServerMeshAoiCountdownTests checks the countdown mirror against the kit grid AOI's float arithmetic.</summary>
    public class ServerMeshAoiCountdownTests
    {
        private const float TwentyHertz = (float)(1.0 / 20);

        [Test]
        public void Step_SystemNotReady_NeverDueAndCountdownUntouched()
        {
            ServerMeshAoiCountdown countdown = new ServerMeshAoiCountdown();

            for (int call = 0; call < 100; ++call)
                Assert.That(countdown.Step(TwentyHertz, 1f, false), Is.False, $"call {call}");

            Assert.That(countdown.Remaining, Is.EqualTo(0f));
            Assert.That(countdown.Step(TwentyHertz, 1f, true), Is.True);
        }

        [Test]
        public void Step_SystemLostAfterAPass_ResumesWhereItStopped()
        {
            ServerMeshAoiCountdown countdown = new ServerMeshAoiCountdown();
            Assert.That(countdown.Step(TwentyHertz, 1f, true), Is.True);

            for (int call = 0; call < 50; ++call)
                Assert.That(countdown.Step(TwentyHertz, 1f, false), Is.False, $"call {call}");

            Assert.That(countdown.Remaining, Is.EqualTo(1f));
            Assert.That(DueCalls(countdown, TwentyHertz, 1f, 20), Is.EqualTo(new[] { 19 }));
        }

        private static IEnumerable<TestCaseData> ScheduleCases()
        {
            yield return new TestCaseData(0f, 1f, 5, new[] { 0 }).SetName("Step_ZeroDelta_OnlyTheFirstReadyCallIsDue");
            yield return new TestCaseData(TwentyHertz, 1f, 60, new[] { 0, 20, 40 }).SetName("Step_TwentyHertzOneSecond_OnePassInTwentyCalls");
            yield return new TestCaseData(TwentyHertz, 0.25f, 60, new[] { 0, 6, 12, 18, 24, 30, 36, 42, 48, 54 })
                .SetName("Step_TwentyHertzQuarterSecond_EverySixCallsNotFive");
        }

        [TestCaseSource(nameof(ScheduleCases))]
        public void Step_Schedule(float deltaTime, float interval, int calls, int[] expectedDue)
        {
            Assert.That(DueCalls(new ServerMeshAoiCountdown(), deltaTime, interval, calls), Is.EqualTo(expectedDue));
        }

        [Test]
        public void ForceDue_SystemNotReady_StaysDueUntilReady()
        {
            ServerMeshAoiCountdown countdown = new ServerMeshAoiCountdown();
            Assert.That(countdown.Step(TwentyHertz, 1f, true), Is.True);

            countdown.ForceDue();

            Assert.That(countdown.Step(0f, 1f, false), Is.False);
            Assert.That(countdown.Step(0f, 1f, true), Is.True);
        }

        [Test]
        public void ForceDue_AfterAPass_NextCallIsDueEvenAtZeroDelta()
        {
            ServerMeshAoiCountdown countdown = new ServerMeshAoiCountdown();
            Assert.That(countdown.Step(TwentyHertz, 1f, true), Is.True);
            Assert.That(countdown.Step(0f, 1f, true), Is.False);

            countdown.ForceDue();

            Assert.That(countdown.Step(0f, 1f, true), Is.True);
            Assert.That(countdown.Remaining, Is.EqualTo(1f));
            Assert.That(countdown.Step(TwentyHertz, 1f, true), Is.False);
        }

        [Test]
        public void TimedAoi_KitSystemField_IsAReferenceTypeField()
        {
            Assert.That(ServerMeshTimedAoi.SystemField, Is.Not.Null);
        }

        [Test]
        public void TimedAoi_DeclaresOnlyTheTwoUpdateOverrides()
        {
            string[] declared = typeof(ServerMeshTimedAoi)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName)
                .Select(m => m.Name)
                .OrderBy(n => n)
                .ToArray();

            Assert.That(declared, Is.EqualTo(new[] { "UpdateInterestManagement", "UpdateInterestManagementImmediate" }));
        }

        [Test]
        public void LateStamps_ExecutionOrders_BracketTheTimedAoi()
        {
            Assert.That(OrderOf(typeof(ServerMeshAoiLateBegin)), Is.EqualTo(29999));
            Assert.That(OrderOf(typeof(ServerMeshTimedAoi)), Is.EqualTo(30000));
            Assert.That(OrderOf(typeof(ServerMeshAoiLateEnd)), Is.EqualTo(30001));
        }

        private static int[] DueCalls(ServerMeshAoiCountdown countdown, float deltaTime, float interval, int calls)
        {
            List<int> due = new List<int>();
            for (int call = 0; call < calls; ++call)
            {
                if (countdown.Step(deltaTime, interval, true))
                    due.Add(call);
            }
            return due.ToArray();
        }

        private static int OrderOf(System.Type type)
        {
            DefaultExecutionOrder attribute = type.GetCustomAttribute<DefaultExecutionOrder>(false);
            Assert.That(attribute, Is.Not.Null, type.Name);
            return attribute.order;
        }
    }
}
