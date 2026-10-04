using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshPhantomShipTests
    {
        private static readonly Rect Cell = new Rect(-300f, -300f, 600f, 600f);

        [Test]
        public void ShipCentres_NullScale_Throws()
        {
            Assert.That(Assert.Throws<ArgumentNullException>(() => ServerMeshPhantomClients.ShipCentres(12345, 4, Cell, 0f, null, out _, out _)).ParamName, Is.EqualTo("scale"));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void ShipCentres_NoPhantoms_Throws(int phantoms)
        {
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => ServerMeshPhantomClients.ShipCentres(12345, phantoms, Cell, 0f, new ServerMeshShipScale(1f), out _, out _)).ParamName, Is.EqualTo("phantoms"));
        }

        [Test]
        public void ShipCentres_AreaTooSmallForAPath_FailsNamingThePhantom()
        {
            bool drawn = ServerMeshPhantomClients.ShipCentres(12345, 4, new Rect(0f, 0f, 100f, 100f), 0f, new ServerMeshShipScale(1f), out Vector3[] centres, out string error);

            Assert.That(drawn, Is.False);
            Assert.That(centres, Is.Null);
            Assert.That(error, Does.StartWith("phantom ship 0 has no path: "));
            Assert.That(error, Does.Not.Contain("{").And.Not.Contain("}"));
        }

        private static IEnumerable<TestCaseData> CentreCases()
        {
            yield return new TestCaseData(1f, 2).SetName("ShipCentres_ScaleOne_TwoPhantomsInTheCoarseCell");
            yield return new TestCaseData(0.03f, 64).SetName("ShipCentres_Scale003_SixtyFourPhantomsInTheCoarseCell");
        }

        [TestCaseSource(nameof(CentreCases))]
        public void ShipCentres_UseThePhantomPathIndices_InsideTheArea_Repeatably(float factor, int phantoms)
        {
            ServerMeshShipScale scale = new ServerMeshShipScale(factor);
            Assert.That(ServerMeshPhantomClients.ShipCentres(777, phantoms, Cell, 2f, scale, out Vector3[] centres, out string error), Is.True, error);
            Assert.That(ServerMeshPhantomClients.ShipCentres(777, phantoms, Cell, 2f, scale, out Vector3[] again, out _), Is.True);

            Assert.That(error, Is.Null);
            Assert.That(centres.Length, Is.EqualTo(phantoms));
            Assert.That(again, Is.EqualTo(centres));
            for (int i = 0; i < phantoms; ++i)
            {
                Assert.That(ServerMeshShipFlightPath.TryChooseCentre(777, ServerMeshShipFlightPath.PhantomIndexBase + i, Cell, 2f, scale, out Vector3 expected, out _), Is.True);
                Assert.That(centres[i], Is.EqualTo(expected));
                Assert.That(centres[i].x, Is.InRange(Cell.xMin, Cell.xMax));
                Assert.That(centres[i].z, Is.InRange(Cell.yMin, Cell.yMax));
                Assert.That(centres[i].y, Is.EqualTo(2f + ServerMeshShipFlightPath.AltitudeMetres).Within(1e-4f));
            }
        }

        private static IEnumerable<TestCaseData> ShipReplyCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, (byte)0).SetName("ShipReply_Legacy54_PayloadZero");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, (byte)1).SetName("ShipReply_Header56_PayloadOne");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, (byte)1).SetName("ShipReply_Compact28_PayloadOne_UploadsHeader56");
        }

        [TestCaseSource(nameof(ShipReplyCases))]
        public void ShipReply_CarriesThePath(ServerMeshShipWireKind wire, byte payload)
        {
            Vector3 centre = new Vector3(-120.5f, 60f, 88.25f);

            ServerMeshPhantomBindReply reply = ServerMeshPhantomClients.ShipReply(2500, 20, wire, 12345, 1003, 0.03f, centre, 3.5f);

            Assert.That(reply.objectId, Is.EqualTo(2500u));
            Assert.That(reply.kind, Is.EqualTo(ServerMeshPhantomBindReply.ShipKind));
            Assert.That(reply.payload, Is.EqualTo(payload));
            Assert.That(reply.hz, Is.EqualTo(20));
            Assert.That(reply.stand, Is.EqualTo(0));
            Assert.That(reply.speed, Is.EqualTo(3.5f));
            Assert.That(new Vector3(reply.centreX, reply.centreY, reply.centreZ), Is.EqualTo(centre));
            Assert.That(new Vector2(reply.x, reply.z), Is.EqualTo(new Vector2(centre.x, centre.z)));
            Assert.That(reply.halfSide, Is.EqualTo(0f));
            Assert.That(reply.seed, Is.EqualTo(12345));
            Assert.That(reply.flightPath, Is.EqualTo(1003));
            Assert.That(reply.scale, Is.EqualTo(0.03f));
            Assert.That(reply.IsFollowableShip, Is.True);
        }
    }
}
