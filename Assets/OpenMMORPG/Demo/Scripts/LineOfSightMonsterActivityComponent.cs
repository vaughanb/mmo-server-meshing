using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The kit's monster brain, except that a monster has to be able to *see* a target before
    /// it picks one up on its own.
    ///
    /// The kit's `FindEnemy` is a sphere overlap of `VisualRange` with no occlusion test, so
    /// once the people were made aggressive (2026-10-02) the crypt went off all at once: the
    /// sanctum's cultists and the Hierophant, a storey below the landing and 16-18 m away
    /// through its floor, took the player the moment the gate opened, and every room on the
    /// way was already walking over. The user's report: "detect me through the floor".
    ///
    /// The kit hands every candidate through <see cref="FindOneEnemyFromList"/> before it
    /// becomes a target, so this strikes anything there is no clear line from the monster's
    /// eyes to out of the list first and lets the kit choose from what is left. The line is
    /// blocked by the kit's own attack-obstacle layers (`GameInstance.attackObstacleLayers`,
    /// Default in the demo - the village, the crypt modules and the terrain) plus the building
    /// layer, and ignores triggers (safe areas, warps, the sea). Characters are on their own
    /// layers, so a crowd never hides the one behind it.
    ///
    /// Only *noticing* needs sight. A monster that is hit, or told by its summoner, still
    /// turns on its attacker through the kit's own paths, and a target once taken is kept
    /// round corners. Inside <see cref="hearingRange"/> the wall does not matter either: a
    /// player brushing the far side of a door is heard.
    ///
    /// Put on every monster prefab by `DemoEntityBuilder.GiveSight` (the template's component
    /// with its script swapped in place, as the skill and ladder components are), which the
    /// wildlife builder calls too.
    ///
    /// **The switch is on the rule, not here.** `CombatGameplayRule.monsterLineOfSight` turns the
    /// test on or off for every monster at once and is read live, so it can be flipped on the
    /// `GameplayRule.asset` without a prefab rebuild; a game running the kit's own rule gets the
    /// test on. What stays per monster is the geometry: eye height, chest height and hearing.
    ///
    /// **Noticing is also level-scaled** (2026-10-02, user: "they see me from very far away"),
    /// on World of Warcraft's rule: the rule's `aggroRange` at equal level, a metre less for
    /// every level the target has over the monster and a metre more for every level under,
    /// floored at `aggroRangeMin` and capped by the monster's own visual range. The kit's
    /// overlap still fills the list to the visual range; <see cref="AggroRangeTo"/> prunes it
    /// here. A level-12 warrior is noticed by a level-4 cultist at 7 m, by a level-2 wolf at
    /// 5 m, and by a level-14 Hierophant at the full 17 m.
    ///
    /// **And facing** (2026-10-06, user: "they should only detect the player if looking at
    /// them"): a candidate outside the rule's `aggroFieldOfView` cone in front of the monster is
    /// struck out too, unless it is inside <see cref="hearingRange"/>. This is stricter than WoW,
    /// whose aggro radius is all round (facing only matters for stealth there); 360 on the rule
    /// gives WoW's behaviour back.
    /// </summary>
    public class LineOfSightMonsterActivityComponent : MonsterActivityComponent
    {
        [Header("Demo: sight")]
        [Tooltip("Where the monster looks from, above its feet. A person's eyes; a wolf's are lower but the difference does not matter at these ranges.")]
        public float eyeHeight = 1.5f;

        [Tooltip("Where on the target the look lands, above its feet - the chest, so a low wall hides a crouching nothing but a parapet hides a body.")]
        public float targetHeight = 1.0f;

        [Tooltip("Inside this distance a monster notices a target whether or not it can see it: footsteps on the other side of a door.")]
        public float hearingRange = 3f;

        /// <summary>The demo rule, or null when the game runs another.</summary>
        private static CombatGameplayRule Rule
        {
            get { return GameInstance.Singleton == null ? null : GameInstance.Singleton.GameplayRule as CombatGameplayRule; }
        }

        /// <summary>Whether sight is required right now: the demo rule's switch, or on when the game runs another rule.</summary>
        public static bool LineOfSightEnabled
        {
            get
            {
                CombatGameplayRule rule = Rule;
                return rule == null || rule.monsterLineOfSight;
            }
        }

        /// <summary>
        /// How far this monster notices <paramref name="target"/>: the rule's level-scaled
        /// range (World of Warcraft's: shorter for every level the target has over the
        /// monster, longer for every level under, floored), capped by the monster's own
        /// visual range. Without the demo rule, the visual range as the kit has it.
        /// </summary>
        public float AggroRangeTo(DamageableEntity target)
        {
            float cap = CharacterDatabase != null ? CharacterDatabase.VisualRange : 0f;
            CombatGameplayRule rule = Rule;
            var character = target as BaseCharacterEntity;
            if (rule == null || character == null)
                return cap;
            return Mathf.Min(cap, rule.GetAggroRange(Entity.Level, character.Level));
        }

        protected override bool FindOneEnemyFromList(bool isSummonedAndSummonerExisted, out DamageableEntity enemy)
        {
            bool sight = LineOfSightEnabled;
            for (int i = _enemies.Count - 1; i >= 0; --i)
            {
                DamageableEntity candidate = _enemies[i];
                if (candidate == null)
                {
                    _enemies.RemoveAt(i);
                    continue;
                }
                // Too far for one of this level to be noticed by one of ours (the overlap
                // that filled the list only knows the visual range) ...
                float range = AggroRangeTo(candidate);
                if (Vector3.Distance(Entity.EntityTransform.position, candidate.EntityTransform.position) > range)
                {
                    _enemies.RemoveAt(i);
                    continue;
                }
                // ... or in range but behind the monster ...
                if (!IsInFront(candidate))
                {
                    _enemies.RemoveAt(i);
                    continue;
                }
                // ... or in front and in range but behind a wall.
                if (sight && !CanSee(candidate))
                    _enemies.RemoveAt(i);
            }
            return base.FindOneEnemyFromList(isSummonedAndSummonerExisted, out enemy);
        }

        /// <summary>
        /// True when the target is inside the rule's field-of-view cone ahead of the monster,
        /// measured flat (a target up a slope is not "above" the cone), or close enough to be heard.
        /// Always true without the demo rule or with a 360-degree cone.
        /// </summary>
        public bool IsInFront(DamageableEntity target)
        {
            CombatGameplayRule rule = Rule;
            if (rule == null || rule.aggroFieldOfView >= 360f)
                return true;
            Vector3 toTarget = target.EntityTransform.position - Entity.EntityTransform.position;
            toTarget.y = 0f;
            if (toTarget.magnitude <= hearingRange)
                return true;
            Vector3 forward = Entity.EntityTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
                return true;
            return Vector3.Angle(forward, toTarget) <= rule.aggroFieldOfView * 0.5f;
        }

        /// <summary>
        /// True when nothing solid stands between this monster's eyes and the target's chest,
        /// or the target is close enough to be heard anyway.
        /// </summary>
        public bool CanSee(DamageableEntity target)
        {
            Vector3 eye = Entity.EntityTransform.position + Vector3.up * eyeHeight;
            Vector3 chest = target.EntityTransform.position + Vector3.up * targetHeight;
            if (Vector3.Distance(eye, chest) <= hearingRange)
                return true;
            int mask = CurrentGameInstance.GetAttackObstacleLayerMask() | CurrentGameInstance.buildingLayer.Mask;
            return !Physics.Linecast(eye, chest, mask, QueryTriggerInteraction.Ignore);
        }
    }
}
