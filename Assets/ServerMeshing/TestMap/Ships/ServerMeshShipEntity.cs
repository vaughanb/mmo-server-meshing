// Server Meshing test rig — the stand-in ship entity (Phase 0 step 7), modelled on the space game's ShipEntity and
// Ship.prefab at commit 1a3a58d9 and Ship_Enemy.prefab at c2eb4154 (read there, not copied): a one-seat VehicleEntity
// whose hull is the kit's CurrentHp, filled from its ship class, with a fixed scaled visible range, spawn-only
// flight-path fields, and registration with the kit under a ship key only.
using System;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipEntity is the stand-in ship: one seat, canBeAttacked false (kit damage never reaches it; ship combat
    /// owns the hull, whose size is the ship class's). Configure sets it up on the server before NetworkSpawn. It never overrides
    /// NeedsServerUpdateWhenUnobserved: its movement, pilot and combat run on their own Unity updates, as the space ship's do.
    /// </summary>
    public class ServerMeshShipEntity : VehicleEntity
    {
        /// <summary>PrefabRefPath is the Resources path of the ServerMeshShipPrefabRef asset.</summary>
        public const string PrefabRefPath = "ServerMeshShipPrefabRef";
        /// <summary>DefaultHull is the hp the prefab builder writes (the space Ship.prefab's 2,500); the hull comes from ShipClass.</summary>
        public const int DefaultHull = 2500;
        /// <summary>BarrelOffset is the right barrel's local position at scale 1; the left one mirrors x.</summary>
        public static readonly Vector3 BarrelOffset = new Vector3(2f, 0f, 6f);

        [SerializeField]
        protected SyncFieldInt flightPath = new SyncFieldInt();
        [SerializeField]
        protected SyncFieldVector3 pathCentre = new SyncFieldVector3();
        [SerializeField]
        [Tooltip("The two barrels bolts leave from (left, right)")]
        protected Transform[] barrels = new Transform[0];

        private static ServerMeshShipEntity s_prefab;
        private static bool s_lateRegistrationLogged;

        private ServerMeshShipMovement _movement;
        private ServerMeshShipCombat _combat;
        private ServerMeshShipPilot _pilot;

        /// <summary>FlightPathIndex is −1 for an AI ship, −2 for a parked ship, else its ServerMeshShipFlightPath index (spawn data only).</summary>
        public int FlightPathIndex => flightPath.Value;

        /// <summary>PathCentre is the flight path's centre, or a parked ship's spawn point (spawn data only).</summary>
        public Vector3 PathCentre => pathCentre.Value;

        /// <summary>Authority is how the server moves the ship; set by Configure (OwnerMirror on clients, where nothing configures it).</summary>
        public ServerMeshShipAuthority Authority { get; private set; }

        /// <summary>Team is 0 or 1; ships shoot ships of the other team.</summary>
        public int Team { get; private set; }

        /// <summary>Scale is the scale Configure gave the ship (null until then).</summary>
        public ServerMeshShipScale Scale { get; private set; }

        /// <summary>Settings is the settings Configure gave the ship (null until then).</summary>
        public ServerMeshShipSettings Settings { get; private set; }

        /// <summary>ShipClass is the class Configure gave the ship: its forces, drags, torques and hull (Base until then).</summary>
        public ServerMeshShipClass ShipClass { get; private set; } = ServerMeshShipClass.Base;

        /// <summary>IsConfigured is true once Configure ran on this instance.</summary>
        public bool IsConfigured { get; private set; }

        /// <summary>SpawnPoint is where Configure found the ship; a respawn teleports it back here.</summary>
        public Vector3 SpawnPoint { get; private set; }

        /// <summary>SpawnRotation is the rotation Configure found the ship at.</summary>
        public Quaternion SpawnRotation { get; private set; } = Quaternion.identity;

        /// <summary>ShipMovement is the ship's movement and state handler.</summary>
        public ServerMeshShipMovement ShipMovement
        {
            get
            {
                if (_movement == null)
                    _movement = GetComponent<ServerMeshShipMovement>();
                return _movement;
            }
        }

        /// <summary>ShipCombat is the ship's shield, hull rules, RPCs and guns.</summary>
        public ServerMeshShipCombat ShipCombat
        {
            get
            {
                if (_combat == null)
                    _combat = GetComponent<ServerMeshShipCombat>();
                return _combat;
            }
        }

        /// <summary>ShipPilot is the ship's AI pilot (enabled only on a ServerAI ship on the server).</summary>
        public ServerMeshShipPilot ShipPilot
        {
            get
            {
                if (_pilot == null)
                    _pilot = GetComponent<ServerMeshShipPilot>();
                return _pilot;
            }
        }

        /// <summary>HullMax is the full hull: the ship class's Hull (the prefab's hp is not read).</summary>
        public int HullMax => ShipClass.Hull;

        /// <summary>DriverObjectId is the object id in the driver seat, 0 when it is empty.</summary>
        public uint DriverObjectId => passengerIds.Count > 0 ? passengerIds[0] : 0;

        /// <summary>BarrelCount is how many barrels the prefab carries.</summary>
        public int BarrelCount => barrels == null ? 0 : barrels.Length;

        /// <summary>Barrel is barrel <paramref name="index"/>'s transform (null when the prefab lacks it).</summary>
        public Transform Barrel(int index) => barrels != null && index >= 0 && index < barrels.Length ? barrels[index] : null;

        /// <summary>InitStats fills the hull with HullMax on the server: canBeAttacked false makes the kit's MaxHp 1.</summary>
        public override void InitStats()
        {
            base.InitStats();
            if (IsServer)
                CurrentHp = HullMax;
        }

        /// <summary>
        /// Configure sets the ship up on the server before NetworkSpawn: its authority, team, flight path index and centre
        /// (spawn data: a value set before spawn travels in the spawn message and never goes dirty), the scaled visible
        /// range, collider and barrels, the ship class (<paramref name="shipClass"/>: its engine, damping and hull), and
        /// enables the pilot only for ServerAI. It throws, naming the field, for authority 3 or above, a team outside 0..1,
        /// an index below −2, or a null scale or settings.
        /// </summary>
        public void Configure(ServerMeshShipAuthority authority, int team, int flightPathIndex, Vector3 centre, ServerMeshShipScale scale, ServerMeshShipSettings settings,
            ServerMeshShipClass shipClass)
        {
            if ((byte)authority > (byte)ServerMeshShipAuthority.Parked)
                throw new ArgumentOutOfRangeException(nameof(authority), authority, "ship authority must be OwnerMirror, ServerAI or Parked");
            if (team < 0 || team > 1)
                throw new ArgumentOutOfRangeException(nameof(team), team, "ship team must be 0 or 1");
            if (flightPathIndex < -2)
                throw new ArgumentOutOfRangeException(nameof(flightPathIndex), flightPathIndex, "flight path index must be -2 (parked), -1 (AI) or a path index");
            if (scale == null)
                throw new ArgumentNullException(nameof(scale));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (shipClass.Name == null)
                throw new ArgumentOutOfRangeException(nameof(shipClass), "the ship class is empty (default); pass ServerMeshShipClass.Base or Enemy");
            Authority = authority;
            Team = team;
            Scale = scale;
            Settings = settings;
            ShipClass = shipClass;
            IsConfigured = true;
            flightPath.Value = flightPathIndex;
            pathCentre.Value = centre;
            // Configure runs on the inactive instance GetObjectInstance returns, before Awake: nothing cached there is read.
            SpawnPoint = transform.position;
            SpawnRotation = transform.rotation;
            Identity.VisibleRange = scale.VisibleRange;
            BoxCollider box = GetComponent<BoxCollider>();
            if (box != null)
            {
                box.size = scale.ColliderSize;
                box.center = scale.ColliderCenter;
            }
            Vector3 barrel = BarrelOffset * scale.Factor;
            if (BarrelCount > 0 && barrels[0] != null)
                barrels[0].localPosition = new Vector3(-barrel.x, barrel.y, barrel.z);
            if (BarrelCount > 1 && barrels[1] != null)
                barrels[1].localPosition = barrel;
            ServerMeshShipPilot pilot = ShipPilot;
            if (pilot != null)
                pilot.enabled = authority == ServerMeshShipAuthority.ServerAI;
        }

        public override void OnNetworkDestroy(byte reasons)
        {
            base.OnNetworkDestroy(reasons);
            // A pooled instance is reused: nothing of this life may leak into the next Configure.
            Authority = ServerMeshShipAuthority.OwnerMirror;
            Team = 0;
            Scale = null;
            Settings = null;
            ShipClass = ServerMeshShipClass.Base;
            IsConfigured = false;
            ServerMeshShipPilot pilot = ShipPilot;
            if (pilot != null)
                pilot.enabled = false;
        }

        /// <summary>
        /// LoadPrefab is the stand-in ship prefab from Resources/ServerMeshShipPrefabRef, cached once found; null with the
        /// reason when the reference asset, its prefab or the prefab's ServerMeshShipEntity is missing.
        /// </summary>
        public static ServerMeshShipEntity LoadPrefab(out string error)
        {
            if (s_prefab != null)
            {
                error = null;
                return s_prefab;
            }
            ServerMeshShipPrefabRef reference = Resources.Load<ServerMeshShipPrefabRef>(PrefabRefPath);
            if (reference == null)
            {
                error = $"no Resources/{PrefabRefPath} asset; build the ship prefab with Server Meshing/Test Rig/Build Ship Prefab";
                return null;
            }
            if (reference.prefab == null)
            {
                error = $"Resources/{PrefabRefPath} holds no prefab; build the ship prefab again";
                return null;
            }
            ServerMeshShipEntity prefab = reference.prefab.GetComponent<ServerMeshShipEntity>();
            if (prefab == null)
            {
                error = $"the prefab {reference.prefab.name} in Resources/{PrefabRefPath} has no {nameof(ServerMeshShipEntity)}";
                return null;
            }
            s_prefab = prefab;
            error = null;
            return prefab;
        }

        /// <summary>
        /// EnsureRegistered makes the kit know the ship prefab: it adds it to GameInstance.VehicleEntities when absent (so
        /// InitPrefabs registers it on every start and scene load) and, with a started <paramref name="manager"/> whose
        /// assets were initialised without it, registers it late through RegisterPrefab (logged once). <paramref name="manager"/>
        /// may be null before any manager exists. False with the reason when the prefab is missing or another vehicle holds
        /// its hash.
        /// </summary>
        public static bool EnsureRegistered(BaseGameNetworkManager manager, out string error)
        {
            ServerMeshShipEntity prefab = LoadPrefab(out error);
            if (prefab == null)
                return false;
            LiteNetLibIdentity identity = prefab.GetComponent<LiteNetLibIdentity>();
            if (identity == null)
            {
                error = $"the ship prefab {prefab.name} has no {nameof(LiteNetLibIdentity)}";
                return false;
            }
            int hash = identity.HashAssetId;
            if (GameInstance.VehicleEntities.TryGetValue(hash, out VehicleEntity known) && known != null)
            {
                if (known != prefab)
                {
                    error = $"prefab hash {hash} already belongs to the vehicle {known.name}; rebuild the ship prefab";
                    return false;
                }
            }
            else
            {
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
                // What AddVehicleEntities does, without its read of the prefab's Identity: that getter's per-frame cache
                // starts at frame 0, so on the first frame of Play (where this runs) it returns null without looking.
                GameInstance.VehicleEntities[hash] = prefab;
                prefab.PrepareRelatesData();
#else
                error = "this build excludes prefab references, so the ship prefab cannot be added to the game's vehicles";
                return false;
#endif
            }
            if (manager != null && manager.Assets != null && manager.Assets.IsInitialized && !IsSpawnable(manager.Assets.spawnablePrefabs, hash))
            {
                manager.Assets.RegisterPrefab(identity);
                if (!s_lateRegistrationLogged)
                {
                    s_lateRegistrationLogged = true;
                    Logging.LogWarning("Mesh", $"Ships: prefab hash {hash} was registered late through RegisterPrefab (it was not a game vehicle when the assets initialised)");
                }
            }
            error = null;
            return true;
        }

        /// <summary>IsSpawnable is true when <paramref name="prefabs"/> holds an identity with <paramref name="hash"/>.</summary>
        public static bool IsSpawnable(LiteNetLibIdentity[] prefabs, int hash)
        {
            if (prefabs == null)
                return false;
            for (int i = 0; i < prefabs.Length; ++i)
            {
                if (prefabs[i] != null && prefabs[i].HashAssetId == hash)
                    return true;
            }
            return false;
        }
    }
}
