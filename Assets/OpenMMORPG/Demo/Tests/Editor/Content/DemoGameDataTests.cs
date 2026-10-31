using System;
using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    [Category(TestCategories.Content)]
    public class DemoGameDataTests
    {
        /// <summary>
        /// The kind of game data a GameInstance dictionary holds: the class just below
        /// BaseGameData, so players and monsters (both BaseCharacter) share one.
        /// </summary>
        private static Type Kind(Type type)
        {
            while (type.BaseType != null && type.BaseType != typeof(BaseGameData))
                type = type.BaseType;
            return type;
        }

        /// <summary>
        /// A DataId is a hash of the id, which defaults to the asset name, so two items both
        /// called "Iron Ingot" are one item to the game and whichever loads last wins.
        /// </summary>
        [Test]
        public void NoTwoDemoGameDataOfTheSameKindShareAnId()
        {
            Dictionary<(Type, int), string> seen = new Dictionary<(Type, int), string>();
            List<string> clashes = new List<string>();
            foreach ((BaseGameData data, string path) in DemoTestPaths.Assets<BaseGameData>())
            {
                (Type, int) key = (Kind(data.GetType()), data.DataId);
                if (seen.TryGetValue(key, out string other))
                    clashes.Add($"{key.Item1.Name} \"{data.Id}\": {other} and {path}");
                else
                    seen.Add(key, path);
            }
            Assert.That(clashes, Is.Empty);
        }
    }
}
