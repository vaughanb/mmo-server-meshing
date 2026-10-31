// Server Meshing test rig — registers items the game database does not list, and the vehicles they summon, so the demo
// copy can hand out test content (the two-seat horse of DESIGN 1.39) without the demo's database changing. Sits in
// DemoMesh8kInit beside DemoMesh8kMapInfo (ServerMeshDemo8kBuilder); every process of the copy boots that scene.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>DemoMesh8kItems adds its items, and the vehicles the mount items summon, to the GameInstance once the game data has loaded.</summary>
    [DefaultExecutionOrder(-10000)] // subscribed before the GameInstance starts loading
    public sealed class DemoMesh8kItems : MonoBehaviour
    {
        public BaseItem[] items = new BaseItem[0];

        private void Awake()
        {
            GameInstance.OnGameDataLoadedEvent += Add;
            if (GameInstance.Items.Count > 0)
                Add(); // the data loaded before this scene did
        }

        private void OnDestroy()
        {
            GameInstance.OnGameDataLoadedEvent -= Add;
        }

        private void Add()
        {
            GameInstance.AddItems(items);
            List<VehicleEntity> vehicles = new List<VehicleEntity>();
            foreach (BaseItem item in items)
            {
                if (item is IMountItem mount && mount.VehicleEntity != null)
                    vehicles.Add(mount.VehicleEntity);
            }
            GameInstance.AddVehicleEntities(vehicles);
        }
    }
}
