using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeStatePositions answers anchors and positions from tables (no anchor and the origin by default), counts the questions and can throw (for one object, or for any when throwOnlyFor is 0).</summary>
    internal sealed class MeshFakeStatePositions : IMeshStatePositions
    {
        public readonly Dictionary<long, Vector3> anchors = new Dictionary<long, Vector3>();
        public readonly Dictionary<uint, Vector3> positions = new Dictionary<uint, Vector3>();
        public readonly Dictionary<long, int> anchorCallsBy = new Dictionary<long, int>();
        public int anchorCalls;
        public int positionCalls;
        public bool throws;
        public uint throwOnlyFor;

        public bool TryGetObserverAnchor(long connectionId, out Vector3 anchor)
        {
            anchorCalls++;
            anchorCallsBy.TryGetValue(connectionId, out int calls);
            anchorCallsBy[connectionId] = calls + 1;
            return anchors.TryGetValue(connectionId, out anchor);
        }

        public Vector3 PositionOf(IMeshStateSource source)
        {
            positionCalls++;
            if (throws && (throwOnlyFor == 0 || throwOnlyFor == source.ObjectId))
                throw new InvalidOperationException("the fake positions failed on purpose");
            return positions.TryGetValue(source.ObjectId, out Vector3 position) ? position : Vector3.zero;
        }
    }
}
