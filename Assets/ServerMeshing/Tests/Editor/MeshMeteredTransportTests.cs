using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteNetLibManager;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshMeteredTransportTests
    {
        private readonly List<GameObject> _gameObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _gameObjects)
                if (go != null)
                    Object.DestroyImmediate(go);
            _gameObjects.Clear();
        }

        private GameObject NewGameObject(string name)
        {
            GameObject go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            _gameObjects.Add(go);
            return go;
        }

        private MeshMeteredTransportFactory NewFactory(string name)
        {
            MeshMeteredTransportFactory factory = NewGameObject(name).AddComponent<MeshMeteredTransportFactory>();
            factory.Meter = new MeshSendMeter(1_000_000);
            return factory;
        }

        private static NetDataWriter Message(ushort type, int payloadBytes)
        {
            NetDataWriter writer = new NetDataWriter();
            TransportHandler.WritePacket(writer, type);
            for (int i = 0; i < payloadBytes; ++i)
                writer.Put((byte)i);
            return writer;
        }

        private static MeshFakeTransport NewFake()
        {
            return new MeshFakeTransport
            {
                serverPeersCount = 7,
                serverMaxConnections = 1234,
                isClientStarted = true,
                isServerStarted = false,
                hasImplementedPing = false,
                isReliableOnly = true,
                result = false,
                clientRtt = 11,
                serverRtt = 22,
                nextEvent = new TransportEventData { type = ENetworkEvent.DataEvent, connectionId = 99 },
            };
        }

        private static readonly NetDataWriter s_forwardedWriter = Message(119, 3);

        private static IEnumerable<TestCaseData> ConstructorFailureCases()
        {
            yield return new TestCaseData(true, false, "inner").SetName("Constructor_NullInner_ThrowsNamingIt");
            yield return new TestCaseData(false, true, "meter").SetName("Constructor_NullMeter_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(ConstructorFailureCases))]
        public void Constructor_NullArgument_Throws(bool nullInner, bool nullMeter, string argument)
        {
            ITransport inner = nullInner ? null : NewFake();
            MeshSendMeter meter = nullMeter ? null : new MeshSendMeter(1_000_000);

            Assert.That(() => new MeshMeteredTransport(inner, meter, false),
                Throws.TypeOf<ArgumentNullException>().With.Property(nameof(ArgumentNullException.ParamName)).EqualTo(argument));
        }

        private static IEnumerable<TestCaseData> ForwardingCases()
        {
            yield return Row("ServerPeersCount", t => t.ServerPeersCount.ToString(), "7", f => f.calls.ToString(), "1");
            yield return Row("ServerMaxConnections", t => t.ServerMaxConnections.ToString(), "1234", f => f.calls.ToString(), "1");
            yield return Row("IsClientStarted", t => t.IsClientStarted.ToString(), "True", f => f.calls.ToString(), "1");
            yield return Row("IsServerStarted", t => t.IsServerStarted.ToString(), "False", f => f.calls.ToString(), "1");
            yield return Row("HasImplementedPing", t => t.HasImplementedPing.ToString(), "False", f => f.calls.ToString(), "1");
            yield return Row("IsReliableOnly", t => t.IsReliableOnly.ToString(), "True", f => f.calls.ToString(), "1");
            yield return Row("StartClient", t => t.StartClient("10.0.0.2", 7777).ToString(), "False", f => $"{f.lastAddress}:{f.lastPort}", "10.0.0.2:7777");
            yield return Row("ClientSend", t => t.ClientSend(3, DeliveryMethod.ReliableSequenced, s_forwardedWriter).ToString(), "False",
                f => $"{f.lastChannel} {f.lastDelivery} {ReferenceEquals(f.lastWriter, s_forwardedWriter)}", "3 ReliableSequenced True");
            yield return Row("ClientReceive", t => { bool ok = t.ClientReceive(out TransportEventData e); return $"{ok} {e.type} {e.connectionId}"; }, "False DataEvent 99", f => f.calls.ToString(), "1");
            yield return Row("StopClient", t => { t.StopClient(); return "void"; }, "void", f => f.calls.ToString(), "1");
            yield return Row("StartServer", t => t.StartServer(8000, 50).ToString(), "False", f => $"{f.lastPort} {f.lastMaxConnections}", "8000 50");
            yield return Row("ServerSend", t => t.ServerSend(5, 2, DeliveryMethod.Unreliable, s_forwardedWriter).ToString(), "False",
                f => $"{f.lastConnectionId} {f.lastChannel} {f.lastDelivery} {ReferenceEquals(f.lastWriter, s_forwardedWriter)}", "5 2 Unreliable True");
            yield return Row("ServerReceive", t => { bool ok = t.ServerReceive(out TransportEventData e); return $"{ok} {e.type} {e.connectionId}"; }, "False DataEvent 99", f => f.calls.ToString(), "1");
            yield return Row("ServerDisconnect", t => t.ServerDisconnect(12).ToString(), "False", f => f.lastConnectionId.ToString(), "12");
            yield return Row("StopServer", t => { t.StopServer(); return "void"; }, "void", f => f.calls.ToString(), "1");
            yield return Row("Destroy", t => { t.Destroy(); return "void"; }, "void", f => f.calls.ToString(), "1");
            yield return Row("GetClientRtt", t => t.GetClientRtt().ToString(), "11", f => f.calls.ToString(), "1");
            yield return Row("GetServerRtt", t => t.GetServerRtt(13).ToString(), "22", f => f.lastConnectionId.ToString(), "13");
        }

        private static TestCaseData Row(string member, Func<ITransport, string> call, string expected, Func<MeshFakeTransport, string> recorded, string expectedRecorded)
        {
            Func<ITransport, string> recordedOnInner = inner => recorded((MeshFakeTransport)inner);
            return new TestCaseData(member, call, expected, recordedOnInner, expectedRecorded).SetName("Forwards_" + member);
        }

        [TestCaseSource(nameof(ForwardingCases))]
        public void Forwards(string member, Func<ITransport, string> call, string expected, Func<ITransport, string> recorded, string expectedRecorded)
        {
            MeshFakeTransport fake = NewFake();
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, new MeshSendMeter(1_000_000), false);

            string result = call(metered);

            Assert.That(result, Is.EqualTo(expected));
            Assert.That(fake.lastMember, Is.EqualTo(member));
            Assert.That(fake.calls, Is.EqualTo(1));
            Assert.That(recorded(fake), Is.EqualTo(expectedRecorded));
        }

        [Test]
        public void Forwards_EveryTransportMemberHasARow()
        {
            HashSet<string> members = new HashSet<string>();
            foreach (MethodInfo method in typeof(ITransport).GetMethods())
                members.Add(method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name.Substring(4) : method.Name);
            HashSet<string> rows = new HashSet<string>(ForwardingCases().Select(row => (string)row.Arguments[0]));

            Assert.That(members.Count, Is.EqualTo(18));
            Assert.That(rows, Is.EquivalentTo(members));
        }

        [Test]
        public void ServerSend_OtherErrors_PassThroughUncounted()
        {
            MeshFakeTransport fake = NewFake();
            InvalidOperationException thrown = new InvalidOperationException("inner failure");
            fake.serverSendThrows = thrown;
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, true);

            Assert.That(() => metered.ServerSend(1, 2, DeliveryMethod.Unreliable, Message(119, 10)), Throws.Exception.SameAs(thrown));

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.TotalCalls, Is.EqualTo(0));
            Assert.That(window.TooBigCalls, Is.EqualTo(0));
        }

        [Test]
        public void ServerSend_TooBig_IsRethrownAsTheSameInstanceAndCountedOnlyAsTooBig()
        {
            MeshFakeTransport fake = NewFake();
            TooBigPacketException thrown = new TooBigPacketException("too big");
            fake.serverSendThrows = thrown;
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, false);
            NetDataWriter writer = Message(119, 1100);

            Assert.That(() => metered.ServerSend(1, 2, DeliveryMethod.Unreliable, writer), Throws.Exception.SameAs(thrown));

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.TooBigCalls, Is.EqualTo(1));
            Assert.That(window.TooBigBytes, Is.EqualTo(writer.Length));
            Assert.That(window.TotalCalls, Is.EqualTo(0));
            Assert.That(window.TotalBytes, Is.EqualTo(0));
            Assert.That(window.Refused, Is.EqualTo(0));
            Assert.That(window.DeliveryCalls(DeliveryMethod.Unreliable), Is.EqualTo(0));
        }

        [Test]
        public void ServerSend_Refused_IsCountedEverywhereAndAsRefused()
        {
            MeshFakeTransport fake = NewFake();
            fake.result = false;
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, false);
            NetDataWriter writer = Message(119, 20);

            bool sent = metered.ServerSend(1, 2, DeliveryMethod.Unreliable, writer);

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(sent, Is.False);
            Assert.That(window.Refused, Is.EqualTo(1));
            Assert.That(window.Calls(119), Is.EqualTo(1));
            Assert.That(window.Bytes(119), Is.EqualTo(writer.Length));
            Assert.That(window.DeliveryCalls(DeliveryMethod.Unreliable), Is.EqualTo(1));
            Assert.That(window.ChannelCalls(2), Is.EqualTo(1));
        }

        [Test]
        public void ServerSend_Accepted_IsCountedUnderItsTypeDeliveryAndChannel()
        {
            MeshFakeTransport fake = NewFake();
            fake.result = true;
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, false);

            Assert.That(metered.ServerSend(4, 0, DeliveryMethod.ReliableOrdered, Message(65001, 5)), Is.True);
            Assert.That(metered.ServerSend(4, 2, DeliveryMethod.Unreliable, Message(119, 30)), Is.True);

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.Calls(65001), Is.EqualTo(1));
            Assert.That(window.Bytes(65001), Is.EqualTo(8));
            Assert.That(window.Calls(119), Is.EqualTo(1));
            Assert.That(window.Bytes(119), Is.EqualTo(31));
            Assert.That(window.Refused, Is.EqualTo(0));
            Assert.That(window.ChannelCalls(0), Is.EqualTo(1));
            Assert.That(window.ChannelCalls(2), Is.EqualTo(1));
            Assert.That(window.MeanMicroseconds(119), Is.EqualTo(0d));
        }

        private static IEnumerable<TestCaseData> UnknownTypeCases()
        {
            yield return new TestCaseData(new byte[0]).SetName("ServerSend_EmptyWriter_CountsAsAnUnknownType");
            yield return new TestCaseData(new byte[] { 0xFA, 0, 0, 0 }).SetName("ServerSend_UnreadableType_CountsAsAnUnknownType");
            yield return new TestCaseData(new byte[] { 0xF9, 0xF4 }).SetName("ServerSend_TruncatedType_CountsAsAnUnknownType");
        }

        [TestCaseSource(nameof(UnknownTypeCases))]
        public void ServerSend_UnknownType(byte[] payload)
        {
            MeshFakeTransport fake = NewFake();
            fake.result = true;
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, false);
            NetDataWriter writer = new NetDataWriter();
            writer.Put(payload);

            Assert.That(metered.ServerSend(1, 0, DeliveryMethod.ReliableOrdered, writer), Is.True);

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.TypeCount, Is.EqualTo(0));
            Assert.That(window.OtherCalls, Is.EqualTo(1));
            Assert.That(window.OtherBytes, Is.EqualTo(payload.Length));
            Assert.That(window.TotalCalls, Is.EqualTo(1));
            Assert.That(fake.serverSendCalls, Is.EqualTo(1));
        }

        [Test]
        public void ClientMembers_AreNotMetered()
        {
            MeshFakeTransport fake = NewFake();
            MeshSendMeter meter = new MeshSendMeter(1_000_000);
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, meter, true);

            metered.StartClient("127.0.0.1", 7000);
            metered.ClientSend(2, DeliveryMethod.Unreliable, Message(119, 40));
            metered.ClientReceive(out TransportEventData _);
            metered.StopClient();

            MeshSendWindow window = meter.TakeWindow(1d);
            Assert.That(window.TotalCalls, Is.EqualTo(0));
            Assert.That(window.OtherCalls, Is.EqualTo(0));
            Assert.That(window.TooBigCalls, Is.EqualTo(0));
        }

        [Test]
        public void ServerSend_TimedAgainstCountOnly()
        {
            MeshSendMeter timedMeter = new MeshSendMeter(System.Diagnostics.Stopwatch.Frequency);
            MeshSendMeter countedMeter = new MeshSendMeter(System.Diagnostics.Stopwatch.Frequency);
            MeshMeteredTransport timed = new MeshMeteredTransport(NewFake(), timedMeter, true);
            MeshMeteredTransport counted = new MeshMeteredTransport(NewFake(), countedMeter, false);
            NetDataWriter writer = Message(119, 10);

            for (int i = 0; i < 100; ++i)
            {
                timed.ServerSend(1, 2, DeliveryMethod.Unreliable, writer);
                counted.ServerSend(1, 2, DeliveryMethod.Unreliable, writer);
            }

            Assert.That(timed.Timed, Is.True);
            Assert.That(counted.Timed, Is.False);
            Assert.That(timedMeter.TakeWindow(1d).MeanMicroseconds(119), Is.GreaterThanOrEqualTo(0d));
            Assert.That(countedMeter.TakeWindow(1d).MeanMicroseconds(119), Is.EqualTo(0d));
        }

        private static IEnumerable<TestCaseData> AllocationCases()
        {
            yield return new TestCaseData(false).SetName("ServerSend_Untimed_AllocatesNothing");
            yield return new TestCaseData(true).SetName("ServerSend_Timed_AllocatesNothing");
        }

        [TestCaseSource(nameof(AllocationCases))]
        public void ServerSend_AllocatesNothing(bool timed)
        {
            MeshFakeTransport fake = NewFake();
            MeshMeteredTransport metered = new MeshMeteredTransport(fake, new MeshSendMeter(1_000_000), timed);
            NetDataWriter writer = Message(119, 200);
            NetDataWriter empty = new NetDataWriter();
            metered.ServerSend(1, 2, DeliveryMethod.Unreliable, writer);
            metered.ServerSend(1, 0, DeliveryMethod.ReliableOrdered, empty);

            Assert.That(() =>
            {
                metered.ServerSend(1, 2, DeliveryMethod.Unreliable, writer);
                metered.ServerSend(1, 0, DeliveryMethod.ReliableOrdered, empty);
            }, Is.Not.AllocatingGCMemory());
        }

        private static IEnumerable<TestCaseData> InnermostAbsentCases()
        {
            yield return new TestCaseData(new Func<ITransport>(() => null)).SetName("Innermost_NullTransport_IsNull");
            yield return new TestCaseData(new Func<ITransport>(() => new OfflineTransport())).SetName("Innermost_OfflineTransport_IsNull");
            yield return new TestCaseData(new Func<ITransport>(() => new MeshFakeTransport { Inner = null })).SetName("Innermost_WrapperWithNullInner_IsNull");
            yield return new TestCaseData(new Func<ITransport>(() => { MeshFakeTransport loop = new MeshFakeTransport(); loop.Inner = loop; return loop; })).SetName("Innermost_SelfReference_Ends");
            yield return new TestCaseData(new Func<ITransport>(() => Wrapped(new LiteNetLibTransport("k", 16, 16), MeshTransportUnwrap.MaxDepth + 1))).SetName("Innermost_BeyondMaxDepth_IsNull");
        }

        [TestCaseSource(nameof(InnermostAbsentCases))]
        public void Innermost_Absent(Func<ITransport> build)
        {
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(build()), Is.Null);
        }

        private static ITransport Wrapped(ITransport innermost, int wrappers)
        {
            ITransport current = innermost;
            for (int i = 0; i < wrappers; ++i)
                current = new MeshFakeTransport { Inner = current };
            return current;
        }

        private static IEnumerable<TestCaseData> InnermostPresentCases()
        {
            yield return new TestCaseData(new Func<LiteNetLibTransport, ITransport>(lnl => lnl)).SetName("Innermost_NoWrapper_IsTheTransport");
            yield return new TestCaseData(new Func<LiteNetLibTransport, ITransport>(lnl => new MeshMeteredTransport(lnl, new MeshSendMeter(1), false))).SetName("Innermost_OneWrapper_IsTheInner");
            yield return new TestCaseData(new Func<LiteNetLibTransport, ITransport>(lnl => new MeshMeteredTransport(new MeshMeteredTransport(lnl, new MeshSendMeter(1), false), new MeshSendMeter(1), true))).SetName("Innermost_TwoWrappers_IsTheInnermost");
            yield return new TestCaseData(new Func<LiteNetLibTransport, ITransport>(lnl => new MeshMeteredTransport(new MeshFakeTransport { Inner = lnl }, new MeshSendMeter(1), false))).SetName("Innermost_MeteredOverAPassThroughOverLiteNetLib_IsLiteNetLib");
            yield return new TestCaseData(new Func<LiteNetLibTransport, ITransport>(lnl => Wrapped(lnl, MeshTransportUnwrap.MaxDepth))).SetName("Innermost_AtMaxDepth_IsFound");
        }

        [TestCaseSource(nameof(InnermostPresentCases))]
        public void Innermost_Present(Func<LiteNetLibTransport, ITransport> wrap)
        {
            LiteNetLibTransport lnl = new LiteNetLibTransport("k", 16, 16);

            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(wrap(lnl)), Is.SameAs(lnl));
        }

        [Test]
        public void Innermost_OfTheWrapperType_IsTheDeepestOne()
        {
            MeshMeteredTransport inner = new MeshMeteredTransport(new LiteNetLibTransport("k", 16, 16), new MeshSendMeter(1), false);
            MeshMeteredTransport outer = new MeshMeteredTransport(inner, new MeshSendMeter(1), false);
            MeshFakeTransport loop = new MeshFakeTransport();
            loop.Inner = loop;

            Assert.That(MeshTransportUnwrap.Innermost<MeshMeteredTransport>(outer), Is.SameAs(inner));
            Assert.That(MeshTransportUnwrap.Innermost<MeshFakeTransport>(loop), Is.SameAs(loop));
        }

        [Test]
        public void Build_InnerIsItself_Throws()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_InnerIsItself_Throws");
            factory.Inner = factory;

            Assert.That(() => factory.Build(), Throws.InvalidOperationException.With.Message.Contains("its own inner factory"));
        }

        [Test]
        public void Build_TwoFactoriesWrappingEachOther_ThrowsAndRecovers()
        {
            MeshMeteredTransportFactory a = NewFactory("FactoryA");
            MeshMeteredTransportFactory b = NewFactory("FactoryB");
            a.Inner = b;
            b.Inner = a;

            Assert.That(() => a.Build(), Throws.InvalidOperationException.With.Message.Contains("under FactoryA cycles back to the metered factory"));

            b.Inner = null;
            ITransport built = a.Build();
            Assert.That(built, Is.InstanceOf<MeshMeteredTransport>());
            Assert.That(((MeshMeteredTransport)built).Inner, Is.InstanceOf<MeshMeteredTransport>());
        }

        [Test]
        public void Build_WithoutAMeter_Throws()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_WithoutAMeter_Throws");
            factory.Meter = null;

            Assert.That(() => factory.Build(), Throws.InvalidOperationException.With.Message.Contains("has no send meter"));
            Assert.That(factory.GetComponents<LiteNetLibTransportFactory>().Length, Is.EqualTo(0));
        }

        [Test]
        public void KitDefault_NullGameObject_Throws()
        {
            Assert.That(() => MeshMeteredTransportFactory.KitDefault(null),
                Throws.TypeOf<ArgumentNullException>().With.Property(nameof(ArgumentNullException.ParamName)).EqualTo("go"));
        }

        [Test]
        public void Build_NoInner_AddsOneKitDefaultFactoryOnce()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_NoInner_AddsOneKitDefaultFactoryOnce");

            ITransport first = factory.Build();
            ITransport second = factory.Build();

            LiteNetLibTransportFactory[] added = factory.GetComponents<LiteNetLibTransportFactory>();
            Assert.That(added.Length, Is.EqualTo(1));
            Assert.That(factory.Inner, Is.SameAs(added[0]));
            Assert.That(added[0].connectKey, Is.EqualTo("SampleConnectKey"));
            Assert.That(added[0].clientDataChannelsCount, Is.EqualTo(16));
            Assert.That(added[0].serverDataChannelsCount, Is.EqualTo(16));
            LiteNetLibTransport lnl = MeshTransportUnwrap.Innermost<LiteNetLibTransport>(first);
            Assert.That(lnl, Is.Not.Null);
            Assert.That(lnl.ConnectKey, Is.EqualTo("SampleConnectKey"));
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(second), Is.Not.SameAs(lnl));
        }

        [Test]
        public void Build_DestroyedInner_IsReplacedByTheKitDefault()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_DestroyedInner_IsReplacedByTheKitDefault");
            LiteNetLibTransportFactory gone = NewGameObject("GoneFactory").AddComponent<LiteNetLibTransportFactory>();
            factory.Inner = gone;
            Object.DestroyImmediate(gone);

            ITransport built = factory.Build();

            Assert.That(factory.GetComponents<LiteNetLibTransportFactory>().Length, Is.EqualTo(1));
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(built), Is.Not.Null);
        }

        [Test]
        public void Build_WithInner_WrapsItsProduct()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_WithInner_WrapsItsProduct");
            LiteNetLibTransportFactory inner = NewGameObject("InnerFactory").AddComponent<LiteNetLibTransportFactory>();
            inner.connectKey = "InnerKey";
            factory.Inner = inner;

            ITransport built = factory.Build();

            Assert.That(factory.GetComponents<LiteNetLibTransportFactory>().Length, Is.EqualTo(0));
            Assert.That(MeshTransportUnwrap.Innermost<LiteNetLibTransport>(built).ConnectKey, Is.EqualTo("InnerKey"));
        }

        [Test]
        public void Build_TwiceGivesDistinctTransportsSharingOneMeter()
        {
            MeshMeteredTransportFactory factory = NewFactory("Build_TwiceGivesDistinctTransportsSharingOneMeter");
            factory.Timed = true;

            MeshMeteredTransport first = (MeshMeteredTransport)factory.Build();
            MeshMeteredTransport second = (MeshMeteredTransport)factory.Build();

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(first.Meter, Is.SameAs(factory.Meter));
            Assert.That(second.Meter, Is.SameAs(factory.Meter));
            Assert.That(first.Timed, Is.True);
            Assert.That(second.Timed, Is.True);
        }

        private static IEnumerable<TestCaseData> InvalidSettingCases()
        {
            yield return new TestCaseData("on", "MESH_METER_SENDS=on is not off, count or time").SetName("TryParseSetting_On_Warns");
            yield return new TestCaseData("1", "MESH_METER_SENDS=1 is not off, count or time").SetName("TryParseSetting_Number_Warns");
            yield return new TestCaseData("counts", "MESH_METER_SENDS=counts is not").SetName("TryParseSetting_Plural_Warns");
            yield return new TestCaseData("count time", "MESH_METER_SENDS=count time is not").SetName("TryParseSetting_TwoWords_Warns");
            yield return new TestCaseData(" {x} ", "MESH_METER_SENDS=(x) is not").SetName("TryParseSetting_Braces_WarnWithParentheses");
        }

        [TestCaseSource(nameof(InvalidSettingCases))]
        public void TryParseSetting_Invalid(string value, string expectedInWarning)
        {
            bool parsed = MeshMeteredTransportFactory.TryParseSetting(value, out bool meter, out bool timed, out string warning);

            Assert.That(parsed, Is.False);
            Assert.That(meter, Is.False);
            Assert.That(timed, Is.False);
            Assert.That(warning, Does.Contain(expectedInWarning));
            Assert.That(warning, Does.Not.Contain("{"));
            Assert.That(warning, Does.Not.Contain("}"));
        }

        private static IEnumerable<TestCaseData> ValidSettingCases()
        {
            yield return new TestCaseData(null, false, false).SetName("TryParseSetting_Null_NoMeter");
            yield return new TestCaseData("", false, false).SetName("TryParseSetting_Empty_NoMeter");
            yield return new TestCaseData("  ", false, false).SetName("TryParseSetting_Whitespace_NoMeter");
            yield return new TestCaseData("off", false, false).SetName("TryParseSetting_Off_NoMeter");
            yield return new TestCaseData("OFF", false, false).SetName("TryParseSetting_UpperOff_NoMeter");
            yield return new TestCaseData("count", true, false).SetName("TryParseSetting_Count_Meter");
            yield return new TestCaseData(" Count ", true, false).SetName("TryParseSetting_PaddedCount_Meter");
            yield return new TestCaseData("time", true, true).SetName("TryParseSetting_Time_TimedMeter");
            yield return new TestCaseData("TIME", true, true).SetName("TryParseSetting_UpperTime_TimedMeter");
        }

        [TestCaseSource(nameof(ValidSettingCases))]
        public void TryParseSetting_Valid(string value, bool expectedMeter, bool expectedTimed)
        {
            bool parsed = MeshMeteredTransportFactory.TryParseSetting(value, out bool meter, out bool timed, out string warning);

            Assert.That(parsed, Is.True);
            Assert.That(meter, Is.EqualTo(expectedMeter));
            Assert.That(timed, Is.EqualTo(expectedTimed));
            Assert.That(warning, Is.Null);
        }

        private static IEnumerable<TestCaseData> SettingLineCases()
        {
            yield return new TestCaseData(false, "MESH_METER_SENDS=count: ").SetName("SettingLine_Count");
            yield return new TestCaseData(true, "MESH_METER_SENDS=time: ").SetName("SettingLine_Time");
        }

        [TestCaseSource(nameof(SettingLineCases))]
        public void SettingLine(bool timed, string expectedStart)
        {
            string line = MeshMeteredTransportFactory.SettingLine(timed);

            Assert.That(line, Does.StartWith(expectedStart));
            Assert.That(line, Does.Not.Contain("{"));
            Assert.That(line, Does.Not.Contain("\n"));
            Assert.That(line, Does.Not.Contain("Exception"));
        }
    }
}
