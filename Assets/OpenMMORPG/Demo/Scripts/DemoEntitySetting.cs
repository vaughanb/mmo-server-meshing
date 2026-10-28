using UnityEngine;

namespace MultiplayerARPG.Demo
{
    /// <summary>
    /// The kit's default entity setting, except that monsters are not given a dash handler.
    ///
    /// `DefaultEntitySetting` adds a `DashAttackHandler` to every player and every monster as
    /// it spawns, unconfigured - source type None, data id 0, no transform. An unconfigured
    /// handler switches itself off in `Start`, but the movement system still calls it on
    /// every force, and it matches any force whose source is also None/0 - which is exactly
    /// what `SkillKnockback` applies. It then dereferences its null transform: a
    /// NullReferenceException every time Shield Bash knocks a wolf back.
    ///
    /// Nothing in the demo that fights as a monster dashes, so monsters get no handler at
    /// all. Players are left to the default: their prefabs carry a handler configured for
    /// Charge, and the kit's `GetOrAddComponent` finds that one instead of adding a blank.
    ///
    /// **The override replaces the kit's method rather than extending it**, because today the
    /// method does nothing else. If a kit update gives monsters more components here, this
    /// has to call through for those.
    /// </summary>
    public class DemoEntitySetting : DefaultEntitySetting
    {
        [Header("Weapon sheathing (see DemoWeaponSheathing)")]
        [Tooltip("Characters spawn with their weapons on their back, and the first attack draws them. " +
                 "Off is the kit's own: spawn with them in hand.")]
        public bool startSheathed = true;

        [Tooltip("Seconds with no attack, skill, harvest swing or damage taken, and nothing chasing or aiming, " +
                 "before a drawn weapon is put away again. 0 keeps it out until the player puts it away.")]
        public float idleSheatheDelay = 8f;

        /// <summary>
        /// The kit's own set, plus <see cref="ToggleBuffUpkeep"/>, which switches a mana-fed
        /// toggle off when the mana runs out, and <see cref="RemoteForceUpkeep"/>, which
        /// ends a Charge or knockback on a player the server does not simulate. Added here
        /// rather than on the player prefabs so that no entity rebuild can leave them off.
        ///
        /// <see cref="DemoWeaponSheathing"/> is here for a reason beyond that: this runs in the
        /// entity's `Awake`, before it is spawned, which is the one moment a character's sheathed
        /// flag can be set to its starting value without it being a change anyone sees (see the
        /// component). It has to be on every peer, the server included, for the spawn baseline to
        /// carry the value.
        ///
        /// <see cref="QuiverArrowCount"/> is a network behaviour, so it too must be added before the
        /// spawn, when the identity collects its behaviours - and on every peer: it is the server's count
        /// of the arrows in the pack, which no other client can see, synced for the quiver on the back.
        ///
        /// <see cref="ClassPowerUpkeep"/> drains a warrior's rage out of combat and empties it on
        /// arrival (see <see cref="ClassPower"/>).
        /// </summary>
        public override void InitialPlayerCharacterEntityComponents(BasePlayerCharacterEntity entity)
        {
            base.InitialPlayerCharacterEntityComponents(entity);
            entity.gameObject.GetOrAddComponent<ToggleBuffUpkeep>();
            entity.gameObject.GetOrAddComponent<ClassPowerUpkeep>();
            entity.gameObject.GetOrAddComponent<RemoteForceUpkeep>();
            entity.gameObject.GetOrAddComponent<DemoWeaponSheathing>();
            entity.gameObject.GetOrAddComponent<QuiverArrowCount>();
        }

        /// <summary>
        /// None of the kit's (see the class note), plus <see cref="MonsterKnockbackUpkeep"/>, without
        /// which a knockback on a monster that has stopped to attack moves nothing visible.
        /// </summary>
        public override void InitialMonsterCharacterEntityComponents(BaseMonsterCharacterEntity entity)
        {
            entity.gameObject.GetOrAddComponent<MonsterKnockbackUpkeep>();
        }
    }
}
