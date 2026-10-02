// Server Meshing add-on — rig AOI timing (MESH_AOI_TIMING): the kit's grid AOI with its in-tick call timed and its passes counted.
using System.Reflection;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshTimedAoi is the kit's JobifiedGridSpatialPartitioningAOI with each per-tick call stamped and marked on the
    /// work meter, together with whether that call ran an AOI pass. It declares only the two update overrides: the base
    /// class's LateUpdate, OnDestroy and OnDrawGizmosSelected are private, and a same-named method here would hide them
    /// from Unity (queries would never complete, owner changes would stay queued, result lists would never be disposed).
    /// Its inherited LateUpdate, the completion half of a pass, runs at <see cref="ExecutionOrder"/>, between
    /// ServerMeshAoiLateBegin and ServerMeshAoiLateEnd. Only ServerMeshTimedAoiInstaller adds it, after checking
    /// <see cref="SystemField"/>.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrder)]
    public class ServerMeshTimedAoi : JobifiedGridSpatialPartitioningAOI
    {
        /// <summary>ExecutionOrder is this component's script order; the late stamps run one below and one above it.</summary>
        public const int ExecutionOrder = 30000;

        /// <summary>SystemFieldName names the kit's private grid system field, null until a loaded scene gives the grid bounds.</summary>
        public const string SystemFieldName = "_system";

        private static readonly FieldInfo s_systemField = ResolveSystemField();

        private readonly ServerMeshAoiCountdown _countdown = new ServerMeshAoiCountdown();

        /// <summary>
        /// SystemField is the kit's private <see cref="SystemFieldName"/> field, or null when the kit no longer declares it
        /// as a reference-type field (reading a value-type field would box on every tick and never read null).
        /// </summary>
        public static FieldInfo SystemField => s_systemField;

        /// <summary>SystemReady says whether the kit has a grid system, which its update needs before it can run a pass.</summary>
        public bool SystemReady => s_systemField.GetValue(this) != null;

        /// <summary>
        /// UpdateInterestManagement runs the kit's update between two stamps and marks the call on the work meter, with
        /// whether it ran a pass as the kit's own countdown decides it.
        /// </summary>
        public override void UpdateInterestManagement(float deltaTime)
        {
            bool due = _countdown.Step(deltaTime, updateInterval, SystemReady);
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            base.UpdateInterestManagement(deltaTime);
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            MeshWorkProbe.Meter.MarkAoiCall(start, end, due);
        }

        /// <summary>
        /// UpdateInterestManagementImmediate forces a pass, as the kit does. The base re-enters the virtual
        /// <see cref="UpdateInterestManagement"/> with 0, so the call is timed and counted there, once.
        /// </summary>
        public override void UpdateInterestManagementImmediate()
        {
            _countdown.ForceDue();
            base.UpdateInterestManagementImmediate();
        }

        private static FieldInfo ResolveSystemField()
        {
            FieldInfo field = typeof(JobifiedGridSpatialPartitioningAOI).GetField(SystemFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field != null && !field.FieldType.IsValueType ? field : null;
        }
    }

    /// <summary>
    /// ServerMeshAoiCountdown mirrors the kit grid AOI's private update countdown in float, in the kit's operation order,
    /// so a caller knows whether an update call ran a pass. The kit leaves its countdown alone while it has no grid
    /// system, hence the systemReady gate; its countdown starts at 0, so the first ready call is due even at a zero delta.
    /// </summary>
    public sealed class ServerMeshAoiCountdown
    {
        private float _remaining;

        /// <summary>Remaining is the countdown in seconds; a call that brings it to 0 or below is due.</summary>
        public float Remaining => _remaining;

        /// <summary>
        /// Step advances the countdown for one update call of <paramref name="deltaTime"/> seconds and returns whether
        /// that call ran a pass, restarting the countdown at <paramref name="interval"/> when it did. While
        /// <paramref name="systemReady"/> is false it returns false and leaves the countdown untouched.
        /// </summary>
        public bool Step(float deltaTime, float interval, bool systemReady)
        {
            if (!systemReady)
                return false;
            _remaining -= deltaTime;
            if (_remaining > 0)
                return false;
            _remaining = interval;
            return true;
        }

        /// <summary>ForceDue makes the next ready call due, as the kit's immediate update does.</summary>
        public void ForceDue()
        {
            _remaining = 0f;
        }
    }
}
