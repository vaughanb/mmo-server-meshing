using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshRatesSettingTests
    {
        private const string ContractSample = "Rates: fps 30 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.02 s (project 0.03) | maxDeltaTime 0.333 | probe budget 33.33 ms | uncapped cells -";

        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData(null, "is empty").SetName("TryParse_Null_IsEmpty");
            yield return new TestCaseData("", "is empty").SetName("TryParse_Empty_IsEmpty");
            yield return new TestCaseData("   ", "is empty").SetName("TryParse_Whitespace_IsEmpty");
            yield return new TestCaseData("abc", "'abc' is not").SetName("TryParse_NoSlash_NamesTheText");
            yield return new TestCaseData("30", "'30' is not").SetName("TryParse_FpsWithoutStep_NamesTheText");
            yield return new TestCaseData("30/0.02/1", "'30/0.02/1' is not").SetName("TryParse_TwoSlashes_NamesTheText");
            yield return new TestCaseData("0/0.02", "fps part '0'").SetName("TryParse_ZeroFps_NamesTheFpsPart");
            yield return new TestCaseData("1001/0.02", "fps part '1001'").SetName("TryParse_FpsAboveOneThousand_NamesTheFpsPart");
            yield return new TestCaseData("-30/0.02", "fps part '-30'").SetName("TryParse_SignedFps_NamesTheFpsPart");
            yield return new TestCaseData("30.5/0.02", "fps part '30.5'").SetName("TryParse_FractionalFps_NamesTheFpsPart");
            yield return new TestCaseData("u0/0.02", "fps part 'u0'").SetName("TryParse_UncappedAtZeroFps_NamesTheFpsPart");
            yield return new TestCaseData("ux/0.02", "fps part 'ux'").SetName("TryParse_UncappedWithText_NamesTheFpsPart");
            yield return new TestCaseData("/0.02", "fps part ''").SetName("TryParse_NoFps_NamesTheFpsPart");
            yield return new TestCaseData("30/0", "step part '0'").SetName("TryParse_ZeroStep_NamesTheStepPart");
            yield return new TestCaseData("30/-0.02", "step part '-0.02'").SetName("TryParse_NegativeStep_NamesTheStepPart");
            yield return new TestCaseData("30/x", "step part 'x'").SetName("TryParse_TextStep_NamesTheStepPart");
            yield return new TestCaseData("30/", "step part ''").SetName("TryParse_NoStep_NamesTheStepPart");
            yield return new TestCaseData("30/2", "step part '2'").SetName("TryParse_StepAboveOneSecond_NamesTheStepPart");
            yield return new TestCaseData("30/NaN", "step part 'NaN'").SetName("TryParse_NaNStep_NamesTheStepPart");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParse_Invalid(string text, string expectedInError)
        {
            bool parsed = ServerMeshRatesSetting.TryParse(text, out ServerMeshRatesSetting setting, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(setting, Is.EqualTo(default(ServerMeshRatesSetting)));
            Assert.That(error, Does.Contain(expectedInError));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("30/0.02", 30, 30, 0.02f).SetName("TryParse_Capped_CapIsTheNominal");
            yield return new TestCaseData("60/0.03", 60, 60, 0.03f).SetName("TryParse_KitRates_Parse");
            yield return new TestCaseData("u/0.02", -1, 0, 0.02f).SetName("TryParse_BareU_NominalFromTheKit");
            yield return new TestCaseData("u30/0.02", -1, 30, 0.02f).SetName("TryParse_UncappedWithFps_KeepsTheNominal");
            yield return new TestCaseData(" 30 / 0.02 ", 30, 30, 0.02f).SetName("TryParse_WhitespaceAroundParts_Parses");
            yield return new TestCaseData("U30/0.02", -1, 30, 0.02f).SetName("TryParse_UpperCaseU_Parses");
            yield return new TestCaseData("u 30/0.02", -1, 30, 0.02f).SetName("TryParse_SpaceAfterU_Parses");
            yield return new TestCaseData("1000/1", 1000, 1000, 1f).SetName("TryParse_UpperLimits_Parse");
            yield return new TestCaseData("1/0.001", 1, 1, 0.001f).SetName("TryParse_LowerLimits_Parse");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParse_Valid(string text, int framesPerSecond, int nominalFramesPerSecond, float fixedStepSeconds)
        {
            bool parsed = ServerMeshRatesSetting.TryParse(text, out ServerMeshRatesSetting setting, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(setting.FramesPerSecond, Is.EqualTo(framesPerSecond));
            Assert.That(setting.NominalFramesPerSecond, Is.EqualTo(nominalFramesPerSecond));
            Assert.That(setting.FixedStepSeconds, Is.EqualTo(fixedStepSeconds));
            Assert.That(setting.IsUncapped, Is.EqualTo(framesPerSecond == ServerMeshRatesSetting.Uncapped));
        }

        private static IEnumerable<TestCaseData> ConstructorFailureCases()
        {
            yield return new TestCaseData(0, 0, 0.02f, "framesPerSecond").SetName("Constructor_ZeroFps_Throws");
            yield return new TestCaseData(-2, 0, 0.02f, "framesPerSecond").SetName("Constructor_NegativeFpsOtherThanUncapped_Throws");
            yield return new TestCaseData(1001, 0, 0.02f, "framesPerSecond").SetName("Constructor_FpsAboveOneThousand_Throws");
            yield return new TestCaseData(30, -1, 0.02f, "nominalFramesPerSecond").SetName("Constructor_NegativeNominal_Throws");
            yield return new TestCaseData(30, 1001, 0.02f, "nominalFramesPerSecond").SetName("Constructor_NominalAboveOneThousand_Throws");
            yield return new TestCaseData(30, 30, 0f, "fixedStepSeconds").SetName("Constructor_ZeroStep_Throws");
            yield return new TestCaseData(30, 30, 1.5f, "fixedStepSeconds").SetName("Constructor_StepAboveOneSecond_Throws");
            yield return new TestCaseData(30, 30, float.NaN, "fixedStepSeconds").SetName("Constructor_NaNStep_Throws");
        }

        [TestCaseSource(nameof(ConstructorFailureCases))]
        public void Constructor_OutOfRange_ThrowsNamingTheArgument(int framesPerSecond, int nominalFramesPerSecond, float fixedStepSeconds, string argument)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new ServerMeshRatesSetting(framesPerSecond, nominalFramesPerSecond, fixedStepSeconds));

            Assert.That(thrown.ParamName, Is.EqualTo(argument));
        }

        [Test]
        public void Constructor_UncappedAtTheLimits_Stores()
        {
            ServerMeshRatesSetting setting = new ServerMeshRatesSetting(ServerMeshRatesSetting.Uncapped, 0, 1f);

            Assert.That(setting.IsUncapped, Is.True);
            Assert.That(setting.NominalFramesPerSecond, Is.EqualTo(0));
            Assert.That(setting.FixedStepSeconds, Is.EqualTo(1f));
        }

        private static IEnumerable<TestCaseData> NominalCases()
        {
            yield return new TestCaseData("u/0.02", -1, 0).SetName("NominalOr_BareUWithUncappedKit_IsZero");
            yield return new TestCaseData("u/0.02", 60, 60).SetName("NominalOr_BareU_IsTheKitCap");
            yield return new TestCaseData("u30/0.02", 60, 30).SetName("NominalOr_UncappedWithFps_IsThatFps");
            yield return new TestCaseData("30/0.02", 60, 30).SetName("NominalOr_Capped_IsTheCap");
        }

        [TestCaseSource(nameof(NominalCases))]
        public void NominalOr(string text, int kitFrameRate, int expected)
        {
            Assert.That(ServerMeshRatesSetting.TryParse(text, out ServerMeshRatesSetting setting, out string error), Is.True, error);

            Assert.That(setting.NominalOr(kitFrameRate), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> CellListCases()
        {
            yield return new TestCaseData(null, new string[0]).SetName("ParseCellList_Null_IsEmpty");
            yield return new TestCaseData("", new string[0]).SetName("ParseCellList_Empty_IsEmpty");
            yield return new TestCaseData(" , ,", new string[0]).SetName("ParseCellList_OnlySeparators_IsEmpty");
            yield return new TestCaseData("C", new[] { "C" }).SetName("ParseCellList_OneCell");
            yield return new TestCaseData(" C , E ", new[] { "C", "E" }).SetName("ParseCellList_EntriesAreTrimmed");
            yield return new TestCaseData("C,,E,", new[] { "C", "E" }).SetName("ParseCellList_EmptyEntriesAreDropped");
            yield return new TestCaseData("C,c,E,C", new[] { "C", "E" }).SetName("ParseCellList_RepeatsIgnoringCaseKeptOnce");
        }

        [TestCaseSource(nameof(CellListCases))]
        public void ParseCellList(string text, string[] expected)
        {
            Assert.That(ServerMeshRates.ParseCellList(text), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> ListedCases()
        {
            yield return new TestCaseData(null, "C", false).SetName("IsListed_NoList_IsFalse");
            yield return new TestCaseData(new[] { "C" }, "", false).SetName("IsListed_EmptyCellId_IsFalse");
            yield return new TestCaseData(new[] { "C", "E" }, "N", false).SetName("IsListed_OtherCell_IsFalse");
            yield return new TestCaseData(new[] { "C", "E" }, "e", true).SetName("IsListed_IgnoresCase");
        }

        [TestCaseSource(nameof(ListedCases))]
        public void IsListed(string[] cells, string cellId, bool expected)
        {
            Assert.That(ServerMeshRates.IsListed(cells, cellId), Is.EqualTo(expected));
        }

        [Test, SetCulture("de-DE")]
        public void RatesLine_NegativeAndNaNValues_PrintAsZero()
        {
            string line = ServerMeshRates.RatesLine(true, 30, 0, 60, -1, -5, -0.02f, float.NaN, -1f, float.NaN, null);

            Assert.That(line, Is.EqualTo("Rates: fps 30 (MESH_RATES; kit 60) vSync 0 | tick 0 Hz (updateFps unchanged) | physics 0.0 s (project 0.0) | maxDeltaTime 0.000 | probe budget 0.00 ms | uncapped cells -"));
        }

        [Test]
        public void RatesLine_BracedCellId_HasNoBraces()
        {
            string line = ServerMeshRates.RatesLine(true, 30, 0, 60, 0, 20, 0.02f, 0.03f, 0.33333334f, 1000f / 30, new[] { "{C}" });

            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}"));
            Assert.That(line, Does.EndWith("uncapped cells (C)"));
        }

        [Test, SetCulture("de-DE")]
        public void RatesLine_Capped_IsTheContractSample()
        {
            string line = ServerMeshRates.RatesLine(true, 30, 0, 60, 0, 20, 0.02f, 0.03f, 0.33333334f, 1000f / 30, new string[0]);

            Assert.That(line, Is.EqualTo(ContractSample));
        }

        [Test, SetCulture("de-DE")]
        public void RatesLine_UncappedWithCells_PrintsTheNominalAndTheList()
        {
            string line = ServerMeshRates.RatesLine(true, -1, 30, 60, 0, 20, 0.02f, 0.03f, 0.33333334f, 1000f / 30, new[] { "C", "E" });

            Assert.That(line, Is.EqualTo("Rates: fps u30 (MESH_RATES; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.02 s (project 0.03) | maxDeltaTime 0.333 | probe budget 33.33 ms | uncapped cells C,E"));
        }

        [Test, SetCulture("de-DE")]
        public void RatesLine_RatesUnset_SaysSo()
        {
            string line = ServerMeshRates.RatesLine(false, 60, 0, 60, 0, 20, 0.03f, 0.03f, 0.33333334f, 1000f / 60, new[] { "C" });

            Assert.That(line, Is.EqualTo("Rates: fps 60 (MESH_RATES unset; kit 60) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.03 s (project 0.03) | maxDeltaTime 0.333 | probe budget 16.67 ms | uncapped cells C"));
        }

        [Test, SetCulture("de-DE")]
        public void RatesLine_BareUWithUncappedKit_PrintsU()
        {
            string line = ServerMeshRates.RatesLine(true, -1, 0, -1, 0, 20, 1f / 60, 0.03f, 0.33333334f, 0f, null);

            Assert.That(line, Is.EqualTo("Rates: fps u (MESH_RATES; kit u) vSync 0 | tick 20 Hz (updateFps unchanged) | physics 0.0167 s (project 0.03) | maxDeltaTime 0.333 | probe budget 0.00 ms | uncapped cells -"));
        }
    }
}
