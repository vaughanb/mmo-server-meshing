using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// Per-item grip offsets captured from the AnimationEditing scene, which
    /// <c>DemoItemBuilder</c> applies when it writes each item's <c>equipmentModels</c>.
    ///
    /// This asset exists because `Build Items` rewrites `equipmentModels` wholesale: an
    /// offset typed straight onto an item asset survives exactly until the next rebuild.
    /// Captured values live here instead, where the builder reads them back, so tuning a
    /// grip in the scene is a permanent change rather than one that quietly disappears.
    ///
    /// An entry wins over the builder's own defaults (`BladeFacing`, `ShieldFacing`), so
    /// capturing a blade's grip replaces its stock 80-degree roll entirely — capture the
    /// whole orientation you want, not just the part you changed. Delete an entry to hand
    /// that item back to the defaults.
    /// </summary>
    public class DemoWeaponGripOverrides : ScriptableObject
    {
        /// <summary>Where the builder and the capture tool both expect this asset.</summary>
        public const string AssetPath = "Assets/OpenMMORPG/Demo/GameData/WeaponGripOverrides.asset";

        [System.Serializable]
        public class Entry
        {
            [Tooltip("Item asset name, e.g. IronLongsword. Matched exactly.")]
            public string itemName;
            public Vector3 localPosition = Vector3.zero;
            public Vector3 localEulerAngles = Vector3.zero;
            public Vector3 localScale = Vector3.one;
        }

        [Tooltip("One entry per item whose grip has been tuned. Items with no entry use the builder's defaults.")]
        public List<Entry> entries = new List<Entry>();

        /// <summary>The entry for an item, or null when it has none and should use the defaults.</summary>
        public Entry Find(string itemName)
        {
            if (string.IsNullOrEmpty(itemName))
                return null;
            foreach (Entry entry in entries)
            {
                if (entry != null && entry.itemName == itemName)
                    return entry;
            }
            return null;
        }

        /// <summary>Adds or updates an item's entry. Returns true when something actually changed.</summary>
        public bool Set(string itemName, Vector3 localPosition, Vector3 localEulerAngles, Vector3 localScale)
        {
            if (string.IsNullOrEmpty(itemName))
                return false;
            Entry entry = Find(itemName);
            if (entry == null)
            {
                entries.Add(new Entry
                {
                    itemName = itemName,
                    localPosition = localPosition,
                    localEulerAngles = localEulerAngles,
                    localScale = localScale,
                });
                return true;
            }
            if (entry.localPosition == localPosition &&
                entry.localEulerAngles == localEulerAngles &&
                entry.localScale == localScale)
                return false;
            entry.localPosition = localPosition;
            entry.localEulerAngles = localEulerAngles;
            entry.localScale = localScale;
            return true;
        }
    }
}
