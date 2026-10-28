using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// A condition on a building socket: something may only be built there once other
    /// sockets are filled. The homestead's roof uses it - a roof needs at least one wall
    /// standing on every side of its foundation - so a roof cannot be put up over bare
    /// boards and left hanging in the air.
    ///
    /// This is the kit's extension point for exactly this kind of rule
    /// (`BaseBuildConditionForBuildingArea`, listed in a `BuildingArea`'s `buildConditions`),
    /// and the kit ships only one of its own, which is about stacking. It is here partly
    /// because the roof needs it and partly to show where such a rule goes.
    ///
    /// **It runs on the client, and only there.** The kit asks a socket's conditions while
    /// the ghost is being aimed, to colour it and to decide whether the click places it;
    /// the server's construct handler trusts the position it is sent and never looks at
    /// sockets. That is the kit's design rather than this condition's, and it means a rule
    /// like this is a guide for honest players, not a lock.
    ///
    /// A socket counts as filled when a finished building of the right type stands in its
    /// trigger volume. Measured with physics rather than read off the parent's list of
    /// children, because that list is kept by the server and the client never has it.
    /// </summary>
    public class RequireBuildingsAround : BaseBuildConditionForBuildingArea
    {
        [System.Serializable]
        public class Side
        {
            [Tooltip("Sockets along one side. The side counts as built when any one of them is filled.")]
            public BuildingArea[] areas = new BuildingArea[0];
        }

        [Tooltip("Every side must have at least one of its sockets filled.")]
        public Side[] sides = new Side[0];

        [Tooltip("The building type that fills a socket.")]
        public string requiredType = "Wall";

        private readonly Collider[] _found = new Collider[16];

        public override bool AllowToBuild(BuildingArea sourceArea, BuildingEntity newBuilding)
        {
            for (int i = 0; i < sides.Length; ++i)
            {
                if (!AnyFilled(sides[i], sourceArea))
                    return false;
            }
            return true;
        }

        private bool AnyFilled(Side side, BuildingArea sourceArea)
        {
            if (side == null || side.areas == null)
                return false;
            for (int i = 0; i < side.areas.Length; ++i)
            {
                if (Filled(side.areas[i], sourceArea))
                    return true;
            }
            return false;
        }

        private bool Filled(BuildingArea area, BuildingArea sourceArea)
        {
            if (area == null)
                return false;
            var box = area.GetComponent<BoxCollider>();
            if (box == null)
                return false;
            Transform frame = area.transform;
            Vector3 centre = frame.TransformPoint(box.center);
            Vector3 halfExtents = Vector3.Scale(box.size, frame.lossyScale) * 0.5f;
            int count = Physics.OverlapBoxNonAlloc(centre, halfExtents, _found, frame.rotation, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; ++i)
            {
                BuildingEntity building = _found[i].GetComponentInParent<BuildingEntity>();
                if (building == null || building.IsBuildMode)
                    continue;
                // The socket's own foundation stands in its volume too.
                if (sourceArea != null && building == sourceArea.entity)
                    continue;
                List<string> types = building.BuildingTypes;
                if (types != null && types.Contains(requiredType))
                    return true;
            }
            return false;
        }
    }
}
