#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
using System.Collections;
using System.Collections.Generic;
using MultiplayerARPG;
using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The first user bug report (v1.1.2): after a quest turn-in and logout, level, exp and
    /// position rolled back while the quest saved. A newer partial save (Quests only) replaced the
    /// waiting full save, so the characters row was never written. Fixed in v1.1.3.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class DatabaseCharacterSaveSchedulerTests : KitTestFixture
    {
        private DatabaseCharacterSaveScheduler _scheduler;
        private PlayerCharacterData _character;

        [SetUp]
        public void SetUp()
        {
            // Never started, so nothing is saved and what waits can be read back.
            _scheduler = new DatabaseCharacterSaveScheduler(null, 1, 100, 1, 20f, 5f);
            _character = TestCharacters.NewPlayer();
        }

        private UpdateCharacterReq Save(TransactionUpdateCharacterState state, List<CharacterBuff> summonBuffs = null, bool deleteStorageReservation = false)
        {
            return new UpdateCharacterReq()
            {
                State = state,
                CharacterData = _character,
                SummonBuffs = summonBuffs,
                DeleteStorageReservation = deleteStorageReservation,
            };
        }

        private UpdateCharacterReq Waiting()
        {
            IDictionary pending = TestReflection.GetField<IDictionary>(_scheduler, "_pending");
            Assert.That(pending.Contains(_character.Id), "a save is waiting");
            return TestReflection.GetField<UpdateCharacterReq>(pending[_character.Id], "Request");
        }

        [Test]
        public void ANewerPartialSaveKeepsTheWaitingSavesTables()
        {
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.All));
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.Quests));

            Assert.That(Waiting().State, Is.EqualTo(TransactionUpdateCharacterState.All));
        }

        [Test]
        public void SummonsFromTheOlderSaveSurviveANewerSaveWithoutThem()
        {
            List<CharacterBuff> summonBuffs = new List<CharacterBuff>();
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.Summons, summonBuffs));
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.Character));

            UpdateCharacterReq waiting = Waiting();
            Assert.That(waiting.State, Is.EqualTo(TransactionUpdateCharacterState.Summons | TransactionUpdateCharacterState.Character));
            Assert.That(waiting.SummonBuffs, Is.SameAs(summonBuffs));
        }

        [Test]
        public void AStorageReservationDeleteIsNotForgotten()
        {
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.Character, deleteStorageReservation: true));
            _scheduler.Enqueue(Save(TransactionUpdateCharacterState.Items));

            Assert.That(Waiting().DeleteStorageReservation, Is.True);
        }
    }
}
#endif
