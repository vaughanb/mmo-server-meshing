// Server Meshing test rig — a stand-in ship's bolt on the server (Phase 0 step 7), modelled on the space game's
// ShipProjectileVisual at commit 1a3a58d9 (read there, not copied): instantiated per barrel per shot, an allocating
// RaycastAll on every layer each frame, its own ship skipped, destroyed on any hit or at its range. Pooled mode
// (MESH_SHIP_PROJECTILES=pooled) is the rig's alternative: a pool and RaycastNonAlloc into one shared buffer.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshShipProjectile is one server bolt (not networked). Launch takes one from the template (instantiated) or
    /// the pool; each frame it casts its step, skipping hits under its own ship's root; the nearest remaining hit ends it,
    /// and on a ship of the other team it takes BoltDamage and sends an impact. It ends at its range otherwise.
    /// </summary>
    public sealed class ServerMeshShipProjectile : MonoBehaviour
    {
        /// <summary>TemplateObjectName names the inactive template every instantiated bolt is copied from.</summary>
        public const string TemplateObjectName = "[Mesh] ShipBoltTemplate";
        /// <summary>SharedHitCapacity is the pooled mode's shared hit buffer size.</summary>
        public const int SharedHitCapacity = 16;

        private static readonly RaycastHit[] s_hits = new RaycastHit[SharedHitCapacity];
        private static readonly Stack<ServerMeshShipProjectile> s_pool = new Stack<ServerMeshShipProjectile>(256);
        private static ServerMeshShipProjectile s_template;

        private ServerMeshShipCombat _source;
        private Transform _ignoreRoot;
        private Vector3 _velocity;
        private float _range;
        private float _travelled;
        private bool _pooled;
        private bool _flying;

        /// <summary>
        /// EnsureTemplate builds the inactive template (a bare GameObject with this component, DontDestroyOnLoad) once; the
        /// ship load calls it when it starts, so the first shot pays no build.
        /// </summary>
        public static void EnsureTemplate()
        {
            if (s_template != null)
                return;
            GameObject go = new GameObject(TemplateObjectName);
            go.SetActive(false);
            DontDestroyOnLoad(go);
            s_template = go.AddComponent<ServerMeshShipProjectile>();
        }

        /// <summary>
        /// Launch fires one bolt from <paramref name="origin"/> at <paramref name="velocity"/> for <paramref name="range"/>
        /// metres, ignoring hits under <paramref name="ignoreRoot"/>: an Instantiate of the template, or a pooled bolt when
        /// <paramref name="pooled"/>.
        /// </summary>
        public static void Launch(ServerMeshShipCombat source, Vector3 origin, Vector3 velocity, float range, Transform ignoreRoot, bool pooled)
        {
            EnsureTemplate();
            ServerMeshShipProjectile bolt = null;
            while (pooled && bolt == null && s_pool.Count > 0)
                bolt = s_pool.Pop();
            if (bolt == null)
            {
                bolt = Instantiate(s_template, origin, Quaternion.identity);
                if (pooled)
                    DontDestroyOnLoad(bolt.gameObject);
            }
            bolt.transform.position = origin;
            bolt._source = source;
            bolt._ignoreRoot = ignoreRoot;
            bolt._velocity = velocity;
            bolt._range = range;
            bolt._travelled = 0f;
            bolt._pooled = pooled;
            bolt._flying = true;
            bolt.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (!_flying)
                return;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Step(Time.deltaTime);
            ServerMeshShipCounters.Current.BoltMs += (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private void Step(float deltaTime)
        {
            Vector3 step = _velocity * deltaTime;
            float length = step.magnitude;
            Vector3 position = transform.position;
            if (length > 0f && TryNearestHit(position, step / length, length, out RaycastHit hit))
            {
                OnHit(hit);
                End();
                return;
            }
            transform.position = position + step;
            _travelled += length;
            if (_travelled >= _range)
                End();
        }

        private bool TryNearestHit(Vector3 position, Vector3 direction, float length, out RaycastHit best)
        {
            best = default;
            bool found = false;
            float bestDistance = float.MaxValue;
            if (_pooled)
            {
                int count = Physics.RaycastNonAlloc(position, direction, s_hits, length);
                for (int i = 0; i < count; ++i)
                    Consider(s_hits[i], ref best, ref bestDistance, ref found);
                return found;
            }
            RaycastHit[] hits = Physics.RaycastAll(position, direction, length);
            for (int i = 0; i < hits.Length; ++i)
                Consider(hits[i], ref best, ref bestDistance, ref found);
            return found;
        }

        private void Consider(RaycastHit hit, ref RaycastHit best, ref float bestDistance, ref bool found)
        {
            if (_ignoreRoot != null && hit.collider.transform.IsChildOf(_ignoreRoot))
                return;
            if (hit.distance >= bestDistance)
                return;
            bestDistance = hit.distance;
            best = hit;
            found = true;
        }

        private void OnHit(RaycastHit hit)
        {
            ServerMeshShipCombat target = hit.collider.GetComponentInParent<ServerMeshShipCombat>();
            if (target == null || _source == null || target.Team == _source.Team || target.ShipDestroyed)
                return;
            target.TakeShipDamage(ServerMeshShipCombat.BoltDamage);
            target.ImpactAt(hit.point);
            ServerMeshShipCounters.Current.Hits++;
        }

        private void End()
        {
            _flying = false;
            _source = null;
            _ignoreRoot = null;
            if (_pooled)
            {
                gameObject.SetActive(false);
                s_pool.Push(this);
                return;
            }
            Destroy(gameObject);
        }
    }
}
