// Server Meshing add-on — server-owned characters across a layout change (Slice 2b, slices/slice2b_design.md decision
// 13). A game (or the test rig) that spawns characters with no client connection registers an adopter; when a layout
// change gives the rectangle such a character stands in to another cell, the owner asks the adopter to capture it,
// despawns it, and sends the capture to the new owner, whose adopter spawns it again. Game-neutral: the add-on only
// carries the bytes. Without an adopter registered, server-owned characters stay where they are (and count as
// stragglers in the drain gate).
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>IMeshServerCharacterAdopter moves one kind of server-owned character from one cell to another.</summary>
    public interface IMeshServerCharacterAdopter
    {
        /// <summary>Kind names the adopter on the wire (one short word, no spaces); unique among registered adopters.</summary>
        string Kind { get; }

        /// <summary>
        /// TryCapture is asked on the owning cell for a server-owned character (no connection) about to leave its owned
        /// rectangle by a layout change: true with the state to send when this adopter owns the character, which the
        /// caller then despawns; false when it is not this adopter's.
        /// </summary>
        bool TryCapture(BasePlayerCharacterEntity character, out byte[] state);

        /// <summary>
        /// TryAdopt is asked on the new owner with a capture this adopter made: spawn the character at the pose and
        /// return true, or false (logged; the character is lost, as a despawn would lose it) when it cannot.
        /// </summary>
        bool TryAdopt(byte[] state, Vector3 position, float yaw, MeshRect ownedRect);
    }

    /// <summary>MeshServerCharacterAdopters is the registry of adopters a process uses (registered at start, read by the mesh).</summary>
    public static class MeshServerCharacterAdopters
    {
        private static readonly List<IMeshServerCharacterAdopter> s_adopters = new List<IMeshServerCharacterAdopter>();

        /// <summary>Register adds an adopter; a second with the same Kind replaces the first.</summary>
        public static void Register(IMeshServerCharacterAdopter adopter)
        {
            if (adopter == null)
                return;
            s_adopters.RemoveAll(a => a.Kind == adopter.Kind);
            s_adopters.Add(adopter);
        }

        /// <summary>All is every registered adopter, in registration order.</summary>
        public static IReadOnlyList<IMeshServerCharacterAdopter> All => s_adopters;

        /// <summary>ByKind is the adopter with that Kind, else null.</summary>
        public static IMeshServerCharacterAdopter ByKind(string kind)
        {
            for (int i = 0; i < s_adopters.Count; ++i)
                if (s_adopters[i].Kind == kind)
                    return s_adopters[i];
            return null;
        }
    }
}
