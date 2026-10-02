// Server Meshing add-on — the transport factory MeshMapNetworkManager installs when MESH_METER_SENDS is count or time:
// every transport it builds is a MeshMeteredTransport around the product of the factory it wraps (the kit's own
// LiteNetLib factory when none was set). The kit builds server and client transports from the same factory, so client
// transports are wrapped too; their sends are not metered.
using System;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// MeshMeteredTransportFactory builds a fresh <see cref="MeshMeteredTransport"/> on every Build, around a transport
    /// from <see cref="Inner"/>, all sharing one <see cref="Meter"/>. It sets nothing up in Awake: the manager configures
    /// it right after adding it.
    /// </summary>
    public class MeshMeteredTransportFactory : BaseTransportFactory
    {
        /// <summary>EnvironmentKey is the environment variable that turns the send meter on: off, count or time.</summary>
        public const string EnvironmentKey = "MESH_METER_SENDS";

        /// <summary>Inner is the factory whose transports are wrapped; when null, Build adds the kit's default factory and keeps it here.</summary>
        public BaseTransportFactory Inner;

        /// <summary>Meter is the send meter every built transport records into.</summary>
        public MeshSendMeter Meter;

        /// <summary>Timed makes every built transport also time each server send inside the inner transport.</summary>
        public bool Timed;

        private bool _building;

        /// <summary>Awake is empty on purpose: it runs inside AddComponent, before the manager has set the fields.</summary>
        private void Awake()
        {
        }

        /// <summary>
        /// KitDefault adds and returns the factory the kit itself falls back to when its manager has none: a
        /// LiteNetLibTransportFactory with its default connect key and channel counts.
        /// </summary>
        public static BaseTransportFactory KitDefault(GameObject go)
        {
            if (go == null)
                throw new ArgumentNullException(nameof(go), "the kit's default transport factory needs a GameObject to live on");
            return go.AddComponent<LiteNetLibTransportFactory>();
        }

        /// <summary>
        /// TryParseSetting reads a MESH_METER_SENDS value, ignoring case and surrounding whitespace. Null, empty and off
        /// give no meter; count gives a meter; time gives a timed meter; all three return true with a null
        /// <paramref name="warning"/>. Anything else returns false with no meter and a warning naming the value (braces
        /// turned into parentheses, because the kit's logger formats every message).
        /// </summary>
        public static bool TryParseSetting(string value, out bool meter, out bool timed, out string warning)
        {
            meter = false;
            timed = false;
            warning = null;
            string trimmed = value == null ? string.Empty : value.Trim();
            if (trimmed.Length == 0 || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(trimmed, "count", StringComparison.OrdinalIgnoreCase))
            {
                meter = true;
                return true;
            }
            if (string.Equals(trimmed, "time", StringComparison.OrdinalIgnoreCase))
            {
                meter = true;
                timed = true;
                return true;
            }
            string shown = trimmed.Replace('{', '(').Replace('}', ')');
            warning = $"{EnvironmentKey}={shown} is not off, count or time; no send meter is installed";
            return false;
        }

        /// <summary>SettingLine is the one line the manager logs when the meter is installed, count or <paramref name="timed"/>.</summary>
        public static string SettingLine(bool timed)
        {
            return timed
                ? $"{EnvironmentKey}=time: every server send is counted by message type, delivery and channel and timed inside the transport; each Work window is followed by a Send window line"
                : $"{EnvironmentKey}=count: every server send is counted by message type, delivery and channel; each Work window is followed by a Send window line";
        }

        /// <summary>
        /// Build returns a new MeshMeteredTransport around Inner's product, sharing Meter. A null Inner (Unity null included)
        /// is replaced by <see cref="KitDefault"/> once and kept, so repeated Builds add one component. It throws
        /// InvalidOperationException when Inner is this factory, when Meter is null, and when the chain of factories comes
        /// back to this one while it is building.
        /// </summary>
        public override ITransport Build()
        {
            if (_building)
                throw new InvalidOperationException($"the transport factory chain under {gameObject.name} cycles back to the metered factory");
            if (Inner == this)
                throw new InvalidOperationException($"the metered transport factory under {gameObject.name} is its own inner factory");
            if (Meter == null)
                throw new InvalidOperationException($"the metered transport factory under {gameObject.name} has no send meter");
            if (Inner == null)
                Inner = KitDefault(gameObject);
            _building = true;
            try
            {
                return new MeshMeteredTransport(Inner.Build(), Meter, Timed);
            }
            finally
            {
                _building = false;
            }
        }
    }
}
