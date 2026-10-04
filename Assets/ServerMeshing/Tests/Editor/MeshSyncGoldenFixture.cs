using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshSyncGoldenFixture holds the stand-in ship's golden records: one sample and its exact legacy54 and header56
    /// bytes. The space team keeps their own record layout in step against a copy of it.
    /// </summary>
    public static class MeshSyncGoldenFixture
    {
        /// <summary>Position is the golden sample's position.</summary>
        public static readonly Vector3 Position = new Vector3(1.5f, -2.25f, 1000f);

        /// <summary>Rotation is the golden sample's rotation (90 degrees of yaw).</summary>
        public static readonly Quaternion Rotation = new Quaternion(0f, 0.70710677f, 0f, 0.70710677f);

        /// <summary>Velocity is the golden sample's velocity.</summary>
        public static readonly Vector3 Velocity = new Vector3(10f, 0f, -5f);

        /// <summary>AngularVelocity is the golden sample's angular velocity.</summary>
        public static readonly Vector3 AngularVelocity = new Vector3(0f, 0.5f, 0f);

        /// <summary>Forward is the golden sample's movement state.</summary>
        public const bool Forward = true;

        /// <summary>TeleportByte is the golden sample's teleport byte.</summary>
        public const byte TeleportByte = 3;

        /// <summary>HeaderAuthority is the header56 golden's authority (ServerAI).</summary>
        public const ServerMeshShipAuthority HeaderAuthority = ServerMeshShipAuthority.ServerAI;

        /// <summary>HeaderFlightState is the header56 golden's flight state.</summary>
        public const byte HeaderFlightState = 2;

        /// <summary>HeaderEpoch is the header56 golden's epoch.</summary>
        public const byte HeaderEpoch = 7;

        /// <summary>Legacy54 is the golden sample's legacy54 record, as space-separated hex.</summary>
        public const string Legacy54 = "00 00 C0 3F 00 00 10 C0 00 00 7A 44 00 00 00 00 F3 04 35 3F 00 00 00 00 F3 04 35 3F 00 00 20 41 00 00 00 00 00 00 A0 C0 00 00 00 00 00 00 00 3F 00 00 00 00 01 03";

        /// <summary>Header56 is the golden sample's header56 record with ServerAI, flight state 2 and epoch 7.</summary>
        public const string Header56 = "09 07 " + Legacy54;

        /// <summary>Sample is the golden sample; with <paramref name="header"/> it carries the header56 golden's header fields.</summary>
        public static ServerMeshShipSample Sample(bool header = false)
        {
            ServerMeshShipSample sample = new ServerMeshShipSample
            {
                Position = Position,
                Rotation = Rotation,
                Velocity = Velocity,
                AngularVelocity = AngularVelocity,
                Forward = Forward,
                TeleportByte = TeleportByte,
            };
            if (header)
            {
                sample.Authority = HeaderAuthority;
                sample.FlightState = HeaderFlightState;
                sample.Epoch = HeaderEpoch;
            }
            return sample;
        }
    }
}
