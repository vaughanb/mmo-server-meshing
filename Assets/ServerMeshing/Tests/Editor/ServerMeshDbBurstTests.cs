using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshDbBurstTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("50").SetName("TryParseSetting_NoAt_Fails");
            yield return new TestCaseData("@10").SetName("TryParseSetting_NoCount_Fails");
            yield return new TestCaseData("50@").SetName("TryParseSetting_NoStart_Fails");
            yield return new TestCaseData("50@10@20").SetName("TryParseSetting_TwoAts_Fails");
            yield return new TestCaseData("0@10").SetName("TryParseSetting_ZeroArrivals_Fails");
            yield return new TestCaseData("-5@10").SetName("TryParseSetting_NegativeArrivals_Fails");
            yield return new TestCaseData("10000@10").SetName("TryParseSetting_ArrivalsAboveMax_Fails");
            yield return new TestCaseData("2.5@10").SetName("TryParseSetting_FractionalArrivals_Fails");
            yield return new TestCaseData("x@10").SetName("TryParseSetting_TextArrivals_Fails");
            yield return new TestCaseData("50@-1").SetName("TryParseSetting_NegativeStart_Fails");
            yield return new TestCaseData("50@x").SetName("TryParseSetting_TextStart_Fails");
            yield return new TestCaseData("50@NaN").SetName("TryParseSetting_NaNStart_Fails");
            yield return new TestCaseData("50@1e3").SetName("TryParseSetting_ExponentStart_Fails");
            yield return new TestCaseData("50@3601").SetName("TryParseSetting_StartAboveMax_Fails");
            yield return new TestCaseData("50@10+40").SetName("TryParseSetting_DurationWithoutRate_Fails");
            yield return new TestCaseData("5/s@10").SetName("TryParseSetting_RateWithoutDuration_Fails");
            yield return new TestCaseData("5/s@10+").SetName("TryParseSetting_EmptyDuration_Fails");
            yield return new TestCaseData("5/s@10+0").SetName("TryParseSetting_ZeroDuration_Fails");
            yield return new TestCaseData("5/s@10+2.5").SetName("TryParseSetting_FractionalDuration_Fails");
            yield return new TestCaseData("5/s@10+40+5").SetName("TryParseSetting_TwoPluses_Fails");
            yield return new TestCaseData("0/s@10+40").SetName("TryParseSetting_ZeroRate_Fails");
            yield return new TestCaseData("/s@10+40").SetName("TryParseSetting_EmptyRate_Fails");
            yield return new TestCaseData("5/m@10+40").SetName("TryParseSetting_RateNotPerSecond_Fails");
            yield return new TestCaseData("100/s@10+100").SetName("TryParseSetting_SustainedAboveMax_Fails");
            yield return new TestCaseData("{5}@10").SetName("TryParseSetting_Braces_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshDbBurst.TryParseSetting(text, out ServerMeshDbBurst.Plan plan, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(plan.Total, Is.EqualTo(0));
            StringAssert.Contains(ServerMeshDbBurst.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("50@10", 50, 0, 0, 10.0).SetName("TryParseSetting_AtOnce_Parses");
            yield return new TestCaseData(" 200 @ 12.5 ", 200, 0, 0, 12.5).SetName("TryParseSetting_AtOnceWithSpaces_Parses");
            yield return new TestCaseData("1@0", 1, 0, 0, 0.0).SetName("TryParseSetting_OneAtZero_Parses");
            yield return new TestCaseData("9999@3600", 9999, 0, 0, 3600.0).SetName("TryParseSetting_AtOnceAtTheMaxima_Parses");
            yield return new TestCaseData("5/s@10+40", 200, 5, 40, 10.0).SetName("TryParseSetting_Sustained_Parses");
            yield return new TestCaseData("20 /s @ 15 + 40", 800, 20, 40, 15.0).SetName("TryParseSetting_SustainedWithSpaces_Parses");
            yield return new TestCaseData("99/s@0+101", 9999, 99, 101, 0.0).SetName("TryParseSetting_SustainedAtTheMax_Parses");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParseSetting_Valid(string text, int total, int rate, int duration, double start)
        {
            bool parsed = ServerMeshDbBurst.TryParseSetting(text, out ServerMeshDbBurst.Plan plan, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(plan.Total, Is.EqualTo(total));
            Assert.That(plan.Rate, Is.EqualTo(rate));
            Assert.That(plan.DurationSeconds, Is.EqualTo(duration));
            Assert.That(plan.StartSeconds, Is.EqualTo(start));
            Assert.That(plan.Sustained, Is.EqualTo(rate > 0));
        }

        [TestCase(1, "meshburst0001", "meshburst0001Char")]
        [TestCase(42, "meshburst0042", "meshburst0042Char")]
        [TestCase(800, "meshburst0800", "meshburst0800Char")]
        [TestCase(9999, "meshburst9999", "meshburst9999Char")]
        public void Names_AreFourDigitRigNames(int index, string account, string character)
        {
            Assert.That(ServerMeshDbBurst.AccountName(index), Is.EqualTo(account));
            Assert.That(ServerMeshDbBurst.CharacterName(index), Is.EqualTo(character));
        }

        private static IEnumerable<TestCaseData> DueCases()
        {
            ServerMeshDbBurst.Plan atOnce = new ServerMeshDbBurst.Plan(50, 0, 0, 10);
            ServerMeshDbBurst.Plan sustained = new ServerMeshDbBurst.Plan(200, 5, 40, 10);
            yield return new TestCaseData(atOnce, -0.1, 0).SetName("DueCount_BeforeTheStart_IsZero");
            yield return new TestCaseData(atOnce, 0.0, 50).SetName("DueCount_AtOnce_IsEveryArrival");
            yield return new TestCaseData(atOnce, 100.0, 50).SetName("DueCount_AtOnceLater_IsEveryArrival");
            yield return new TestCaseData(sustained, 0.0, 1).SetName("DueCount_SustainedAtTheStart_IsOne");
            yield return new TestCaseData(sustained, 0.19, 1).SetName("DueCount_SustainedJustBeforeTheSecond_IsOne");
            yield return new TestCaseData(sustained, 0.2, 2).SetName("DueCount_SustainedAtOneFifth_IsTwo");
            yield return new TestCaseData(sustained, 1.0, 6).SetName("DueCount_SustainedAfterOneSecond_IsRatePlusOne");
            yield return new TestCaseData(sustained, 39.9, 200).SetName("DueCount_SustainedLastArrivalDue_IsTotal");
            yield return new TestCaseData(sustained, 1000.0, 200).SetName("DueCount_SustainedLongAfter_IsTotal");
            yield return new TestCaseData(sustained, double.NaN, 0).SetName("DueCount_NaN_IsZero");
        }

        [TestCaseSource(nameof(DueCases))]
        public void DueCount(ServerMeshDbBurst.Plan plan, double elapsed, int expected)
        {
            Assert.That(ServerMeshDbBurst.DueCount(plan, elapsed), Is.EqualTo(expected));
        }

        [TestCase(0L, 1000L, 0L)]
        [TestCase(-5L, 1000L, 0L)]
        [TestCase(1500L, 1000L, 1500L)]
        [TestCase(10_000_000L, 10_000_000L, 1000L)]
        [TestCase(15_000L, 10_000_000L, 2L)]
        [TestCase(14_999L, 10_000_000L, 1L)]
        [TestCase(100L, 0L, 0L)]
        public void Milliseconds_RoundsTicks(long ticks, long frequency, long expected)
        {
            Assert.That(ServerMeshDbBurst.Milliseconds(ticks, frequency), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> PercentileCases()
        {
            yield return new TestCaseData(new int[0], 0, 0.5, 0).SetName("Percentile_Empty_IsZero");
            yield return new TestCaseData(null, 3, 0.5, 0).SetName("Percentile_Null_IsZero");
            yield return new TestCaseData(new[] { 7 }, 1, 0.95, 7).SetName("Percentile_OneValue_IsIt");
            yield return new TestCaseData(new[] { 1, 2, 3, 4 }, 4, 0.5, 2).SetName("Percentile_P50OfFour_IsTheSecond");
            yield return new TestCaseData(new[] { 1, 2, 3, 4 }, 4, 0.95, 4).SetName("Percentile_P95OfFour_IsTheLast");
            yield return new TestCaseData(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 }, 20, 0.95, 19)
                .SetName("Percentile_P95OfTwenty_IsTheNineteenth");
            yield return new TestCaseData(new[] { 1, 2, 3, 99, 99 }, 3, 1.0, 3).SetName("Percentile_OnlyTheFirstCountValues");
            yield return new TestCaseData(new[] { 5, 6 }, 9, 1.0, 6).SetName("Percentile_CountPastTheArray_IsClamped");
            yield return new TestCaseData(new[] { 5, 6 }, 2, 0.0, 5).SetName("Percentile_ShareZero_IsTheFirst");
        }

        [TestCaseSource(nameof(PercentileCases))]
        public void Percentile_IsNearestRank(int[] ascending, int count, double share, int expected)
        {
            Assert.That(ServerMeshDbBurst.Percentile(ascending, count, share), Is.EqualTo(expected));
        }

        [Test]
        public void ReadyLine_MatchesTheContract()
        {
            Assert.That(ServerMeshDbBurst.ReadyLine(7, 3, 2, 1, 5, 11), Is.EqualTo("DB burst 7: save 3 token 2 get 1 bg 5 ready 11 ms ok"));
        }

        [Test]
        public void ReadyLine_NegativesAreZero()
        {
            Assert.That(ServerMeshDbBurst.ReadyLine(1, -3, 0, 0, 0, -1), Is.EqualTo("DB burst 1: save 0 token 0 get 0 bg 0 ready 0 ms ok"));
        }

        [TestCase("save", "DB burst 12: failed save after 40 ms")]
        [TestCase("token", "DB burst 12: failed token after 40 ms")]
        [TestCase("get", "DB burst 12: failed get after 40 ms")]
        [TestCase("bg", "DB burst 12: failed bg after 40 ms")]
        public void FailedLine_MatchesTheContract(string step, string expected)
        {
            Assert.That(ServerMeshDbBurst.FailedLine(12, step, 40), Is.EqualTo(expected));
        }

        [Test]
        public void WindowLine_MatchesTheContract()
        {
            string line = ServerMeshDbBurst.WindowLine(5.004, 50, 48, 2, 120, 300, 310);

            Assert.That(line, Is.EqualTo("DB burst window 5.00 s: started 50 ready 48 failed 2 | ready p50 120 p95 300 max 310 ms"));
        }

        [Test]
        public void WindowLine_NoReadyArrival_IsNotAvailable()
        {
            string line = ServerMeshDbBurst.WindowLine(double.NaN, 0, 0, 0, 0, 0, 0);

            Assert.That(line, Is.EqualTo("DB burst window 0.00 s: started 0 ready 0 failed 0 | ready p50 n/a p95 n/a max n/a ms"));
        }

        [Test]
        public void StartLine_AtOnce_MatchesTheContract()
        {
            Assert.That(ServerMeshDbBurst.StartLine(new ServerMeshDbBurst.Plan(200, 0, 0, 10), 10), Is.EqualTo("DB burst: 200 arrivals at once at 10 s"));
        }

        [Test]
        public void StartLine_Sustained_MatchesTheContract()
        {
            Assert.That(ServerMeshDbBurst.StartLine(new ServerMeshDbBurst.Plan(200, 5, 40, 10), 12.257), Is.EqualTo("DB burst: 5/s for 40 s from 12.26 s"));
        }

        [Test]
        public void DoneLine_MatchesTheContract()
        {
            Assert.That(ServerMeshDbBurst.DoneLine(198, 2), Is.EqualTo("DB burst: done, 198 ready 2 failed"));
        }

        [Test]
        public void Lines_AreInvariantUnderACommaCulture()
        {
            System.Globalization.CultureInfo saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.That(ServerMeshDbBurst.WindowLine(5.5, 1000, 1000, 0, 1, 2, 3), Does.StartWith("DB burst window 5.50 s: started 1000 "));
                Assert.That(ServerMeshDbBurst.StartLine(new ServerMeshDbBurst.Plan(5, 5, 1, 0), 1.5), Is.EqualTo("DB burst: 5/s for 1 s from 1.5 s"));
                Assert.That(ServerMeshDbBurst.TryParseSetting("5@2.5", out ServerMeshDbBurst.Plan plan, out _), Is.True);
                Assert.That(plan.StartSeconds, Is.EqualTo(2.5));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }
    }
}
