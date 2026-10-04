// Server Meshing test rig — the stand-in ship's class (Phase 0 step 7b, MESH_SHIP_AI_PREFAB): the engine and hull numbers
// of the space game's Ship.prefab and Ship_Enemy.prefab at commit c2eb4154 (read there, not copied). Forces scale with
// MESH_SHIP_SCALE where the engine applies them; torques, drags and hull never scale.
using System;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipClass is one ship class's engine and hull numbers, unscaled. Base is Ship.prefab (players, mirrors,
    /// parked ships and Ship_AI); Enemy is Ship_Enemy.prefab, whose angular drag is the engine's 8 because ShipEngine.Awake
    /// overwrites the prefab's rigidbody damping of 3.
    /// </summary>
    public readonly struct ServerMeshShipClass
    {
        /// <summary>Base is Ship.prefab: force 400, boost 800, drags 3 and 8, torques (23, 23, 30), hull 2,500.</summary>
        public static readonly ServerMeshShipClass Base = new ServerMeshShipClass("base", 400f, 800f, 3f, 8f, new Vector3(23f, 23f, 30f), 2500);

        /// <summary>Enemy is Ship_Enemy.prefab: force 300, boost 600, drags 3 and 8, torques (8, 8, 10), hull 1,500.</summary>
        public static readonly ServerMeshShipClass Enemy = new ServerMeshShipClass("enemy", 300f, 600f, 3f, 8f, new Vector3(8f, 8f, 10f), 1500);

        /// <summary>
        /// ServerMeshShipClass creates a class. A null name throws ArgumentNullException naming it; an empty name, a number
        /// (or a torque component) that is not positive and finite, or a hull of 0 or less throws ArgumentOutOfRangeException
        /// naming the field.
        /// </summary>
        public ServerMeshShipClass(string name, float force, float boostForce, float linearDrag, float angularDrag, Vector3 steeringTorques, int hull)
        {
            if (name == null)
                throw new ArgumentNullException(nameof(name));
            if (name.Trim().Length == 0)
                throw new ArgumentOutOfRangeException(nameof(name), name, "the ship class name must not be empty");
            Positive(force, nameof(force));
            Positive(boostForce, nameof(boostForce));
            Positive(linearDrag, nameof(linearDrag));
            Positive(angularDrag, nameof(angularDrag));
            if (!IsPositive(steeringTorques.x) || !IsPositive(steeringTorques.y) || !IsPositive(steeringTorques.z))
                throw new ArgumentOutOfRangeException(nameof(steeringTorques), steeringTorques, "every steering torque must be a positive finite number");
            if (hull <= 0)
                throw new ArgumentOutOfRangeException(nameof(hull), hull, "the hull must be positive");
            Name = name;
            Force = force;
            BoostForce = boostForce;
            LinearDrag = linearDrag;
            AngularDrag = angularDrag;
            SteeringTorques = steeringTorques;
            Hull = hull;
        }

        /// <summary>Name is the class's setting word: base or enemy (null only for a default value).</summary>
        public string Name { get; }

        /// <summary>Force is the unscaled thrust per ship-local axis.</summary>
        public float Force { get; }

        /// <summary>BoostForce is the unscaled boosted forward thrust.</summary>
        public float BoostForce { get; }

        /// <summary>LinearDrag is the rigidbody's linear damping.</summary>
        public float LinearDrag { get; }

        /// <summary>AngularDrag is the rigidbody's angular damping.</summary>
        public float AngularDrag { get; }

        /// <summary>SteeringTorques is the largest angular acceleration per axis (pitch, yaw, roll), ForceMode.Acceleration.</summary>
        public Vector3 SteeringTorques { get; }

        /// <summary>Hull is the hull's hit points.</summary>
        public int Hull { get; }

        private static void Positive(float value, string name)
        {
            if (!IsPositive(value))
                throw new ArgumentOutOfRangeException(name, value, "must be a positive finite number");
        }

        private static bool IsPositive(float value) => value > 0f && !float.IsInfinity(value);
    }
}
