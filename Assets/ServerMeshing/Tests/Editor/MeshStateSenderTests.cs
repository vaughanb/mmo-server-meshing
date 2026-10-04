using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshStateSenderTests
    {
        private GameObject _gameObject;

        [SetUp]
        public void SetUp()
        {
            _gameObject = new GameObject("MeshStateSenderTests") { hideFlags = HideFlags.HideAndDontSave };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_gameObject);
        }

        private static void AssertWarningNames(string warning, string key)
        {
            Assert.That(warning, Does.StartWith("State sender: " + key + "="));
            Assert.That(warning, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception").And.Not.Contain("\n"));
        }

        [TestCase("junk")]
        [TestCase("onn")]
        [TestCase("1")]
        [TestCase("{on}")]
        public void TryParseSetting_Junk_FailsWithAWarningNamingTheKey(string value)
        {
            bool parsed = MeshStateSender.TryParseSetting(value, out MeshStateSenderSetting setting, out string warning);

            Assert.That(parsed, Is.False);
            Assert.That(setting, Is.EqualTo(MeshStateSenderSetting.Off));
            AssertWarningNames(warning, MeshStateSender.EnvironmentKey);
        }

        [TestCase(null, MeshStateSenderSetting.Off)]
        [TestCase("", MeshStateSenderSetting.Off)]
        [TestCase("  ", MeshStateSenderSetting.Off)]
        [TestCase("off", MeshStateSenderSetting.Off)]
        [TestCase("OFF", MeshStateSenderSetting.Off)]
        [TestCase("measure", MeshStateSenderSetting.Measure)]
        [TestCase(" Measure ", MeshStateSenderSetting.Measure)]
        [TestCase("on", MeshStateSenderSetting.On)]
        [TestCase("ON", MeshStateSenderSetting.On)]
        public void TryParseSetting_Valid(string value, MeshStateSenderSetting expected)
        {
            Assert.That(MeshStateSender.TryParseSetting(value, out MeshStateSenderSetting setting, out string warning), Is.True);
            Assert.That(setting, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [TestCase("all")]
        [TestCase("kit,kit")]
        [TestCase("kit,")]
        [TestCase(",")]
        public void TryParseSources_Junk_FailsKeepingBoth(string value)
        {
            Assert.That(MeshStateSender.TryParseSources(value, out bool kit, out bool direct, out string warning), Is.False);
            Assert.That(kit, Is.True);
            Assert.That(direct, Is.True);
            AssertWarningNames(warning, MeshStateSender.SourcesKey);
        }

        [TestCase(null, true, true)]
        [TestCase("", true, true)]
        [TestCase("kit", true, false)]
        [TestCase("direct", false, true)]
        [TestCase("kit,direct", true, true)]
        [TestCase(" Direct , KIT ", true, true)]
        public void TryParseSources_Valid(string value, bool expectedKit, bool expectedDirect)
        {
            Assert.That(MeshStateSender.TryParseSources(value, out bool kit, out bool direct, out string warning), Is.True);
            Assert.That(kit, Is.EqualTo(expectedKit));
            Assert.That(direct, Is.EqualTo(expectedDirect));
            Assert.That(warning, Is.Null);
        }

        [TestCase("20/10")]
        [TestCase("20/10/5/20/1")]
        [TestCase("20/3/5")]
        [TestCase("20/10/21")]
        [TestCase("{20}/10/5")]
        public void TryParseTiers_Junk_FailsKeepingTwenty(string value)
        {
            Assert.That(MeshStateSender.TryParseTiers(value, out byte near, out byte mid, out byte far, out byte owner, out string warning), Is.False);
            Assert.That(new[] { near, mid, far, owner }, Is.EqualTo(new byte[] { 20, 20, 20, 20 }));
            AssertWarningNames(warning, MeshStateSender.TiersKey);
        }

        [TestCase(null, 20, 20, 20, 20)]
        [TestCase("", 20, 20, 20, 20)]
        [TestCase("20/10/5", 20, 10, 5, 20)]
        [TestCase("20/10/5/1", 20, 10, 5, 1)]
        [TestCase(" 0/1/2 ", 0, 1, 2, 20)]
        public void TryParseTiers_Valid(string value, int expectedNear, int expectedMid, int expectedFar, int expectedOwner)
        {
            Assert.That(MeshStateSender.TryParseTiers(value, out byte near, out byte mid, out byte far, out byte owner, out string warning), Is.True);
            Assert.That(new[] { near, mid, far, owner }, Is.EqualTo(new[] { (byte)expectedNear, (byte)expectedMid, (byte)expectedFar, (byte)expectedOwner }));
            Assert.That(warning, Is.Null);
        }

        [TestCase("0")]
        [TestCase("-5")]
        [TestCase("Infinity")]
        [TestCase("NaN")]
        [TestCase("80m")]
        public void TryParseR0_Junk_FailsWithNaN(string value)
        {
            Assert.That(MeshStateSender.TryParseR0(value, out float r0, out string warning), Is.False);
            Assert.That(float.IsNaN(r0), Is.True);
            AssertWarningNames(warning, MeshStateSender.R0Key);
        }

        [Test, SetCulture("de-DE")]
        public void TryParseR0_Valid_ReadsInvariantMetres()
        {
            Assert.That(MeshStateSender.TryParseR0("40.5", out float r0, out string warning), Is.True);
            Assert.That(r0, Is.EqualTo(40.5f));
            Assert.That(warning, Is.Null);
            Assert.That(MeshStateSender.TryParseR0(null, out r0, out warning), Is.True);
            Assert.That(float.IsNaN(r0), Is.True);
            Assert.That(warning, Is.Null);
        }

        [TestCase("near")]
        [TestCase("1")]
        public void TryParseCompact_Junk_FailsOff(string value)
        {
            Assert.That(MeshStateSender.TryParseCompact(value, out bool compactFar, out string warning), Is.False);
            Assert.That(compactFar, Is.False);
            AssertWarningNames(warning, MeshStateSender.CompactKey);
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("off", false)]
        [TestCase("FAR", true)]
        public void TryParseCompact_Valid(string value, bool expected)
        {
            Assert.That(MeshStateSender.TryParseCompact(value, out bool compactFar, out string warning), Is.True);
            Assert.That(compactFar, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [TestCase(MeshStateSender.SpreadKey, "2")]
        [TestCase(MeshStateSender.HorizontalKey, "yes")]
        [TestCase(MeshStateSender.HorizontalKey, "true")]
        public void TryParseSwitch_Junk_FailsOffNamingTheKey(string key, string value)
        {
            Assert.That(MeshStateSender.TryParseSwitch(key, value, out bool on, out string warning), Is.False);
            Assert.That(on, Is.False);
            AssertWarningNames(warning, key);
        }

        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("0", false)]
        [TestCase(" 1 ", true)]
        public void TryParseSwitch_Valid(string value, bool expected)
        {
            Assert.That(MeshStateSender.TryParseSwitch(MeshStateSender.SpreadKey, value, out bool on, out string warning), Is.True);
            Assert.That(on, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [TestCase("0")]
        [TestCase("1001")]
        [TestCase("-4")]
        [TestCase("four")]
        public void TryParseClassifyBuilds_Junk_FailsKeepingFour(string value)
        {
            Assert.That(MeshStateSender.TryParseClassifyBuilds(value, out int builds, out string warning), Is.False);
            Assert.That(builds, Is.EqualTo(4));
            AssertWarningNames(warning, MeshStateSender.ClassifyKey);
        }

        [TestCase(null, 4)]
        [TestCase("", 4)]
        [TestCase("1", 1)]
        [TestCase("10", 10)]
        [TestCase("1000", 1000)]
        public void TryParseClassifyBuilds_Valid(string value, int expected)
        {
            Assert.That(MeshStateSender.TryParseClassifyBuilds(value, out int builds, out string warning), Is.True);
            Assert.That(builds, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        [TestCase("1024")]
        [TestCase("512")]
        [TestCase("big")]
        public void TryParsePacketBytes_Junk_FailsKeeping1020(string value)
        {
            Assert.That(MeshStateSender.TryParsePacketBytes(value, out int bytes, out string warning), Is.False);
            Assert.That(bytes, Is.EqualTo(1020));
            AssertWarningNames(warning, MeshStateSender.PacketBytesKey);
        }

        [TestCase(null, 1020)]
        [TestCase("", 1020)]
        [TestCase("1020", 1020)]
        [TestCase("510", 510)]
        [TestCase("255", 255)]
        public void TryParsePacketBytes_Valid(string value, int expected)
        {
            Assert.That(MeshStateSender.TryParsePacketBytes(value, out int bytes, out string warning), Is.True);
            Assert.That(bytes, Is.EqualTo(expected));
            Assert.That(warning, Is.Null);
        }

        private static IEnumerable<TestCaseData> CleanCases()
        {
            yield return new TestCaseData(null, "").SetName("CleanForLog_Null_IsEmpty");
            yield return new TestCaseData("a {b} c", "a (b) c").SetName("CleanForLog_Braces_BecomeParentheses");
            yield return new TestCaseData("NullReferenceException thrown", "NullReferenceerror thrown").SetName("CleanForLog_Exception_BecomesError");
            yield return new TestCaseData("an exception", "an error").SetName("CleanForLog_LowerCaseException_BecomesError");
            yield return new TestCaseData("one\r\ntwo", "one  two").SetName("CleanForLog_LineBreaks_BecomeSpaces");
        }

        [TestCaseSource(nameof(CleanCases))]
        public void CleanForLog(string text, string expected)
        {
            Assert.That(MeshStateSender.CleanForLog(text), Is.EqualTo(expected));
        }

        [Test]
        public void AllowHandlerType_Null_ThrowsNamingIt()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => sender.AllowHandlerType(null));

            Assert.That(thrown.ParamName, Is.EqualTo("handlerType"));
        }

        [Test]
        public void Register_Null_ThrowsNamingIt()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();

            ArgumentNullException thrown = Assert.Throws<ArgumentNullException>(() => sender.Register(null));

            Assert.That(thrown.ParamName, Is.EqualTo("source"));
        }

        [TestCase(MeshStateSenderSetting.Measure)]
        [TestCase(MeshStateSenderSetting.On)]
        public void NewSender_BeforeAServerStart_IsNotSending(MeshStateSenderSetting setting)
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();
            sender.Setting = setting;

            Assert.That(sender.Setting, Is.EqualTo(setting));
            Assert.That(sender.IsSending, Is.False);
        }

        [Test]
        public void TickHooksAndRegistryCalls_BeforeAServerStart_DoNothing()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();
            sender.Setting = MeshStateSenderSetting.On;
            sender.AllowHandlerType(typeof(MeshFakeKitHandler));

            Assert.That(() =>
            {
                sender.BeforeKitTick(1);
                sender.AfterKitTick(1);
                sender.Unregister(null);
                sender.MarkRelevanceChanged(1, 2);
                sender.MarkSourceChanged(1);
                sender.MarkObserverChanged(2);
            }, Throws.Nothing);
            Assert.That(sender.IsSending, Is.False);
        }

        [Test]
        public void Unregister_AnOwnedSource_CallsNothingOnIt()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();
            sender.Setting = MeshStateSenderSetting.On;
            MeshFakeSenderOwnedSource source = new MeshFakeSenderOwnedSource(5, null);

            sender.Unregister(source);

            Assert.That(source.released, Is.EqualTo(0));
            Assert.That(source.writes, Is.EqualTo(0));
        }

        [Test]
        public void TakeWindowLine_BeforeConfiguration_HasNoPayloadGroup()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();
            sender.Setting = MeshStateSenderSetting.On;

            Assert.That(sender.TakeWindowLine(5d), Does.Not.Contain("payload"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void TakeWindowLine_BeforeConfiguration_IsAZerosLine()
        {
            MeshStateSender sender = _gameObject.AddComponent<MeshStateSender>();
            sender.Setting = MeshStateSenderSetting.Measure;

            string line = sender.TakeWindowLine(5d);

            Assert.That(line, Does.StartWith("Sender window 5.00 s: measure sources 0 kit 0 direct 0 pairs 0 clients 0 | builds 0 skipped 0 rewraps 0 refusals 0 |"));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("NaN").And.Not.Contain("Exception"));
        }
    }
}
