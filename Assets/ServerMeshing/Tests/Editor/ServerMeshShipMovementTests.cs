using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipMovementTests
    {
        [Test]
        public void SmoothingFactor_IsOneMinusExpOfMinusTenDt()
        {
            Assert.That(ServerMeshShipMovement.SmoothingFactor(0f), Is.EqualTo(0f));
            Assert.That(ServerMeshShipMovement.SmoothingFactor(0.1f), Is.EqualTo(1f - Mathf.Exp(-1f)).Within(1e-6f));
            Assert.That(ServerMeshShipMovement.SmoothingFactor(0.02f), Is.EqualTo(1f - Mathf.Exp(-0.2f)).Within(1e-6f));
        }

        [Test]
        public void ObserverStep_BeyondSnapDistance_SnapsToTheExtrapolatedTarget()
        {
            Vector3 net = new Vector3(60f, 0f, 0f);
            Quaternion netRotation = Quaternion.Euler(0f, 45f, 0f);

            bool snapped = ServerMeshShipMovement.ObserverStep(Vector3.zero, Quaternion.identity, ref net, new Vector3(10f, 0f, 0f), netRotation, 0.1f, 50f,
                out Vector3 position, out Quaternion rotation);

            Assert.That(snapped, Is.True);
            Assert.That(net, Is.EqualTo(new Vector3(61f, 0f, 0f)));
            Assert.That(position, Is.EqualTo(net));
            Assert.That(rotation, Is.EqualTo(netRotation));
        }

        [Test]
        public void ObserverStep_WithinSnapDistance_LerpsAndSlerpsByTheSmoothingFactor()
        {
            Vector3 net = new Vector3(4f, 0f, 0f);
            Quaternion netRotation = Quaternion.Euler(0f, 90f, 0f);
            const float dt = 0.05f;

            bool snapped = ServerMeshShipMovement.ObserverStep(Vector3.zero, Quaternion.identity, ref net, new Vector3(0f, 0f, 2f), netRotation, dt, 50f,
                out Vector3 position, out Quaternion rotation);

            float t = 1f - Mathf.Exp(-10f * dt);
            Assert.That(snapped, Is.False);
            Assert.That(net, Is.EqualTo(new Vector3(4f, 0f, 0.1f)));
            Assert.That(Vector3.Distance(position, Vector3.Lerp(Vector3.zero, net, t)), Is.LessThan(1e-6f));
            Assert.That(Quaternion.Angle(rotation, Quaternion.Slerp(Quaternion.identity, netRotation, t)), Is.LessThan(1e-3f));
        }

        private static IEnumerable<TestCaseData> StaleCases()
        {
            yield return new TestCaseData((byte)2, (byte)3, true).SetName("IsStaleUpload_AckBehind_Stale");
            yield return new TestCaseData((byte)255, (byte)0, true).SetName("IsStaleUpload_AckBehindAcrossWrap_Stale");
            yield return new TestCaseData((byte)3, (byte)3, false).SetName("IsStaleUpload_AckEqual_Applied");
            yield return new TestCaseData((byte)0, (byte)0, false).SetName("IsStaleUpload_NeverTeleported_Applied");
        }

        [TestCaseSource(nameof(StaleCases))]
        public void IsStaleUpload(byte acknowledged, byte sequence, bool expected)
        {
            Assert.That(ServerMeshShipMovement.IsStaleUpload(acknowledged, sequence), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> SelfAckCases()
        {
            yield return new TestCaseData(false, true, (byte)1, (byte)0, 10u, 5u, false).SetName("SelfAckDue_Disabled_NeverAcks");
            yield return new TestCaseData(false, true, (byte)1, (byte)0, 1u, uint.MaxValue, false).SetName("SelfAckDue_DisabledAcrossTheTickWrap_NeverAcks");
            yield return new TestCaseData(true, false, (byte)1, (byte)0, 10u, 5u, false).SetName("SelfAckDue_OwnedByAClient_Never");
            yield return new TestCaseData(true, true, (byte)1, (byte)1, 10u, 5u, false).SetName("SelfAckDue_AlreadyAcknowledged_No");
            yield return new TestCaseData(true, true, (byte)1, (byte)0, 5u, 5u, false).SetName("SelfAckDue_SameTick_No");
            yield return new TestCaseData(true, true, (byte)1, (byte)0, 6u, 5u, false).SetName("SelfAckDue_OneTickLater_No");
            yield return new TestCaseData(true, true, (byte)1, (byte)0, 7u, 5u, true).SetName("SelfAckDue_TwoTicksLater_Yes");
            yield return new TestCaseData(true, true, (byte)1, (byte)0, 1u, uint.MaxValue, true).SetName("SelfAckDue_TickWraps_Yes");
        }

        [TestCaseSource(nameof(SelfAckCases))]
        public void SelfAckDue(bool enabled, bool serverOwned, byte sequence, byte acknowledged, uint nowTick, uint teleportTick, bool expected)
        {
            Assert.That(ServerMeshShipMovement.SelfAckDue(enabled, serverOwned, sequence, acknowledged, nowTick, teleportTick), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> UploadCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 0, (byte)0, false).SetName("IsUploadReadable_Legacy54Empty_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 53, (byte)0, false).SetName("IsUploadReadable_Legacy54Short_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 55, (byte)0, false).SetName("IsUploadReadable_Header56Short_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 54, (byte)0, false).SetName("IsUploadReadable_Header56GivenALegacyRecord_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0x09, false).SetName("IsUploadReadable_Header56Mode1FlightState2_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 60, (byte)0x01, false).SetName("IsUploadReadable_Header56LongerMode1_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0x02, false).SetName("IsUploadReadable_Header56Mode2_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x03, false).SetName("IsUploadReadable_Compact28Mode3Upload_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x81, false).SetName("IsUploadReadable_Compact28Mode1_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 28, (byte)0x00, false).SetName("IsUploadReadable_Compact28Short_Bad");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 54, (byte)0, true).SetName("IsUploadReadable_Legacy54Exact_Read");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 54, (byte)0x03, true).SetName("IsUploadReadable_Legacy54FirstByteNeverTested_Read");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0x08, true).SetName("IsUploadReadable_Header56Mode0FlightState2_Read");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0xE0, true).SetName("IsUploadReadable_Header56Bits5To7Ignored_Read");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x00, true).SetName("IsUploadReadable_Compact28FullUpload_Read");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 60, (byte)0x10, true).SetName("IsUploadReadable_Header56Longer_Read");
        }

        [TestCaseSource(nameof(UploadCases))]
        public void IsUploadReadable(ServerMeshShipWireKind wire, int available, byte first, bool expected)
        {
            Assert.That(ServerMeshShipMovement.IsUploadReadable(wire, available, first), Is.EqualTo(expected));
        }

        [TestCase(ServerMeshShipWireKind.Legacy54, 54)]
        [TestCase(ServerMeshShipWireKind.Header56, 56)]
        [TestCase(ServerMeshShipWireKind.Compact28, 56)]
        public void UploadSize_IsTheFullRecord(ServerMeshShipWireKind wire, int expected)
        {
            Assert.That(ServerMeshShipMovement.UploadSize(wire), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> ClientSizeCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0x09, 0).SetName("ClientRecordSize_Header56Mode1_ZeroSoSkippedAsBadLength");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x09, 0).SetName("ClientRecordSize_Compact28Mode1_ZeroSoSkippedAsBadLength");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x02, 0).SetName("ClientRecordSize_Mode2_ZeroSoSkippedAsBadLength");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, 54, (byte)0x03, 54).SetName("ClientRecordSize_Legacy54_Always54");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0x08, 56).SetName("ClientRecordSize_Header56Mode0_56");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 56, (byte)0xE0, 56).SetName("ClientRecordSize_Bits5To7Ignored_56");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 56, (byte)0x08, 56).SetName("ClientRecordSize_Compact28Mode0_56");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, 28, (byte)0x0B, 28).SetName("ClientRecordSize_Compact28Mode3_28");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, 0, (byte)0x09, 56).SetName("ClientRecordSize_NothingLeft_56");
        }

        [TestCaseSource(nameof(ClientSizeCases))]
        public void ClientRecordSize(ServerMeshShipWireKind wire, int available, byte first, int expected)
        {
            Assert.That(ServerMeshShipMovement.ClientRecordSize(wire, available, first), Is.EqualTo(expected));
        }

        [Test]
        public void PackDiscreteVersion_PacksSequenceFlightStateAndEpoch()
        {
            Assert.That(ServerMeshShipMovement.PackDiscreteVersion(3, 2, 7), Is.EqualTo(3u | 2u << 8 | 7u << 16));
            Assert.That(ServerMeshShipMovement.PackDiscreteVersion(255, 7, 255), Is.EqualTo(0x00FF07FFu));
            Assert.That(ServerMeshShipMovement.PackDiscreteVersion(0, 0, 0), Is.EqualTo(0u));
        }

        private static IEnumerable<TestCaseData> CompactAllowedCases()
        {
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, (byte)1, (byte)0, false).SetName("CompactAllowed_TeleportPending_Refused");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, (byte)0, (byte)255, false).SetName("CompactAllowed_PendingAcrossTheWrap_Refused");
            yield return new TestCaseData(ServerMeshShipWireKind.Header56, (byte)0, (byte)0, false).SetName("CompactAllowed_Header56_Refused");
            yield return new TestCaseData(ServerMeshShipWireKind.Legacy54, (byte)0, (byte)0, false).SetName("CompactAllowed_Legacy54_Refused");
            yield return new TestCaseData(ServerMeshShipWireKind.Compact28, (byte)4, (byte)4, true).SetName("CompactAllowed_Compact28Acknowledged_Allowed");
        }

        [TestCaseSource(nameof(CompactAllowedCases))]
        public void CompactAllowed(ServerMeshShipWireKind wire, byte sequence, byte acknowledged, bool expected)
        {
            Assert.That(ServerMeshShipMovement.CompactAllowed(wire, sequence, acknowledged), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> CompactDiffersCases()
        {
            yield return new TestCaseData((byte)5, (byte)6, true).SetName("CompactSequenceDiffers_NextSequence_Differs");
            yield return new TestCaseData((byte)0x00, (byte)0x7F, true).SetName("CompactSequenceDiffers_SevenBitWrapBehind_Differs");
            yield return new TestCaseData((byte)5, (byte)5, false).SetName("CompactSequenceDiffers_Same_Equal");
            yield return new TestCaseData((byte)0x85, (byte)0x05, false).SetName("CompactSequenceDiffers_HighBitIgnored_Equal");
            yield return new TestCaseData((byte)0xFF, (byte)0x7F, false).SetName("CompactSequenceDiffers_AtTheTop_Equal");
        }

        [TestCaseSource(nameof(CompactDiffersCases))]
        public void CompactSequenceDiffers(byte current, byte sequence7, bool expected)
        {
            Assert.That(ServerMeshShipMovement.CompactSequenceDiffers(current, sequence7), Is.EqualTo(expected));
        }

        private static IEnumerable<TestCaseData> CompactAdvanceCases()
        {
            yield return new TestCaseData((byte)5, (byte)5, (byte)5).SetName("AdvanceToCompactSequence_Same_Unchanged");
            yield return new TestCaseData((byte)5, (byte)6, (byte)6).SetName("AdvanceToCompactSequence_Next_OneUp");
            yield return new TestCaseData((byte)0x7F, (byte)0x00, (byte)0x80).SetName("AdvanceToCompactSequence_SevenBitWrap_CarriesIntoBit7");
            yield return new TestCaseData((byte)0xFF, (byte)0x00, (byte)0x00).SetName("AdvanceToCompactSequence_EightBitWrap_Zero");
            yield return new TestCaseData((byte)0x85, (byte)0x07, (byte)0x87).SetName("AdvanceToCompactSequence_HighBitKept_TwoUp");
            yield return new TestCaseData((byte)3, (byte)1, (byte)0x81).SetName("AdvanceToCompactSequence_Behind_MovesForwardOnly");
        }

        [TestCaseSource(nameof(CompactAdvanceCases))]
        public void AdvanceToCompactSequence(byte current, byte sequence7, byte expected)
        {
            Assert.That(ServerMeshShipMovement.AdvanceToCompactSequence(current, sequence7), Is.EqualTo(expected));
        }

        [Test]
        public void AdvanceToCompactSequence_EveryPair_LowSevenBitsMatchTheRecord()
        {
            for (int current = 0; current < 256; ++current)
            {
                for (int sequence7 = 0; sequence7 < 128; ++sequence7)
                {
                    byte next = ServerMeshShipMovement.AdvanceToCompactSequence((byte)current, (byte)sequence7);
                    Assert.That(ServerMeshShipMovement.CompactSequenceDiffers(next, (byte)sequence7), Is.False, $"current {current} sequence {sequence7}");
                    Assert.That((next - current) & 0xFF, Is.LessThan(128), $"current {current} sequence {sequence7}");
                }
            }
        }

        [Test]
        public void CompactRecordOvertakesTheReliableTeleport_TheFullRecordThenFindsNothingNew()
        {
            byte client = 127;
            byte teleport = 128;
            byte sequence7 = (byte)(teleport & 0x7F);

            Assert.That(ServerMeshShipMovement.CompactSequenceDiffers(client, sequence7), Is.True);
            client = ServerMeshShipMovement.AdvanceToCompactSequence(client, sequence7);

            Assert.That(client, Is.EqualTo(teleport), "the reliable mode 0 record that follows compares all 8 bits and does not snap again");
        }


        private static IEnumerable<TestCaseData> VelocityCases()
        {
            Vector3 body = new Vector3(1f, 2f, 3f), bodyAngular = new Vector3(0f, 0.1f, 0f);
            Vector3 upload = new Vector3(4f, 5f, 6f), uploadAngular = new Vector3(0f, 0.2f, 0f);
            yield return new TestCaseData(ServerMeshShipAuthority.OwnerMirror, false, body, bodyAngular, upload, uploadAngular, Vector3.zero, Vector3.zero)
                .SetName("RecordVelocities_MirrorByDefault_Zero");
            yield return new TestCaseData(ServerMeshShipAuthority.OwnerMirror, true, body, bodyAngular, upload, uploadAngular, upload, uploadAngular)
                .SetName("RecordVelocities_MirrorReal_TheUploads");
            yield return new TestCaseData(ServerMeshShipAuthority.ServerAI, false, body, bodyAngular, upload, uploadAngular, body, bodyAngular)
                .SetName("RecordVelocities_ServerAI_TheBodys");
            yield return new TestCaseData(ServerMeshShipAuthority.Parked, true, body, bodyAngular, upload, uploadAngular, body, bodyAngular)
                .SetName("RecordVelocities_ParkedWithRealSetting_StillTheBodys");
        }

        [TestCaseSource(nameof(VelocityCases))]
        public void RecordVelocities(ServerMeshShipAuthority authority, bool real, Vector3 body, Vector3 bodyAngular, Vector3 upload, Vector3 uploadAngular,
            Vector3 expected, Vector3 expectedAngular)
        {
            ServerMeshShipMovement.RecordVelocities(authority, real, body, bodyAngular, upload, uploadAngular, out Vector3 velocity, out Vector3 angular);

            Assert.That(velocity, Is.EqualTo(expected));
            Assert.That(angular, Is.EqualTo(expectedAngular));
        }

        private static IEnumerable<TestCaseData> SimulatesCases()
        {
            yield return new TestCaseData(false, false, ServerMeshShipAuthority.ServerAI, true, false).SetName("Simulates_Client_No");
            yield return new TestCaseData(true, false, ServerMeshShipAuthority.ServerAI, false, false).SetName("Simulates_NotConfigured_No");
            yield return new TestCaseData(true, true, ServerMeshShipAuthority.ServerAI, true, false).SetName("Simulates_PuppetHeld_No");
            yield return new TestCaseData(true, false, ServerMeshShipAuthority.OwnerMirror, true, false).SetName("Simulates_Mirror_No");
            yield return new TestCaseData(true, false, ServerMeshShipAuthority.ServerAI, true, true).SetName("Simulates_ServerAI_Yes");
            yield return new TestCaseData(true, false, ServerMeshShipAuthority.Parked, true, true).SetName("Simulates_Parked_Yes");
        }

        [TestCaseSource(nameof(SimulatesCases))]
        public void Simulates(bool isServer, bool puppetHeld, ServerMeshShipAuthority authority, bool configured, bool expected)
        {
            Assert.That(ServerMeshShipMovement.Simulates(isServer, puppetHeld, authority, configured), Is.EqualTo(expected));
        }

        [Test]
        public void SteeringInputs_LookingAhead_IsZero()
        {
            Vector3 steer = ServerMeshShipMovement.SteeringInputs(Quaternion.identity, Vector3.forward);

            Assert.That(steer.magnitude, Is.LessThan(1e-5f));
        }

        [Test]
        public void SteeringInputs_TargetRightAndUp_YawsRightPitchesUpClamped()
        {
            Vector3 right = ServerMeshShipMovement.SteeringInputs(Quaternion.identity, Vector3.right);
            Vector3 up = ServerMeshShipMovement.SteeringInputs(Quaternion.identity, Vector3.up);
            Vector3 slight = ServerMeshShipMovement.SteeringInputs(Quaternion.identity, Quaternion.Euler(0f, 10f, 0f) * Vector3.forward);

            Assert.That(right.y, Is.EqualTo(1f));
            Assert.That(up.x, Is.EqualTo(-1f));
            Assert.That(slight.y, Is.EqualTo(10f * Mathf.Deg2Rad * ServerMeshShipMovement.SteeringGain).Within(1e-4f));
        }

        [Test]
        public void SteeringInputs_Banked_RollsBackTowardLevel()
        {
            Vector3 steer = ServerMeshShipMovement.SteeringInputs(Quaternion.Euler(0f, 0f, 20f), Quaternion.Euler(0f, 0f, 20f) * Vector3.forward);

            Assert.That(Mathf.Abs(steer.z), Is.EqualTo(20f * Mathf.Deg2Rad * ServerMeshShipMovement.RollLevelGain).Within(1e-4f));
        }

        [Test]
        public void LocalThrust_TinyIntent_IsZero()
        {
            Assert.That(ServerMeshShipMovement.LocalThrust(Quaternion.identity, new Vector3(0.001f, 0f, 0f)), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void LocalThrust_CapsTheMagnitudeAtOneInTheShipFrame()
        {
            Vector3 local = ServerMeshShipMovement.LocalThrust(Quaternion.Euler(0f, 90f, 0f), new Vector3(3f, 0f, 0f));
            Vector3 partial = ServerMeshShipMovement.LocalThrust(Quaternion.identity, new Vector3(0f, 0f, 0.4f));

            Assert.That(local.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(local.z, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(partial.z, Is.EqualTo(0.4f).Within(1e-6f));
        }
    }
}
