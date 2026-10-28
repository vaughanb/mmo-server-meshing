using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// An arrow at every NPC's feet, pointing where it will look in the game.
    ///
    /// A character the kit drives faces its transform's +Z. That is not what the bind
    /// pose in the editor suggests - these bodies are modelled facing -Z - so without
    /// this an NPC turned by hand to look at a doorway is turned to look away from it.
    /// The arrow follows the transform, so it is right whatever pose the mesh is in.
    /// </summary>
    public static class NpcGizmo
    {
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void Draw(NpcEntity npc, GizmoType type)
        {
            Transform t = npc.transform;
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.color = selected ? Color.yellow : new Color(1f, 0.75f, 0.2f, 0.9f);
            Vector3 feet = t.position + Vector3.up * 0.05f;
            Vector3 tip = feet + t.forward * 1.2f;
            Gizmos.DrawLine(feet, tip);
            Gizmos.DrawLine(tip, tip - t.forward * 0.3f + t.right * 0.18f);
            Gizmos.DrawLine(tip, tip - t.forward * 0.3f - t.right * 0.18f);
            Handles.Label(t.position + Vector3.up * 2.1f, npc.name);
        }

        /// <summary>The loop a patrolling guard walks, so it can be seen and dragged about.</summary>
        [DrawGizmo(GizmoType.NonSelected | GizmoType.Selected | GizmoType.Pickable)]
        private static void Draw(MultiplayerARPG.NpcPatrol patrol, GizmoType type)
        {
            if (patrol.waypoints == null || patrol.waypoints.Length == 0)
                return;
            bool selected = (type & GizmoType.Selected) != 0;
            Gizmos.color = selected ? Color.cyan : new Color(0.3f, 0.8f, 0.9f, 0.7f);
            for (int i = 0; i < patrol.waypoints.Length; ++i)
            {
                Vector3 a = patrol.waypoints[i] + Vector3.up * 0.1f;
                Vector3 b = patrol.waypoints[(i + 1) % patrol.waypoints.Length] + Vector3.up * 0.1f;
                Gizmos.DrawLine(a, b);
                Gizmos.DrawSphere(a, 0.15f);
            }
        }
    }
}
