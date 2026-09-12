// Server Meshing add-on — Spike 1a (ghost simulation-suppression probe).
// See Docs/ServerMeshing/SPIKE1.md.
//
// EXTENSION-ONLY, single-server. Run the game AS HOST (so monsters exist and Entity.IsServer is true),
// drop this on any GameObject, then use its context-menu items to ghostify the nearest monster and
// verify it (a) stops simulating itself and (b) can still be driven externally and renders.

using System.Collections;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    public class Spike1_GhostSuppressionProbe : MonoBehaviour
    {
        [Tooltip("Silence EVERY managed-update component on the entity (except the entity itself), not just the known server-drivers.")]
        public bool stripAllManagedUpdates = false;

        [Tooltip("Seconds to watch an idle ghost for self-movement (should stay put).")]
        public float driftCheckSeconds = 5f;

        [Header("External drive (simulated ghost snapshots)")]
        public float driveRadius = 3f;
        public float driveAngularSpeed = 60f; // deg/sec

        private BaseGameEntity _target;
        private Vector3 _driveCenter;
        private bool _driving;
        private float _driveAngle;

        [ContextMenu("1) Ghostify nearest monster")]
        public void GhostifyNearestMonster()
        {
            _target = FindNearestMonster();
            if (_target == null)
            {
                Debug.LogWarning("[Spike1] No monster found. Run the game AS HOST in a scene that has monsters.");
                return;
            }
            int silenced = GhostSuppression.Ghostify(_target, this, null, stripAllManagedUpdates);
            _driveCenter = _target.transform.position;
            Debug.Log($"[Spike1] Ghostified '{_target.name}': silenced {silenced} component(s), MovementDisabled={_target.MovementDisableState.IsActive}. " +
                      "Now run 'Idle drift check', then 'Toggle external drive'.");
        }

        [ContextMenu("2) Idle drift check")]
        public void StartIdleDriftCheck()
        {
            if (_target == null)
            {
                Debug.LogWarning("[Spike1] Ghostify a monster first.");
                return;
            }
            StartCoroutine(IdleDriftRoutine());
        }

        [ContextMenu("3) Toggle external drive (circle)")]
        public void ToggleDrive()
        {
            if (_target == null)
            {
                Debug.LogWarning("[Spike1] Ghostify a monster first.");
                return;
            }
            _driving = !_driving;
            Debug.Log($"[Spike1] External drive {(_driving ? "ON" : "OFF")}.");
        }

        private IEnumerator IdleDriftRoutine()
        {
            bool wasDriving = _driving;
            _driving = false; // measure self-movement only
            Vector3 start = _target.transform.position;
            float maxDrift = 0f;
            float t = 0f;
            while (t < driftCheckSeconds)
            {
                t += Time.deltaTime;
                maxDrift = Mathf.Max(maxDrift, Vector3.Distance(start, _target.transform.position));
                yield return null;
            }
            _driving = wasDriving;
            Debug.Log($"[Spike1] Idle drift over {driftCheckSeconds}s: maxDrift={maxDrift:0.###} m  -> " +
                      (maxDrift < 0.05f ? "PASS (suppressed — no self-movement)" : "FAIL (still self-simulating — add its component to the suppress list)"));
        }

        private void Update()
        {
            if (!_driving || _target == null)
                return;
            _driveAngle += driveAngularSpeed * Time.deltaTime;
            Vector3 p = _driveCenter + (Quaternion.Euler(0f, _driveAngle, 0f) * (Vector3.forward * driveRadius));
            GhostSuppression.ApplyGhostSnapshot(_target, p, _target.transform.rotation);
        }

        private BaseGameEntity FindNearestMonster()
        {
            BaseMonsterCharacterEntity[] monsters = FindObjectsByType<BaseMonsterCharacterEntity>(FindObjectsSortMode.None);
            BaseGameEntity best = null;
            float bestSqr = float.MaxValue;
            Vector3 here = transform.position;
            foreach (BaseMonsterCharacterEntity m in monsters)
            {
                float sqr = (m.transform.position - here).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = m;
                }
            }
            return best;
        }
    }
}
