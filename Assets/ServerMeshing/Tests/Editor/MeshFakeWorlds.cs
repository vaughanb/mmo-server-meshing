using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeWorlds builds registries shaped like the rig's world for the layout tests.</summary>
    internal static class MeshFakeWorlds
    {
        public const string Map1 = "Map1";
        public const string Map2 = "Map2";

        /// <summary>
        /// A 3x3 grid of 200 m cells from (-300,-300) on Map1 (row-major SW S SE W C E NW N NE) and, on Map2, one explicit
        /// cell C2 at [-100,100)^2 whose map sits 400 m east: world x [300,500), touching E, NE and SE.
        /// </summary>
        public static MeshWorldRegistry RigLike(bool map1Dynamic, float minCellSize = 100f, float band = 80f)
        {
            MeshWorldRegistry world = ScriptableObject.CreateInstance<MeshWorldRegistry>();
            world.dynamicSettings.bandWidth = band;
            world.maps.Add(new MeshMapLayout
            {
                mapId = Map1,
                gridOrigin = new Vector3(-300f, 0f, -300f),
                cellSize = 200f,
                columns = 3,
                rows = 3,
                cellIds = new[] { "SW", "S", "SE", "W", "C", "E", "NW", "N", "NE" },
                cellMode = map1Dynamic ? MeshCellMode.Dynamic : MeshCellMode.Static,
                minCellSize = minCellSize,
            });
            world.maps.Add(new MeshMapLayout
            {
                mapId = Map2,
                worldOrigin = new Vector3(400f, 0f, 0f),
                cellRects = new[] { new MeshCellRect { id = "C2", min = new Vector2(-100f, -100f), size = new Vector2(200f, 200f) } },
            });
            world.Rebuild();
            return world;
        }

        public static MeshLayoutEntry Entry(string id, string baseId, MeshCellState state, MeshRect owned, MeshRect interest, string mapId = Map1)
        {
            return new MeshLayoutEntry { id = id, mapId = mapId, baseId = baseId, state = state, owned = owned, interest = interest };
        }

        public static MeshLayoutEntry Active(string id, string baseId, MeshRect owned) => Entry(id, baseId, MeshCellState.Active, owned, owned);

        public static MeshLayoutSnapshot Snapshot(long version, params MeshLayoutEntry[] entries)
        {
            MeshLayoutSnapshot snapshot = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, version) };
            snapshot.entries.AddRange(entries);
            return snapshot;
        }

        /// <summary>C split at x=0: C keeps the west half, C.1 takes the east half.</summary>
        public static MeshLayoutSnapshot SplitC(long version = 1)
        {
            return Snapshot(version,
                Active("C", "C", new MeshRect(-100f, -100f, 0f, 100f)),
                Active("C.1", "C", new MeshRect(0f, -100f, 100f, 100f)));
        }

        public static List<string> Ids(IEnumerable<MeshCell> cells)
        {
            List<string> ids = new List<string>();
            foreach (MeshCell cell in cells)
                ids.Add(cell.id);
            return ids;
        }

        /// <summary>BandEnvironment clears MESH_BAND for a test and puts it back afterwards.</summary>
        public sealed class BandEnvironment : IDisposable
        {
            private readonly string _saved = Environment.GetEnvironmentVariable(MeshMapNetworkManager.ENV_BAND);

            public BandEnvironment() { Environment.SetEnvironmentVariable(MeshMapNetworkManager.ENV_BAND, null); }

            public void Dispose() { Environment.SetEnvironmentVariable(MeshMapNetworkManager.ENV_BAND, _saved); }
        }
    }
}
