using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipParkPinTests
    {
        private static IEnumerable<TestCaseData> PinCases()
        {
            yield return new TestCaseData(false, true, ServerMeshShipAuthority.Parked, false).SetName("PinsParked_KeyUnsetOr0_NeverPins");
            yield return new TestCaseData(true, false, ServerMeshShipAuthority.Parked, false).SetName("PinsParked_NotSimulating_DoesNotPin");
            yield return new TestCaseData(true, true, ServerMeshShipAuthority.ServerAI, false).SetName("PinsParked_ServerAI_DoesNotPin");
            yield return new TestCaseData(true, true, ServerMeshShipAuthority.OwnerMirror, false).SetName("PinsParked_OwnerMirror_DoesNotPin");
            yield return new TestCaseData(true, true, ServerMeshShipAuthority.Parked, true).SetName("PinsParked_Key1SimulatingParked_Pins");
        }

        [TestCaseSource(nameof(PinCases))]
        public void PinsParked(bool pinEnabled, bool simulating, ServerMeshShipAuthority authority, bool expected)
        {
            Assert.That(ServerMeshShipMovement.PinsParked(pinEnabled, simulating, authority), Is.EqualTo(expected));
        }
    }
}
