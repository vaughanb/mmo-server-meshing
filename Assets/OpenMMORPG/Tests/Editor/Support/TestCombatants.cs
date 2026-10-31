using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using LiteNetLibManager;
using MultiplayerARPG;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Character entities for the gameplay rule's combat and recovery maths, which take
    /// BaseCharacterEntity and read the entity's level and its cached stats. Outside play mode an
    /// entity's Awake never runs, but its sync fields (level, food, hp) work, so this builds a bare
    /// entity and hands it ready-made stats: a CharacterDataCache with the given CharacterStats is
    /// put where GetCaches() and CachedData look for it, and nothing marks it for recaching, so the
    /// kit never works the stats out from game data. The cache entry is removed with the test.
    ///
    /// Only what the stats cache holds is faked. Anything that reads the character's game data
    /// (GetDatabase, its equipment) still needs that data registered in a TestGameInstance.
    /// </summary>
    public static class TestCombatants
    {
        /// <summary>
        /// A living character at <paramref name="level"/> with <paramref name="stats"/>. Food and
        /// water start full enough that the default rule's hunger and thirst do not apply.
        /// </summary>
        public static T Create<T>(TestObjects objects, int level = 1, CharacterStats stats = default)
            where T : BaseCharacterEntity
        {
            T entity = objects.NewGameObject(typeof(T).Name).AddComponent<T>();
            entity.Level = level;
            entity.CurrentHp = 1;
            entity.CurrentFood = 100;
            entity.CurrentWater = 100;
            SetStats(entity, stats);
            int hashCode = entity.GetHashCode();
            objects.Own(new CacheEntry(hashCode));
            return entity;
        }

        /// <summary>Replaces the character's cached stats.</summary>
        public static void SetStats(BaseCharacterEntity entity, CharacterStats stats)
        {
            CharacterDataCache cache = new CharacterDataCache();
            TestReflection.Invoke(cache, "SetStats", stats);
            // The rule walks the passive skills after a hit; an empty set, not null.
            TestReflection.Invoke(cache, "SetSkills", new Dictionary<BaseSkill, int>());
            entity.CachedData = cache;
            Caches()[entity.GetHashCode()] = cache;
        }

        /// <summary>
        /// Gives the character a network object id. Every unspawned entity has id 0, so anything
        /// keyed by object id treats two of them as one.
        /// </summary>
        public static void SetObjectId(BaseCharacterEntity entity, uint objectId)
        {
            LiteNetLibIdentity identity = entity.GetComponent<LiteNetLibIdentity>();
            TestReflection.SetField(identity, "networkedObjectId", objectId);
        }

        private static ConcurrentDictionary<int, CharacterDataCache> Caches()
        {
            return TestReflection.GetStaticField<ConcurrentDictionary<int, CharacterDataCache>>(
                typeof(CharacterDataCacheManager), "s_caches");
        }

        private sealed class CacheEntry : IDisposable
        {
            private readonly int _hashCode;

            public CacheEntry(int hashCode)
            {
                _hashCode = hashCode;
            }

            public void Dispose()
            {
                Caches().TryRemove(_hashCode, out _);
            }
        }
    }
}
