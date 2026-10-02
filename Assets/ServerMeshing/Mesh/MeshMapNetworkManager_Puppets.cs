// Server Meshing add-on — puppet motion (Phase 0 step 3). A character puppet follows its owner's snapshots in one of two
// ways. Steered (the default) walks it with the kit's own movement component toward each snapshot (SteerGhost). Kinematic
// hands its transform to a MeshKinematicCharacterBody: the body stands in the kit's movement-handler slot, takes its pose
// from a MeshKinematicTrack fed by the snapshots, and writes the kit's own movement bytes, so clients read it unchanged.
// Pets and mounted puppets stay steered. With puppetMotion Steered nothing here changes a puppet: no body, no extra
// silenced component, no Puppets window line.
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>
        /// puppetMotion is how a character puppet follows its owner's snapshots: Steered walks it with the kit's movement
        /// component (the default), Kinematic sets its pose from the snapshots. MESH_PUPPET_MOTION overrides it at server start.
        /// </summary>
        [Header("Server Meshing — puppet motion")]
        [Tooltip("How a character puppet follows its owner's snapshots. Steered: the kit's movement component walks it toward each snapshot. Kinematic: its pose is set from the snapshots and sent to clients in the kit's own movement bytes; pets and mounted puppets stay steered. MESH_PUPPET_MOTION=steered|kinematic overrides this at server start.")]
        public MeshPuppetMotion puppetMotion = MeshPuppetMotion.Steered;

