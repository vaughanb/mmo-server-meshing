using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomSettingsTests
    {
        private static Func<string, string> Env(params string[] pairs)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                values[pairs[i]] = pairs[i + 1];
            return key => values.TryGetValue(key, out string value) ? value : null;
        }

        private static IEnumerable<TestCaseData> InvalidPhantomCases()
        {
            yield return new TestCaseData("virtual:200:character", "no cell field").SetName("TryParsePhantom_NoCellField_Refused");
            yield return new TestCaseData("virtual:200:ship:C", "step 7").SetName("TryParsePhantom_ShipKind_RefusedUntilStep7");
            yield return new TestCaseData("remote:5:eye:C", "not virtual or sink").SetName("TryParsePhantom_UnknownMode_Refused");
            yield return new TestCaseData("virtual:0:eye:C", "at least 1").SetName("TryParsePhantom_ZeroCount_Refused");
            yield return new TestCaseData("virtual:-1:eye:C", "at least 1").SetName("TryParsePhantom_NegativeCount_Refused");
            yield return new TestCaseData("virtual:+5:eye:C", "at least 1").SetName("TryParsePhantom_SignedCount_Refused");
            yield return new TestCaseData("virtual:5.0:eye:C", "at least 1").SetName("TryParsePhantom_DecimalCount_Refused");
            yield return new TestCaseData("virtual:abc:eye:C", "at least 1").SetName("TryParsePhantom_WordCount_Refused");
            yield return new TestCaseData("virtual:5:robot:C", "not character or eye").SetName("TryParsePhantom_UnknownKind_Refused");
            yield return new TestCaseData("virtual:5:eye:", "cell field").SetName("TryParsePhantom_EmptyCellField_Refused");
            yield return new TestCaseData("virtual:5:eye:C,,E", "cell field").SetName("TryParsePhantom_EmptyCellInList_Refused");
            yield return new TestCaseData("virtual:5:eye:C,c", "twice").SetName("TryParsePhantom_DuplicateCell_Refused");
            yield return new TestCaseData("virtual:5:eye:all,C", "cell field").SetName("TryParsePhantom_AllInAList_Refused");
            yield return new TestCaseData("virtual:5:eye:C:extra", "is not").SetName("TryParsePhantom_FifthField_Refused");
            yield return new TestCaseData("virtual", "is not").SetName("TryParsePhantom_OneField_Refused");
        }

        [TestCaseSource(nameof(InvalidPhantomCases))]
        public void TryParsePhantom_Invalid(string text, string reason)
        {
            bool parsed = ServerMeshPhantomSettings.TryParsePhantom(text, out _, out int phantoms, out _, out _, out string[] cells, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(phantoms, Is.EqualTo(0));
            Assert.That(cells, Is.Empty);
            Assert.That(error, Does.Contain(reason));
            Assert.That(error, Does.Contain(ServerMeshPhantomSettings.ENV_PHANTOM));
        }

        [TestCase(null)]
        [TestCase("")]
        public void TryParsePhantom_Unset_FalseWithoutError(string text)
        {
            Assert.That(ServerMeshPhantomSettings.TryParsePhantom(text, out _, out _, out _, out _, out _, out string error), Is.False);
            Assert.That(error, Is.Null);
        }

        private static IEnumerable<TestCaseData> ValidPhantomCases()
        {
            yield return new TestCaseData("virtual:200:character:C", ServerMeshPhantomRoute.Virtual, 200, ServerMeshPhantomKind.Character, false, new[] { "C" }).SetName("TryParsePhantom_VirtualCharacterOnOneCell");
            yield return new TestCaseData(" Sink : 50 : EYE : C , E ", ServerMeshPhantomRoute.Sink, 50, ServerMeshPhantomKind.Eye, false, new[] { "C", "E" }).SetName("TryParsePhantom_WhitespaceAndCase_AreIgnored");
            yield return new TestCaseData("virtual:1:eye:ALL", ServerMeshPhantomRoute.Virtual, 1, ServerMeshPhantomKind.Eye, true, new string[0]).SetName("TryParsePhantom_AllCells");
        }

        [TestCaseSource(nameof(ValidPhantomCases))]
        public void TryParsePhantom_Valid(string text, ServerMeshPhantomRoute route, int phantoms, ServerMeshPhantomKind kind, bool all, string[] cells)
        {
            bool parsed = ServerMeshPhantomSettings.TryParsePhantom(text, out ServerMeshPhantomRoute gotRoute, out int gotPhantoms, out ServerMeshPhantomKind gotKind, out bool gotAll, out string[] gotCells, out string error);

            Assert.That(parsed, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(gotRoute, Is.EqualTo(route));
            Assert.That(gotPhantoms, Is.EqualTo(phantoms));
            Assert.That(gotKind, Is.EqualTo(kind));
            Assert.That(gotAll, Is.EqualTo(all));
            Assert.That(gotCells, Is.EqualTo(cells));
        }

        private static IEnumerable<TestCaseData> InvalidValueCases()
        {
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_VIEW, "0", ServerMeshPhantomKind.Character).SetName("TryParseView_ZeroForCharacters_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_VIEW, "-1", ServerMeshPhantomKind.Eye).SetName("TryParseView_NegativeForEyes_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_VIEW, "1.5", ServerMeshPhantomKind.Eye).SetName("TryParseView_Decimal_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_ARRIVAL, "0", ServerMeshPhantomKind.Eye).SetName("TryParseArrival_Zero_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_ARRIVAL, "-5", ServerMeshPhantomKind.Eye).SetName("TryParseArrival_Negative_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_ARRIVAL, "NaN", ServerMeshPhantomKind.Eye).SetName("TryParseArrival_NaN_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_ARRIVAL, "Infinity", ServerMeshPhantomKind.Eye).SetName("TryParseArrival_Infinity_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_UPLOAD_HZ, "0", ServerMeshPhantomKind.Eye).SetName("TryParseUploadHz_Zero_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_UPLOAD_HZ, "61", ServerMeshPhantomKind.Eye).SetName("TryParseUploadHz_AboveSixty_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_UPLOAD_HZ, "20.5", ServerMeshPhantomKind.Eye).SetName("TryParseUploadHz_Decimal_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_PAYLOAD, "ship56", ServerMeshPhantomKind.Character).SetName("TryParsePayload_Ship56_RefusedUntilStep7");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_PAYLOAD, "ship28", ServerMeshPhantomKind.Character).SetName("TryParsePayload_Ship28_RefusedUntilStep7");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_PAYLOAD, "input", ServerMeshPhantomKind.Eye).SetName("TryParsePayload_InputWithEyes_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_PAYLOAD, "pose", ServerMeshPhantomKind.Character).SetName("TryParsePayload_UnknownWord_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_STAND, "-0.1", ServerMeshPhantomKind.Character).SetName("TryParseShare_Negative_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_STAND, "1.1", ServerMeshPhantomKind.Character).SetName("TryParseShare_AboveOne_Refused");
            yield return new TestCaseData(ServerMeshPhantomSettings.ENV_FIGHT, "half", ServerMeshPhantomKind.Character).SetName("TryParseShare_Word_Refused");
        }

        private static bool ParseOne(string key, string text, ServerMeshPhantomKind kind, out string error)
        {
            switch (key)
            {
                case ServerMeshPhantomSettings.ENV_VIEW:
                    return ServerMeshPhantomSettings.TryParseView(text, kind, out _, out error);
                case ServerMeshPhantomSettings.ENV_ARRIVAL:
                    return ServerMeshPhantomSettings.TryParseArrival(text, out _, out error);
                case ServerMeshPhantomSettings.ENV_UPLOAD_HZ:
                    return ServerMeshPhantomSettings.TryParseUploadHz(text, out _, out error);
                case ServerMeshPhantomSettings.ENV_PAYLOAD:
                    return ServerMeshPhantomSettings.TryParsePayload(text, kind, out _, out error);
                default:
                    return ServerMeshPhantomSettings.TryParseShare(key, text, out _, out error);
            }
        }

        [TestCaseSource(nameof(InvalidValueCases))]
        public void TryParseValue_Invalid(string key, string text, ServerMeshPhantomKind kind)
        {
            Assert.That(ParseOne(key, text, kind, out string error), Is.False);
            Assert.That(error, Does.Contain(key));
        }

        [Test]
        public void TryParseValues_Valid()
        {
            Assert.That(ServerMeshPhantomSettings.TryParseView("0", ServerMeshPhantomKind.Eye, out int eyeView, out _), Is.True);
            Assert.That(eyeView, Is.EqualTo(0));
            Assert.That(ServerMeshPhantomSettings.TryParseView(" 100 ", ServerMeshPhantomKind.Character, out int view, out _), Is.True);
            Assert.That(view, Is.EqualTo(100));
            Assert.That(ServerMeshPhantomSettings.TryParseArrival("12.5", out float arrival, out _), Is.True);
            Assert.That(arrival, Is.EqualTo(12.5f));
            Assert.That(ServerMeshPhantomSettings.TryParseUploadHz("60", out int hz, out _), Is.True);
            Assert.That(hz, Is.EqualTo(60));
            Assert.That(ServerMeshPhantomSettings.TryParseUploadHz("1", out hz, out _), Is.True);
            Assert.That(hz, Is.EqualTo(1));
            Assert.That(ServerMeshPhantomSettings.TryParsePayload("INPUT", ServerMeshPhantomKind.Character, out ServerMeshPhantomPayloadKind payload, out _), Is.True);
            Assert.That(payload, Is.EqualTo(ServerMeshPhantomPayloadKind.Input));
            Assert.That(ServerMeshPhantomSettings.TryParsePayload("transform", ServerMeshPhantomKind.Eye, out payload, out _), Is.True);
            Assert.That(payload, Is.EqualTo(ServerMeshPhantomPayloadKind.Transform));
            Assert.That(ServerMeshPhantomSettings.TryParseShare(ServerMeshPhantomSettings.ENV_FIGHT, "1", out float share, out _), Is.True);
            Assert.That(share, Is.EqualTo(1f));
        }

        [Test]
        public void EveryKey_EmptyOrNull_IsUnsetWithDefaultsAndNoWarning()
        {
            foreach (string text in new[] { null, "" })
            {
                Assert.That(ServerMeshPhantomSettings.TryParseView(text, ServerMeshPhantomKind.Character, out int view, out string e1), Is.True);
                Assert.That(view, Is.EqualTo(-1));
                Assert.That(ServerMeshPhantomSettings.TryParseArrival(text, out float arrival, out string e2), Is.True);
                Assert.That(arrival, Is.EqualTo(ServerMeshPhantomSettings.DefaultArrivalPerSecond));
                Assert.That(ServerMeshPhantomSettings.TryParseUploadHz(text, out int hz, out string e3), Is.True);
                Assert.That(hz, Is.EqualTo(ServerMeshPhantomSettings.DefaultUploadHz));
                Assert.That(ServerMeshPhantomSettings.TryParsePayload(text, ServerMeshPhantomKind.Eye, out ServerMeshPhantomPayloadKind payload, out string e4), Is.True);
                Assert.That(payload, Is.EqualTo(ServerMeshPhantomPayloadKind.Transform));
                Assert.That(ServerMeshPhantomSettings.TryParseSteps(text, ServerMeshPhantomKind.Character, 10, out int[] steps, out _, out string e5), Is.True);
                Assert.That(steps, Is.Null);
                Assert.That(ServerMeshPhantomSettings.TryParseArea(text, out bool set, out _, out _, out _, out _, out string e6), Is.True);
                Assert.That(set, Is.False);
                Assert.That(ServerMeshPhantomSettings.TryParseShare(ServerMeshPhantomSettings.ENV_STAND, text, out float share, out string e7), Is.True);
                Assert.That(share, Is.EqualTo(0f));
                Assert.That(new[] { e1, e2, e3, e4, e5, e6, e7 }, Is.All.Null);
            }
        }

        private static IEnumerable<TestCaseData> InvalidStepsCases()
        {
            yield return new TestCaseData("50,100@30", ServerMeshPhantomKind.Character, "eye kind").SetName("TryParseSteps_CharacterKind_Refused");
            yield return new TestCaseData("50,100", ServerMeshPhantomKind.Eye, "is not").SetName("TryParseSteps_NoHold_Refused");
            yield return new TestCaseData("50,100@0", ServerMeshPhantomKind.Eye, "is not").SetName("TryParseSteps_ZeroHold_Refused");
            yield return new TestCaseData("50,100@x", ServerMeshPhantomKind.Eye, "is not").SetName("TryParseSteps_WordHold_Refused");
            yield return new TestCaseData("0,100@30", ServerMeshPhantomKind.Eye, "from 1 to P").SetName("TryParseSteps_ZeroStep_Refused");
            yield return new TestCaseData("50,201@30", ServerMeshPhantomKind.Eye, "from 1 to P").SetName("TryParseSteps_StepAboveP_Refused");
            yield return new TestCaseData("a@30", ServerMeshPhantomKind.Eye, "from 1 to P").SetName("TryParseSteps_WordStep_Refused");
        }

        [TestCaseSource(nameof(InvalidStepsCases))]
        public void TryParseSteps_Invalid(string text, ServerMeshPhantomKind kind, string reason)
        {
            Assert.That(ServerMeshPhantomSettings.TryParseSteps(text, kind, 200, out int[] steps, out _, out string error), Is.False);
            Assert.That(steps, Is.Null);
            Assert.That(error, Does.Contain(reason));
        }

        [Test]
        public void TryParseSteps_Valid()
        {
            Assert.That(ServerMeshPhantomSettings.TryParseSteps("50, 200 ,50@30.5", ServerMeshPhantomKind.Eye, 200, out int[] steps, out float hold, out string error), Is.True);
            Assert.That(error, Is.Null);
            Assert.That(steps, Is.EqualTo(new[] { 50, 200, 50 }));
            Assert.That(hold, Is.EqualTo(30.5f));
        }

        private static IEnumerable<TestCaseData> InvalidAreaCases()
        {
            yield return new TestCaseData("1,2,3").SetName("TryParseArea_ThreeParts_Refused");
            yield return new TestCaseData("1,2,0,4").SetName("TryParseArea_ZeroWidth_Refused");
            yield return new TestCaseData("1,2,4,-4").SetName("TryParseArea_NegativeHeight_Refused");
            yield return new TestCaseData("a,b,c,d").SetName("TryParseArea_Words_Refused");
            yield return new TestCaseData("disc:1,2").SetName("TryParseArea_DiscTwoParts_Refused");
            yield return new TestCaseData("disc:1,2,0").SetName("TryParseArea_DiscZeroRadius_Refused");
            yield return new TestCaseData("disc:1,2,x").SetName("TryParseArea_DiscWordRadius_Refused");
        }

        [TestCaseSource(nameof(InvalidAreaCases))]
        public void TryParseArea_Invalid(string text)
        {
            Assert.That(ServerMeshPhantomSettings.TryParseArea(text, out bool set, out _, out _, out _, out _, out string error), Is.False);
            Assert.That(set, Is.False);
            Assert.That(error, Does.Contain(ServerMeshPhantomSettings.ENV_AREA));
        }

        [Test]
        public void TryParseArea_RectAndDisc()
        {
            Assert.That(ServerMeshPhantomSettings.TryParseArea("10,-20,30,40", out bool set, out bool disc, out Vector2 centre, out Vector2 size, out float radius, out _), Is.True);
            Assert.That(set && !disc, Is.True);
            Assert.That(centre, Is.EqualTo(new Vector2(10f, -20f)));
            Assert.That(size, Is.EqualTo(new Vector2(30f, 40f)));
            Assert.That(radius, Is.EqualTo(0f));
            Assert.That(ServerMeshPhantomSettings.TryParseArea(" DISC:1,2,50 ", out set, out disc, out centre, out size, out radius, out _), Is.True);
            Assert.That(set && disc, Is.True);
            Assert.That(centre, Is.EqualTo(new Vector2(1f, 2f)));
            Assert.That(radius, Is.EqualTo(50f));
        }

        private static IEnumerable<TestCaseData> RefusedResolveCases()
        {
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:200:character:C" }, "MESH_PHANTOM_VIEW is required").SetName("TryResolve_ViewMissing_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:200:character:C", "MESH_PHANTOM_VIEW", "0" }, "MESH_PHANTOM_VIEW=0").SetName("TryResolve_ViewZeroWithCharacters_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:20:eye:C", "MESH_PHANTOM_AREA", "0,0,50,50" }, "MESH_PHANTOM_AREA needs the character kind").SetName("TryResolve_AreaWithEyes_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:20:character:C", "MESH_PHANTOM_AREA", "0,0,50,50", "MESH_PHANTOM_VIEW", "19" }, "must be unset").SetName("TryResolve_AreaWithView_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:200:character:C", "MESH_PHANTOM_VIEW", "100", "MESH_PHANTOM_STEPS", "50,100@30" }, "MESH_PHANTOM_STEPS").SetName("TryResolve_StepsWithCharacters_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:200:eye:C", "MESH_PHANTOM_VIEW", "100", "MESH_PHANTOM_STEPS", "50,300@30" }, "MESH_PHANTOM_STEPS").SetName("TryResolve_StepAboveP_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "sink:200:eye:C", "MESH_PHANTOM_VIEW", "100", "MESH_PHANTOM_STEPS", "50,100@30" }, "virtual mode").SetName("TryResolve_StepsWithSink_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:20:eye:C", "MESH_PHANTOM_VIEW", "10", "MESH_PHANTOM_STAND", "0.5" }, "MESH_PHANTOM_STAND=0.5 needs the character kind").SetName("TryResolve_StandWithEyes_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:20:eye:C", "MESH_PHANTOM_VIEW", "10", "MESH_PHANTOM_FIGHT", "1" }, "MESH_PHANTOM_FIGHT=1 needs the character kind").SetName("TryResolve_FightWithEyes_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:20:eye:C", "MESH_PHANTOM_VIEW", "10", "MESH_LOAD_SEED", "x" }, "MESH_LOAD_SEED").SetName("TryResolve_BadSeed_Refused");
            yield return new TestCaseData(new[] { "MESH_PHANTOM", "virtual:{1}:eye:C" }, "virtual:(1):eye:C").SetName("TryResolve_Braces_TurnIntoParentheses");
        }

        [TestCaseSource(nameof(RefusedResolveCases))]
        public void TryResolve_Refused(string[] pairs, string reason)
        {
            bool resolved = ServerMeshPhantomSettings.TryResolve(Env(pairs), out ServerMeshPhantomSettings settings, out string warning);

            Assert.That(resolved, Is.False);
            Assert.That(settings, Is.Null);
            Assert.That(warning, Does.StartWith("Phantoms: "));
            Assert.That(warning, Does.Contain(reason));
            Assert.That(warning, Does.EndWith("; no phantoms installed"));
            Assert.That(warning, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [TestCase(null)]
        [TestCase("")]
        public void TryResolve_Unset_FalseWithoutWarning(string value)
        {
            Assert.That(ServerMeshPhantomSettings.TryResolve(Env("MESH_PHANTOM", value), out ServerMeshPhantomSettings settings, out string warning), Is.False);
            Assert.That(settings, Is.Null);
            Assert.That(warning, Is.Null);
        }

        [Test]
        public void TryResolve_EveryKey()
        {
            bool resolved = ServerMeshPhantomSettings.TryResolve(Env(
                "MESH_PHANTOM", "virtual:400:character:C,E",
                "MESH_PHANTOM_VIEW", "100",
                "MESH_PHANTOM_ARRIVAL_PER_S", "25",
                "MESH_PHANTOM_UPLOAD_HZ", "30",
                "MESH_PHANTOM_PAYLOAD", "input",
                "MESH_PHANTOM_STAND", "0.25",
                "MESH_PHANTOM_FIGHT", "0.5",
                "MESH_LOAD_SEED", "7"), out ServerMeshPhantomSettings settings, out string warning);

            Assert.That(resolved, Is.True);
            Assert.That(warning, Is.Null);
            Assert.That(settings.Route, Is.EqualTo(ServerMeshPhantomRoute.Virtual));
            Assert.That(settings.Phantoms, Is.EqualTo(400));
            Assert.That(settings.Kind, Is.EqualTo(ServerMeshPhantomKind.Character));
            Assert.That(settings.ServesCell("C") && settings.ServesCell("E") && !settings.ServesCell("SW"), Is.True);
            Assert.That(settings.View, Is.EqualTo(100));
            Assert.That(settings.ArrivalPerSecond, Is.EqualTo(25f));
            Assert.That(settings.UploadHz, Is.EqualTo(30));
            Assert.That(settings.Payload, Is.EqualTo(ServerMeshPhantomPayloadKind.Input));
            Assert.That(settings.StandShare, Is.EqualTo(0.25f));
            Assert.That(settings.FightShare, Is.EqualTo(0.5f));
            Assert.That(settings.FightSet, Is.True);
            Assert.That(settings.Seed, Is.EqualTo(7));
            Assert.That(settings.Steps, Is.Null);
            Assert.That(settings.HasArea, Is.False);
            Assert.That(settings.SettingLine(), Does.StartWith("Phantoms: MESH_PHANTOM=virtual:400:character:C,E installed: mode virtual kind character P 400 view 100"));
        }

        [Test]
        public void TryResolve_AreaWithoutView_AndEyeHostMode()
        {
            Assert.That(ServerMeshPhantomSettings.TryResolve(Env("MESH_PHANTOM", "virtual:20:character:all", "MESH_PHANTOM_AREA", "disc:10,20,30"), out ServerMeshPhantomSettings area, out _), Is.True);
            Assert.That(area.HasArea && area.AreaIsDisc, Is.True);
            Assert.That(area.View, Is.EqualTo(-1));
            Assert.That(area.ServesCell("anything"), Is.True);
            Assert.That(area.FightSet, Is.False);
            Assert.That(area.AreaRegion.center.x, Is.EqualTo(10f).Within(1e-4f));
            Assert.That(area.AreaRegion.center.y, Is.EqualTo(20f).Within(1e-4f));
            Assert.That(area.AreaRegion.width, Is.EqualTo(30f * Mathf.Sqrt(2f)).Within(1e-3f));

            Assert.That(ServerMeshPhantomSettings.TryResolve(Env("MESH_PHANTOM", "virtual:50:eye:C", "MESH_PHANTOM_VIEW", "0", "MESH_PHANTOM_STEPS", "10,50@20"), out ServerMeshPhantomSettings eyes, out _), Is.True);
            Assert.That(eyes.View, Is.EqualTo(0));
            Assert.That(eyes.Steps, Is.EqualTo(new[] { 10, 50 }));
            Assert.That(eyes.StepHoldSeconds, Is.EqualTo(20f));
        }

        private static IEnumerable<TestCaseData> StepTargetCases()
        {
            yield return new TestCaseData(-1, -1f, 0, 0, 50).SetName("StepTarget_NotStarted_IsTheFirstStep");
            yield return new TestCaseData(0, -1f, 50, 0, 50).SetName("StepTarget_ReachedButClockNotStarted_Stays");
            yield return new TestCaseData(0, 5f, 50, 0, 50).SetName("StepTarget_HeldLessThanTheHold_Stays");
            yield return new TestCaseData(0, 10f, 40, 0, 50).SetName("StepTarget_NotAllBound_Stays");
            yield return new TestCaseData(0, 10f, 50, 1, 100).SetName("StepTarget_HeldForTheHold_StepsUp");
            yield return new TestCaseData(1, 12f, 100, 2, 50).SetName("StepTarget_HeldForTheHold_StepsDown");
            yield return new TestCaseData(2, 100f, 50, 2, 50).SetName("StepTarget_LastStep_Stays");
        }

        [TestCaseSource(nameof(StepTargetCases))]
        public void StepTarget(int current, float since, int bound, int expectedIndex, int expectedTarget)
        {
            int index = ServerMeshPhantomSettings.StepTarget(new[] { 50, 100, 50 }, 10f, current, since, bound, out int target);

            Assert.That(index, Is.EqualTo(expectedIndex));
            Assert.That(target, Is.EqualTo(expectedTarget));
        }

        [Test]
        public void StepTarget_NoSteps_Throws()
        {
            Assert.That(Assert.Throws<ArgumentException>(() => ServerMeshPhantomSettings.StepTarget(null, 1f, 0, 0f, 0, out _)).ParamName, Is.EqualTo("steps"));
        }
    }
}
