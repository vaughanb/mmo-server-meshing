using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    // DESIGN 1.27: the body of a mounted puppet's vehicle keeps its track in the rider's coordinates and places the vehicle
    // so that the seat stands on the rider's pose.
    public partial class MeshKinematicCharacterBodyTests
    {
        private static readonly Vector3 SeatOffset = new Vector3(0.1f, 1.4f, -0.3f);

        [Test]
        public void Seat_ByDefault_IsNotCarried()
        {
            MeshKinematicCharacterBody body = NewBody(false);

            Assert.That(body.CarriesSeat, Is.False);
            Assert.That(body.SeatOffsetAt(90f), Is.EqualTo(Vector3.zero));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(217f)]
        public void Tick_CarryingASeat_PlacesTheVehicleSoTheSeatStandsOnTheRidersPose(float riderYaw)
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.SetSeat(SeatOffset, 0f);
            Vector3 rider = new Vector3(10f, 2f, 5f);
            body.Accept(rider, Vector3.zero, riderYaw, 0x10, 0, false, 0f);

            body.Tick(1f / 60f);

            Vector3 seat = _transform.position + _transform.rotation * SeatOffset;
            Assert.That(Vector3.Distance(seat, rider), Is.LessThan(1e-4f));
            Assert.That(Mathf.DeltaAngle(_transform.eulerAngles.y, riderYaw), Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Tick_CarryingATurnedSeat_TurnsTheVehicleByTheSeatsYaw()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.SetSeat(SeatOffset, 15f);
            Vector3 rider = new Vector3(-3f, 21f, 513f);
            body.Accept(rider, Vector3.zero, 100f, 0x10, 0, false, 0f);

            body.Tick(1f / 60f);

            Assert.That(Mathf.DeltaAngle(_transform.eulerAngles.y, 85f), Is.EqualTo(0f).Within(1e-3f));
            Vector3 seat = _transform.position + _transform.rotation * SeatOffset;
            Assert.That(Vector3.Distance(seat, rider), Is.LessThan(1e-4f));
        }

        [Test]
        public void Pose_CarryingASeat_IsTheRidersPoseNotTheVehicles()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.SetSeat(SeatOffset, 0f);
            Vector3 rider = new Vector3(4f, 5f, 6f);
            body.Accept(rider, new Vector3(8f, 0f, 0f), 90f, 0x10, 0, false, 0f);

            body.Tick(0.05f);

            Assert.That(Vector3.Distance(body.Pose(0.05f), _transform.position + _transform.rotation * SeatOffset), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(body.Pose(0.05f), _transform.position), Is.GreaterThan(1f));
        }

        [Test]
        public void Pose_CarryingASeatBeforeTheFirstSnapshot_IsTheSeatOfTheStandingVehicle()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            _transform.SetPositionAndRotation(new Vector3(4f, 5f, 6f), Quaternion.Euler(0f, 30f, 0f));
            body.SetSeat(SeatOffset, 0f);

            Assert.That(Vector3.Distance(body.Pose(1f), new Vector3(4f, 5f, 6f) + Quaternion.Euler(0f, 30f, 0f) * SeatOffset), Is.LessThan(1e-4f));
        }

        [Test]
        public void WriteServerState_CarryingASeat_WritesTheVehiclesPose()
        {
            MeshKinematicCharacterBody body = NewBody(false);
            body.SetSeat(SeatOffset, 0f);
            body.Accept(new Vector3(10f, 2f, 5f), Vector3.zero, 90f, 0x10, 0, false, 0f);
            body.Tick(1f / 60f);

            Assert.That(ServerBytes(body, 1, out _), Is.EqualTo(KitBytes(0x10, 0)));
        }
    }
}