#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        // The steer-error histogram holds metres in the histogram's millisecond bins: 400 bins of 0.05 m cover 20 m.
        private const float PuppetSteerBinMetres = 0.05f;
        private const int PuppetSteerBinCount = 400;

        private MeshMovementHandlerSlots _slots;
        private readonly List<MeshGhost> _kinematicGhosts = new List<MeshGhost>();
        private readonly MeshWorkHistogram _puppetSteerKinematic = new MeshWorkHistogram(PuppetSteerBinMetres, PuppetSteerBinCount);
        private readonly MeshWorkHistogram _puppetSteerSteered = new MeshWorkHistogram(PuppetSteerBinMetres, PuppetSteerBinCount);
        private readonly HashSet<string> _puppetHandlerRefusalsLogged = new HashSet<string>();
        private bool _puppetGridRefusalLogged;
        private float _puppetSnapDistance;
        private float _puppetWindowStart;
        private int _puppetSnaps, _puppetObservedTicks, _puppetUnobservedPoses;

        /// <summary>
        /// PuppetsOnStartServer settles the puppet motion for this run: MESH_PUPPET_MOTION overrides the inspector, and a
        /// value that is neither steered nor kinematic is reported and ignored. Kinematic motion snaps past
        /// ghostTeleportDistance, so it needs that distance above zero or the cell stays steered.
        /// </summary>
        private void PuppetsOnStartServer()
        {
            string key = MeshPuppetMotionSetting.EnvironmentKey;
            string value = System.Environment.GetEnvironmentVariable(key);
            string source = "the inspector";
            if (!string.IsNullOrEmpty(value))
            {
                string shown = value.Trim().Replace('{', '(').Replace('}', ')'); // the logger formats every message
                if (MeshPuppetMotionSetting.TryParse(value, out MeshPuppetMotion parsed))
                {
                    puppetMotion = parsed;
                    source = key + "=" + shown;
                }
                else
                {
                    Logging.LogWarning("Mesh", $"{key}={shown} is neither steered nor kinematic; ignored, puppet motion stays {puppetMotion}");
                }
            }
            if (puppetMotion == MeshPuppetMotion.Kinematic && !(ghostTeleportDistance > 0f))
            {
                Logging.LogWarning("Mesh", "Kinematic puppet motion snaps past ghostTeleportDistance, which is not above 0; puppets stay steered");
                puppetMotion = MeshPuppetMotion.Steered;
            }
            _puppetSnapDistance = ghostTeleportDistance;
            _slots = MeshMovementHandlerSlots.For(this);
            _kinematicGhosts.Clear();
            _puppetHandlerRefusalsLogged.Clear();
            _puppetGridRefusalLogged = false;
            ResetPuppetWindow(Time.unscaledTime);
            if (puppetMotion == MeshPuppetMotion.Kinematic)
                Logging.Log("Mesh", System.FormattableString.Invariant($"Puppet motion kinematic (from {source}): character puppets take their pose from the owner's snapshots and snap past {_puppetSnapDistance:0.0#} m; pets and mounted puppets stay steered"));
        }

        /// <summary>PuppetsOnStopServer runs after every puppet was destroyed: the kit clears its handler slots on stop, so the slot records go too.</summary>
        private void PuppetsOnStopServer()
        {
            _slots?.Reset();
            _kinematicGhosts.Clear();
        }

        /// <summary>
        /// PuppetExtrasFor is what Ghostify silences beyond its deny list for a puppet of this kind: the item lock and expiry
        /// of a kinematic player puppet, and null otherwise, so a steered puppet is ghosted exactly as it always was.
        /// </summary>
        private IReadOnlyList<string> PuppetExtrasFor(GhostKind kind)
        {
            return puppetMotion == MeshPuppetMotion.Kinematic && kind == GhostKind.Player ? MeshPuppetMotionSetting.PuppetExtraTypeNames(puppetMotion) : null;
        }

        /// <summary>IsMountedPuppet is true for a puppet that rides, is about to, or is getting off: the seat moves a rider, so it is steered.</summary>
        private static bool IsMountedPuppet(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            return msg.mountEntityId != 0 || ghost.mountPending || ghost.entity.PassengingVehicleEntity != null;
        }

        /// <summary>
        /// EnsurePuppetMotion begins kinematic motion for a puppet without it, at a snapshot, once its mount, summoner and
        /// affiliation are synced and before it is steered. A mounted puppet or a pet stays steered for this snapshot; a
        /// puppet whose kit handler the body does not replace, on a cell that sends movement on the kit's grid path, or
        /// whose install fails stays steered for good.
        /// </summary>
        private void EnsurePuppetMotion(MeshGhost ghost, GhostSnapshotMessage msg)
        {
            if (puppetMotion != MeshPuppetMotion.Kinematic || ghost.body != null || ghost.refusal != MeshPuppetFallback.None || _slots == null)
                return;
            if (IsMountedPuppet(ghost, msg) || !string.IsNullOrEmpty(msg.summonerMeshId))
                return;
            BaseCharacterEntity entity = ghost.entity;
            uint objectId = entity.ObjectId;
            if (!_slots.TryGetKitHandler(objectId, out IEntityMovementDataHandler kit) || kit == null)
            {
                RefusePuppetMotion(ghost, MeshPuppetFallback.Install, $"the kit's movement-handler slot has no entry for object {objectId}");
                return;
            }
            bool gridDisabled = DefaultGridManagerComponent.Instance == null || DefaultGridManagerComponent.Instance.IsDisabled;
            MeshPuppetFallback refusal = MeshKinematicCharacterBody.Refusal(kit, mounted: false, summoned: false, gridDisabled);
            if (refusal != MeshPuppetFallback.None)
            {
                RefusePuppetMotion(ghost, refusal, kit.GetType().Name);
                return;
            }
            Transform transform = entity.EntityTransform;
            float now = Time.unscaledTime;
            MeshKinematicTrack track = new MeshKinematicTrack(snapDistance: _puppetSnapDistance);
            if (!track.TryAccept(transform.position, Vector3.zero, transform.eulerAngles.y, false, now))
            {
                RefusePuppetMotion(ghost, MeshPuppetFallback.Install, $"object {objectId} does not stand at a finite pose");
                return;
            }
            EndSteering(ghost);
            MeshKinematicCharacterBody body = new MeshKinematicCharacterBody(objectId, entity.Identity, transform, kit, kit is NavMeshEntityMovement, track);
            if (!body.Begin(entity, _slots, out string error))
            {
                RefusePuppetMotion(ghost, MeshPuppetFallback.Install, error);
                return;
            }
            ghost.body = body;
            _kinematicGhosts.Add(ghost);
        }

        /// <summary>
        /// RefusePuppetMotion keeps a puppet steered for good. A handler type is reported once per type and the grid path
        /// once per run, since every puppet with them is refused alike; a failed install is reported per puppet.
        /// </summary>
        private void RefusePuppetMotion(MeshGhost ghost, MeshPuppetFallback reason, string detail)
        {
            ghost.refusal = reason;
            switch (reason)
            {
                case MeshPuppetFallback.Handler:
                    if (_puppetHandlerRefusalsLogged.Add(detail))
                        Logging.LogWarning("Mesh", $"Puppet motion: {ghost.kind} puppets moved by {detail} stay steered; kinematic motion replaces only the kit's character-controller and NavMesh handlers");
                    break;
                case MeshPuppetFallback.Grid:
                    if (!_puppetGridRefusalLogged)
                    {
                        _puppetGridRefusalLogged = true;
                        Logging.LogWarning("Mesh", "Puppet motion: this cell sends movement on the kit's grid path, which a kinematic body does not feed; every puppet stays steered");
                    }
                    break;
                default:
                    BaseCharacterEntity entity = ghost.entity;
                    Logging.LogWarning("Mesh", $"Puppet motion: {ghost.kind} {(string.IsNullOrEmpty(entity.CharacterName) ? ghost.kind.ToString() : entity.CharacterName)} ({MeshEntityIdOf(entity)}) stays steered ({reason}): {detail}");
                    break;
            }
        }

        /// <summary>StopPuppetMotion gives a puppet back to the kit for good (reservation, promotion): its body ends, then EndSteering. A steered puppet gets EndSteering alone.</summary>
        private void StopPuppetMotion(MeshGhost ghost)
        {
            if (ghost.body != null)
                EndPuppetMotion(ghost, MeshPuppetFallback.None);
            EndSteering(ghost);
        }

        /// <summary>
        /// EndPuppetMotion takes a puppet's body out: the kit's handler goes back in its slot (the body forgets the slot of a
        /// destroyed entity) and the movement component runs again, so a live puppet is steered from its next snapshot.
        /// Nothing happens for a puppet without a body.
        /// </summary>
        private void EndPuppetMotion(MeshGhost ghost, MeshPuppetFallback reason)
        {
            MeshKinematicCharacterBody body = ghost.body;
            if (body == null)
                return;
            ghost.body = null;
            _kinematicGhosts.Remove(ghost);
            BaseCharacterEntity entity = ghost.entity;
            body.End(entity, _slots);
            if (reason != MeshPuppetFallback.None && traceHandoffTiming && entity != null)
                Logging.Log("Mesh", $"Puppet motion: {ghost.kind} {(string.IsNullOrEmpty(entity.CharacterName) ? ghost.kind.ToString() : entity.CharacterName)} ({MeshEntityIdOf(entity)}) back to steering ({reason})");
        }

        /// <summary>
        /// KinematicPuppetsUpdate drops, every frame, a kinematic puppet the kit destroyed outside DestroyGhost; its body
        /// forgets the slot: the entry went with the entity, or belongs to whatever reused the object id. Puppets are posed
        /// elsewhere: a watched one at every kit tick (PoseKinematicPuppets), one nobody watches when its snapshots arrive.
        /// </summary>
        private void KinematicPuppetsUpdate()
        {
            for (int i = _kinematicGhosts.Count - 1; i >= 0; --i)
            {
                MeshGhost ghost = _kinematicGhosts[i];
                MeshKinematicCharacterBody body = ghost.body;
                BaseCharacterEntity entity = ghost.entity;
                if (body == null || entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                {
                    if (body != null)
                        body.End(entity, _slots);
                    ghost.body = null;
                    _kinematicGhosts.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// PoseKinematicPuppets places every watched kinematic puppet at its pose for now, at the start of each kit tick:
        /// the tick sends movement to clients, which see only what a tick sends, and its interest pass reads the same pose.
        /// The pose is a function of time, so posing at the tick rate rather than every frame loses nothing clients see.
        /// </summary>
        private void PoseKinematicPuppets()
        {
            if (_kinematicGhosts.Count == 0)
                return;
            float now = Time.unscaledTime;
            for (int i = 0; i < _kinematicGhosts.Count; ++i)
            {
                MeshGhost ghost = _kinematicGhosts[i];
                BaseCharacterEntity entity = ghost.entity;
                if (ghost.body == null || entity == null || entity.Identity == null || entity.Identity.IsDestroyed)
                    continue; // dropped by KinematicPuppetsUpdate
                if (entity.Identity.CountSubscribers() > 0)
                {
                    ghost.body.Tick(now);
                    _puppetObservedTicks++;
                }
            }
        }

        /// <summary>
        /// FeedKinematicPuppet is SteerGhost for a puppet with a body. The snapshot goes to the body; when its pose missed the
        /// snapshot by more than ghostTeleportDistance (<paramref name="dist"/>, taken from that pose) the body snaps, and
        /// the snap is counted and logged exactly as a steered teleport. A puppet nobody watches takes its pose at once,
        /// because nothing moves it between snapshots; a watched monster puppet keeps the steered catch-up move speed,
        /// which its clients walk it at. The kit's Teleport, PointClickMovement and StopMove are never called: they would
        /// reach the disabled movement component.
        /// </summary>
        private void FeedKinematicPuppet(MeshGhost ghost, GhostSnapshotMessage msg, float dist, bool moving)
        {
            BaseCharacterEntity entity = ghost.entity;
            MeshKinematicCharacterBody body = ghost.body;
            float now = Time.unscaledTime;
            if (!body.Accept(msg.position, msg.velocity, msg.yaw, msg.movementState, msg.extraMovementState, dist > ghostTeleportDistance, now))
                WarnThrottled($"Puppet motion: the snapshot of {ghost.kind} {msg.characterId} was refused by its track (a value that is not finite); the puppet keeps its last one");
            else if (body.LastAcceptSnapped)
            {
                NoteGhostTeleport(ghost, msg, dist);
                _puppetSnaps++;
            }
            if (entity.Identity.CountSubscribers() > 0)
            {
                if (entity.Movement is NavMeshEntityMovement)
                    ApplySteerMoveSpeed(entity, msg, dist, moving);
            }
            else
            {
                body.ApplyNow(now);
                _puppetUnobservedPoses++;
            }
            SyncGhostHp(ghost, msg);
        }

        /// <summary>SteeredReason is why a puppet without a body is steered while puppet motion is kinematic, or None (reserved, or no snapshot since its demotion).</summary>
        private static MeshPuppetFallback SteeredReason(MeshGhost ghost)
        {
            if (ghost.mountPending || ghost.entity.PassengingVehicleEntity != null)
                return MeshPuppetFallback.Mounted;
            if (!string.IsNullOrEmpty(ghost.summonerMeshId))
                return MeshPuppetFallback.Summoned;
            return ghost.refusal;
        }

        /// <summary>
        /// LogPuppetsWindow writes the Puppets window line next to the Mesh stats line while puppet motion is kinematic: the
        /// puppets by motion at the end of the window and the steered ones by reason, the kinematic snaps, the watched
        /// tick poses and the unwatched snapshot poses per second, and the steer error of every moving puppet, kinematic and
        /// steered apart (the sample ghostSteer takes: the distance from the pose the puppet shows, a kinematic puppet's
        /// pose on its track, to each snapshot as it arrives). Invariant culture, because the rig's tools parse it.
        /// </summary>
        private void LogPuppetsWindow()
        {
            float now = Time.unscaledTime;
            float window = Mathf.Max(0.001f, now - _puppetWindowStart);
            int kinematic = 0, steered = 0, mounted = 0, summoned = 0, handler = 0, grid = 0, install = 0;
            foreach (MeshGhost ghost in _ghosts.Values)
            {
                if (ghost.entity == null)
                    continue;
                if (ghost.body != null)
                {
                    kinematic++;
                    continue;
                }
                steered++;
                switch (SteeredReason(ghost))
                {
                    case MeshPuppetFallback.Mounted: mounted++; break;
                    case MeshPuppetFallback.Summoned: summoned++; break;
                    case MeshPuppetFallback.Handler: handler++; break;
                    case MeshPuppetFallback.Grid: grid++; break;
                    case MeshPuppetFallback.Install: install++; break;
                }
            }
            Logging.Log("Mesh", System.FormattableString.Invariant(
                $"Puppets window {window:0.00} s: kinematic {kinematic} steered {steered} | fallback mounted {mounted} summoned {summoned} handler {handler} grid {grid} install {install} | snaps {_puppetSnaps} | observed ticks {_puppetObservedTicks / window:0}/s unobserved poses {_puppetUnobservedPoses / window:0}/s | steer kinematic {_puppetSteerKinematic.Count} mean {_puppetSteerKinematic.Mean:0.00} m p95 {_puppetSteerKinematic.Percentile(0.95f):0.00} m max {_puppetSteerKinematic.Max:0.00} m | steer steered {_puppetSteerSteered.Count} mean {_puppetSteerSteered.Mean:0.00} m p95 {_puppetSteerSteered.Percentile(0.95f):0.00} m max {_puppetSteerSteered.Max:0.00} m"));
            ResetPuppetWindow(now);
        }

        private void ResetPuppetWindow(float now)
        {
            _puppetWindowStart = now;
            _puppetSnaps = 0;
            _puppetObservedTicks = 0;
            _puppetUnobservedPoses = 0;
            _puppetSteerKinematic.Reset();
            _puppetSteerSteered.Reset();
        }
#endif
    }
}
