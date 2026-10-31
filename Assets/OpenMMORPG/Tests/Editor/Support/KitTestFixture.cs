using MultiplayerARPG;
using NUnit.Framework;
using UnityEngine;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// Base class for kit tests. Each test gets fresh <see cref="Objects"/>, a fixed random seed
    /// (the gameplay rules roll crits, hits and blocks with UnityEngine.Random) and empty
    /// game-data slots; all of it is put back afterwards. Derived [SetUp] methods run after this
    /// one and derived [TearDown] methods before it.
    /// </summary>
    public abstract class KitTestFixture
    {
        public const int RandomSeed = 1234;

        private Random.State _randomState;

        protected TestObjects Objects { get; private set; }

        [SetUp]
        public void KitSetUp()
        {
            _randomState = Random.state;
            Random.InitState(RandomSeed);
            RuntimeGameDataSlots.Clear();
            Objects = new TestObjects();
        }

        [TearDown]
        public void KitTearDown()
        {
            Objects.Dispose();
            RuntimeGameDataSlots.Clear();
            Random.state = _randomState;
        }

        /// <summary>Stands up a GameInstance for this test; it is torn down with the test.</summary>
        protected TestGameInstance UseGameInstance()
        {
            return TestGameInstance.Create(Objects);
        }
    }
}
