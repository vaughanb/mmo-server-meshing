using System.Collections.Generic;
using MultiplayerARPG;
using NUnit.Framework;
using OpenMMORPG.Tests;

namespace OpenMMORPG.Demo.Tests
{
    /// <summary>
    /// Fields on the kit's entity templates that silently win over the data the demo sets up
    /// (2026-09-15). After cloning a template, these are the ones to clear or fill.
    /// </summary>
    [Category(TestCategories.Content)]
    public class DemoEntityPrefabTests
    {
        /// <summary>
        /// A plain entity animates only through its serialized `model`, which the kit fills with
        /// GetComponent, not GetComponentInChildren. The model sits on a child, so every NPC stood
        /// frozen and the patrolling guard slid along with still legs.
        /// </summary>
        [Test]
        public void EveryNpcWithAModelHasItWired()
        {
            List<string> broken = new List<string>();
            foreach ((NpcEntity npc, string path) in DemoTestPaths.Prefabs<NpcEntity>())
            {
                if (npc.GetComponentInChildren<GameEntityModel>(true) != null &&
                    TestReflection.GetField<GameEntityModel>(npc, "model") == null)
                    broken.Add(path);
            }
            Assert.That(broken, Is.Empty, "NPCs that will never animate");
        }

        /// <summary>
        /// `entityTitle` overrides the monster's data title, and the kit's template ships it as
        /// "Enemy", so every bandit, marauder and cultist wore the same nameplate.
        /// </summary>
        [Test]
        public void MonstersShowTheirOwnName()
        {
            List<string> broken = new List<string>();
            foreach ((BaseMonsterCharacterEntity monster, string path) in DemoTestPaths.Prefabs<BaseMonsterCharacterEntity>())
            {
                string title = TestReflection.GetField<string>(monster, "entityTitle");
                if (!string.IsNullOrEmpty(title))
                    broken.Add($"{path}: \"{title}\"");
            }
            Assert.That(broken, Is.Empty, "monster prefabs whose entityTitle hides their MonsterCharacter title");
        }
    }
}
