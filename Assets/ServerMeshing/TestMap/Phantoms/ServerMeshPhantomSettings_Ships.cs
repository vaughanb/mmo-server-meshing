// Server Meshing add-on — rig phantom settings, ship part (Phase 0 step 7): what MESH_PHANTOM's ship kind reads and refuses.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    public sealed partial class ServerMeshPhantomSettings
    {
        private static readonly string[] s_shipRefusedKeys = { ENV_VIEW, ENV_AREA, ENV_STAND, ENV_FIGHT, ENV_STEPS, ENV_PAYLOAD };

        /// <summary>Wire is the ship kind's MESH_SHIP_WIRE (legacy54 when unset, and for the other kinds).</summary>
        public ServerMeshShipWireKind Wire { get; private set; } = ServerMeshShipWireKind.Legacy54;
        /// <summary>ShipScale is the ship kind's MESH_SHIP_SCALE factor (1 when unset, and for the other kinds).</summary>
        public float ShipScale { get; private set; } = 1f;

        /// <summary>UploadWire is what a ship phantom uploads: legacy54 under legacy54, header56 otherwise (compact records are never uploaded).</summary>
        public ServerMeshShipWireKind UploadWire => UploadWireOf(Wire);

        /// <summary>UploadWireOf is the upload variant of a configured wire: legacy54 stays, header56 and compact28 upload header56.</summary>
        public static ServerMeshShipWireKind UploadWireOf(ServerMeshShipWireKind wire) => wire == ServerMeshShipWireKind.Legacy54 ? ServerMeshShipWireKind.Legacy54 : ServerMeshShipWireKind.Header56;

        /// <summary>ShipWireName is legacy54, header56 or compact28.</summary>
        public static string ShipWireName(ServerMeshShipWireKind wire)
        {
            switch (wire)
            {
                case ServerMeshShipWireKind.Header56:
                    return "header56";
                case ServerMeshShipWireKind.Compact28:
                    return "compact28";
                default:
                    return "legacy54";
            }
        }

        /// <summary>
        /// ShipRefusal is the error for the first key the ship kind refuses that readValue finds set (MESH_PHANTOM_VIEW, _AREA,
        /// _STAND, _FIGHT, _STEPS, _PAYLOAD: a ship phantom sees whatever ships are in its range and uploads what MESH_SHIP_WIRE
        /// names), or null when none is set.
        /// </summary>
        public static string ShipRefusal(Func<string, string> readValue)
        {
            if (readValue == null)
                throw new ArgumentNullException(nameof(readValue));
            for (int i = 0; i < s_shipRefusedKeys.Length; ++i)
            {
                string text = readValue(s_shipRefusedKeys[i]);
                if (!string.IsNullOrEmpty(text))
                    return $"{s_shipRefusedKeys[i]}={text} is refused with the ship kind: a ship phantom sees the ships in its range and uploads what {ServerMeshShipSettings.WireKey} names";
            }
            return null;
        }

        private static bool TryResolveShip(Func<string, string> readValue, ServerMeshPhantomSettings settings, out string error)
        {
            error = ShipRefusal(readValue);
            if (error != null)
                return false;
            string wireText = readValue(ServerMeshShipSettings.WireKey);
            ServerMeshShipWireKind wire = ServerMeshShipWireKind.Legacy54;
            if (!string.IsNullOrEmpty(wireText) && !ServerMeshShipSettings.TryParseWire(wireText, out wire, out string reason))
            {
                error = $"{ServerMeshShipSettings.WireKey}={wireText} is refused for the ship kind: {reason}";
                return false;
            }
            string scaleText = readValue(ServerMeshShipSettings.ScaleKey);
            float factor = 1f;
            if (!string.IsNullOrEmpty(scaleText) && !ServerMeshShipScale.TryParse(scaleText, out factor, out reason))
            {
                error = $"{ServerMeshShipSettings.ScaleKey}={scaleText} is refused for the ship kind: {reason}";
                return false;
            }
            settings.Wire = wire;
            settings.ShipScale = factor;
            error = null;
            return true;
        }

        private string PayloadText() => Kind == ServerMeshPhantomKind.Ship
            ? FormattableString.Invariant($"{ShipWireName(Wire)} scale {ShipScale:0.####}")
            : PayloadName(Payload);
    }
}
