using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Runs down a movement force on the server for a player whose movement the server does
    /// not simulate - which is every player in a real client/server session.
    ///
    /// **Why Charge ran off the map.** `SimpleDashAttackSkill` applies its force on the server
    /// only (`ApplyForce` returns on a client), and the server then sends its force list to the
    /// owner client in every sync message, where the client replaces its own list with the copy
    /// (`ReadServerStateAtClient`). But the kit only ticks forces inside `UpdateMovement`, and for
    /// a client-authoritative player (`movementSecure = NotSecure`, the demo's setting) the server
    /// never runs that - `CanSimulateMovement()` is false there, so the server interpolates and
    /// nothing touches the list. The server's copy of the Charge force therefore sits at elapsed
    /// 0 and full speed forever, the client gets a fresh one every sync and restarts the dash, and
    /// the warrior keeps running in a straight line - across the sea and off the edge of the map.
    /// A knockback against a player ends the same way, since any force on a player goes through
    /// the same path.
    ///
    /// The editor harness never showed it because its player is server-owned, and a server-owned
    /// player's movement IS simulated by the server, so the force ends on its clock there.
    ///
    /// **What this does.** On the server, for a player whose movement it does not simulate, this
    /// ticks the force list the way `UpdateMovement` would - the same `UpdateForces` extension,
    /// the same walking-speed cut-off, the same listeners before and after - so the force runs
    /// down, drops out of the list, and the next sync tells the client to stop. Calling the
    /// listeners also makes the `DashAttackHandler` fire Charge's arrival hit on the server,
    /// which is the only side whose damage counts. The list itself is private to the kit's
    /// movement functions, so it is read once by reflection; if a kit update renames the field
    /// this logs once and switches itself off rather than guessing.
    ///
    /// Inert on clients and on any player the server simulates, so the harness is unchanged.
    /// Ticks on the entity's own update (<see cref="BaseGameEntity.onUpdate"/>), and lets go of
    /// it on a pure client once the dash handler there has been quieted.
    /// The demo puts it on every player from <c>DemoEntitySetting</c>.
    /// </summary>
    public class RemoteForceUpkeep : MonoBehaviour
    {
        private const string ForceListField = "_movementForceAppliers";

        private static FieldInfo s_forceListField;
        private static bool s_lookedUpField;

        private BaseCharacterEntity _entity;
        private CharacterControllerEntityMovement _movement;
        private List<EntityMovementForceApplier> _forces;
        private IEntityMovementForceUpdateListener[] _listeners;
        private bool _subscribed;

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            if (_entity == null)
            {
                enabled = false;
                return;
            }
            if (!s_lookedUpField)
            {
                s_lookedUpField = true;
                s_forceListField = typeof(BuiltInEntityMovementFunctions3D).GetField(
                    ForceListField, BindingFlags.NonPublic | BindingFlags.Instance);
                if (s_forceListField == null)
                {
                    Debug.LogError($"[{nameof(RemoteForceUpkeep)}] {nameof(BuiltInEntityMovementFunctions3D)} has no " +
                                   $"\"{ForceListField}\" field any more; a dash or knockback on a remote player " +
                                   "will never end. Point this at the kit's force list.");
                }
            }
        }

        private void OnEnable()
        {
            if (_entity == null || _subscribed)
                return;
            _entity.onUpdate += Tick;
            _subscribed = true;
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            if (!_subscribed)
                return;
            _subscribed = false;
            _entity.onUpdate -= Tick;
        }

        private void Tick(BaseGameEntity entity)
        {
            if (!_entity.IsServer)
            {
                QuietClientHandler();
                // A pure client has nothing more to do here, ever.
                if (_quietedClientHandler)
                    Unsubscribe();
                return;
            }
            if (s_forceListField == null)
                return;
            if (!TryResolve())
                return;
            // The kit ticks these itself whenever it simulates the movement; only the
            // interpolated, client-driven player is left to us.
            if (_movement.Functions.CanSimulateMovement())
                return;
            if (_forces.Count == 0)
                return;
            _listeners.OnPreUpdateForces(_forces);
            _forces.UpdateForces(Time.deltaTime,
                _entity.GetMoveSpeed(MovementState.Forward, ExtraMovementState.None),
                out _, out _);
            _listeners.OnPostUpdateForces(_forces);
        }

        private bool _quietedClientHandler;

        /// <summary>
        /// On a pure client, the kit's `DashAttackHandler` still runs: the owner client ticks
        /// the forces it is sent, so the handler sees Charge's force end and fires the arrival
        /// hit there too - `ApplyDamage` on a client, which cannot touch a synced field and
        /// logs an error and a NullReferenceException for every charge (seen 2026-09-30). The
        /// damage is the server's, where the same handler fires from the tick above, so the
        /// client's copy is switched off once the entity knows which side it is on. Its
        /// interface callbacks still arrive, but with `LateUpdate` off the hit never fires.
        /// </summary>
        private void QuietClientHandler()
        {
            if (_quietedClientHandler || !_entity.IsClient)
                return;
            _quietedClientHandler = true;
            var handler = GetComponent<DashAttackHandler>();
            if (handler != null)
                handler.enabled = false;
        }

        /// <summary>
        /// The movement component and its private list, found lazily: the entity picks its
        /// active movement after it spawns, and the functions object is built in its Start.
        /// </summary>
        private bool TryResolve()
        {
            if (_forces != null && _movement != null && _movement.Functions != null)
                return true;
            _movement = _entity.ActiveMovement as CharacterControllerEntityMovement;
            if (_movement == null || _movement.Functions == null)
                return false;
            _forces = s_forceListField.GetValue(_movement.Functions) as List<EntityMovementForceApplier>;
            if (_forces == null)
            {
                Debug.LogError($"[{nameof(RemoteForceUpkeep)}] \"{ForceListField}\" on {_entity.name} is not a " +
                               "List<EntityMovementForceApplier>; switching off.");
                enabled = false;
                return false;
            }
            // Same set the kit captures in its own Start: the Charge handler among them.
            _listeners = GetComponents<IEntityMovementForceUpdateListener>();
            return true;
        }
    }
}
