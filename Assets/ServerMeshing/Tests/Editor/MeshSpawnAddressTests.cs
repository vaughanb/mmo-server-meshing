using System.Collections.Generic;
using System.Net;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshSpawnAddressTests
    {
        private static readonly IPAddress[] MachineB = { IPAddress.Parse("192.168.50.211"), IPAddress.Parse("172.20.64.1") };

        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData("192.168.50.83", MachineB, "192.168.50.211").SetName("ChooseAdvertisedAddress_AnotherLanMachine_UsesOwnAddressOnThatSubnet");
            yield return new TestCaseData("10.0.0.5", MachineB, "127.0.0.1").SetName("ChooseAdvertisedAddress_PrivateOnNoLocalSubnet_UsesLoopback");
            yield return new TestCaseData("172.20.64.9", MachineB, "172.20.64.1").SetName("ChooseAdvertisedAddress_Private172Range_UsesOwnAddressOnThatSubnet");
            yield return new TestCaseData("192.168.50.83", new IPAddress[0], "127.0.0.1").SetName("ChooseAdvertisedAddress_NoInterfaces_UsesLoopback");
            yield return new TestCaseData("192.168.50.211", MachineB, "192.168.50.211").SetName("ChooseAdvertisedAddress_OwnAddress_Kept");
            yield return new TestCaseData("127.0.0.1", MachineB, "127.0.0.1").SetName("ChooseAdvertisedAddress_Loopback_Kept");
            yield return new TestCaseData("8.8.8.8", MachineB, "8.8.8.8").SetName("ChooseAdvertisedAddress_PublicAddress_KeptForNat");
            yield return new TestCaseData("172.32.0.1", MachineB, "172.32.0.1").SetName("ChooseAdvertisedAddress_JustOutside172Range_Kept");
            yield return new TestCaseData("game.example.com", MachineB, "game.example.com").SetName("ChooseAdvertisedAddress_Hostname_Kept");
            yield return new TestCaseData("fd00::1", MachineB, "fd00::1").SetName("ChooseAdvertisedAddress_IPv6_Kept");
        }

        [TestCaseSource(nameof(Cases))]
        public void ChooseAdvertisedAddress(string configured, IPAddress[] local, string expected)
        {
            Assert.That(MeshMapSpawnNetworkManager.ChooseAdvertisedAddress(configured, local), Is.EqualTo(expected));
        }
    }
}
