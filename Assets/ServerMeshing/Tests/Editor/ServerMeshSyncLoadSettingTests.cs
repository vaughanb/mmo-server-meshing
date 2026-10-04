using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshSyncLoadSettingTests
    {
        private static IEnumerable<TestCaseData> InvalidCases()
        {
            yield return new TestCaseData((string)null).SetName("TryParseSetting_Null_Fails");
            yield return new TestCaseData("").SetName("TryParseSetting_Empty_Fails");
            yield return new TestCaseData("x").SetName("TryParseSetting_Junk_Fails");
            yield return new TestCaseData("dirty:100").SetName("TryParseSetting_MissingRpc_Fails");
            yield return new TestCaseData("rpc:200").SetName("TryParseSetting_MissingDirty_Fails");
            yield return new TestCaseData("dirty:100,").SetName("TryParseSetting_EmptyPart_Fails");
            yield return new TestCaseData("dirty:,rpc:200").SetName("TryParseSetting_MissingValue_Fails");
            yield return new TestCaseData("dirty:100,rpc:200,dirty:5").SetName("TryParseSetting_RepeatedDirty_Fails");
            yield return new TestCaseData("rpc:1,rpc:2").SetName("TryParseSetting_RepeatedRpc_Fails");
            yield return new TestCaseData("load:100,rpc:200").SetName("TryParseSetting_UnknownName_Fails");
            yield return new TestCaseData("dirty:x,rpc:200").SetName("TryParseSetting_TextValue_Fails");
            yield return new TestCaseData("dirty:-1,rpc:200").SetName("TryParseSetting_NegativeDirty_Fails");
            yield return new TestCaseData("dirty:10001,rpc:200").SetName("TryParseSetting_DirtyAboveMax_Fails");
            yield return new TestCaseData("dirty:100,rpc:100001").SetName("TryParseSetting_RpcAboveMax_Fails");
            yield return new TestCaseData("dirty:1.5,rpc:200").SetName("TryParseSetting_FractionalDirty_Fails");
            yield return new TestCaseData("dirty:1:2,rpc:200").SetName("TryParseSetting_TwoColons_Fails");
        }

        [TestCaseSource(nameof(InvalidCases))]
        public void TryParseSetting_Invalid(string text)
        {
            bool parsed = ServerMeshSyncLoad.TryParseSetting(text, out int dirty, out int rpc, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(dirty, Is.EqualTo(0));
            Assert.That(rpc, Is.EqualTo(0));
            StringAssert.Contains(ServerMeshSyncLoad.EnvironmentKey, error);
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        private static IEnumerable<TestCaseData> ValidCases()
        {
            yield return new TestCaseData("dirty:100,rpc:200", 100, 200).SetName("TryParseSetting_DirtyThenRpc_Parses");
            yield return new TestCaseData("rpc:200,dirty:100", 100, 200).SetName("TryParseSetting_RpcThenDirty_Parses");
            yield return new TestCaseData(" DIRTY : 0 , Rpc : 0 ", 0, 0).SetName("TryParseSetting_ZerosSpacesAndCase_Parse");
            yield return new TestCaseData("dirty:10000,rpc:100000", 10000, 100000).SetName("TryParseSetting_Limits_Parse");
        }

        [TestCaseSource(nameof(ValidCases))]
        public void TryParseSetting_Valid(string text, int expectedDirty, int expectedRpc)
        {
            bool parsed = ServerMeshSyncLoad.TryParseSetting(text, out int dirty, out int rpc, out string error);

            Assert.That(parsed, Is.True, error);
            Assert.That(dirty, Is.EqualTo(expectedDirty));
            Assert.That(rpc, Is.EqualTo(expectedRpc));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void SettingLine_ExactLine()
        {
            Assert.That(ServerMeshSyncLoad.SettingLine(100, 200, 50), Is.EqualTo("Sync load: dirty 100/tick rpc 200/s over 50 ships"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_ExactLine()
        {
            string line = ServerMeshSyncLoad.WindowLine(5.0, 50, 10000, 1000, 4000, 0.5, 4.5, 640.4, 2);

            Assert.That(line, Is.EqualTo("Sync load window 5.00 s: ships 50 | writes 10000 rpc 1000 rpcSends 4000 | ms/s write 0.10 rpc 0.90 | alloc 640 B/rpc gc0 2"));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshSyncLoad.WindowLine(double.NaN, -1, -1, 0, 0, double.NaN, -2.0, double.NaN, -1);

            Assert.That(line, Is.EqualTo("Sync load window 0.00 s: ships 0 | writes 0 rpc 0 rpcSends 0 | ms/s write 0.00 rpc 0.00 | alloc 0 B/rpc gc0 0"));
        }
    }
}
