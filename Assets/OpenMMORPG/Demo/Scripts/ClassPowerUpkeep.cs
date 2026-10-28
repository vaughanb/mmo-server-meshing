using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The half of rage the gameplay rule cannot do: letting it drain away out of combat, and
    /// starting a character's session with none (see <see cref="ClassPower"/>).
    ///
    /// **Why not the rule's own drain.** `GetDecreasingMpPerSeconds` would do the arithmetic, but
    /// the kit pays that drain out through `OnBuffMpDecrease`, which floats a combat number over
    /// the character every second and raises `onBuffMpDecrease` - the event
    /// <see cref="ToggleBuffUpkeep"/> reads as "a mana-fed toggle has run dry". So the rage is
    /// taken straight off <c>CurrentMp</c> here, which is synced like any other change to it.
    ///
    /// **Why empty on arrival.** A new character is created with every pool full, and a saved one
    /// comes back with whatever it logged out holding. The kit's respawn is handled by the rule;
    /// this covers logging in and changing map, where a warrior would otherwise walk into the next
    /// fight with a free bar.
    ///
    /// Server only; on every other peer it does nothing. The demo puts it on every player from
    /// <c>DemoEntitySetting</c>.
    /// </summary>
    public class ClassPowerUpkeep : MonoBehaviour
    {
        private BaseCharacterEntity _entity;
        private bool _arrived;
        private float _drain;

        private void Awake()
        {
            _entity = GetComponent<BaseCharacterEntity>();
            if (_entity == null)
                enabled = false;
        }

        private void Update()
        {
            if (!_entity.IsServer)
                return;
            CombatGameplayRule rule = ClassPower.Rule;
            if (rule == null || rule.GetClassPower(_entity) != ClassPowerType.Rage)
                return;

            if (!_arrived)
            {
                _arrived = true;
                _entity.CurrentMp = 0;
                return;
            }

            if (_entity.CurrentMp <= 0 || rule.IsInCombat(_entity))
            {
                _drain = 0f;
                return;
            }
            _drain += rule.rageDecayPerSecond * Time.deltaTime;
            if (_drain < 1f)
                return;
            int amount = (int)_drain;
            _drain -= amount;
            _entity.CurrentMp = Mathf.Max(0, _entity.CurrentMp - amount);
        }
    }
}
