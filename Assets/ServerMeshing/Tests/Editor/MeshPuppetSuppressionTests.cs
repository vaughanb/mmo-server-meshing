using System;
using System.Collections.Generic;
using Insthync.ManagedUpdating;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshPuppetSuppressionTests
    {
        [Test]
        public void PuppetExtraTypeNames_UndefinedMotion_ThrowsNamingTheMotion()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => MeshPuppetMotionSetting.PuppetExtraTypeNames((MeshPuppetMotion)7));

            Assert.That(thrown.ParamName, Is.EqualTo("motion"));
        }

        [Test]
        public void PuppetExtraTypeNames_Steered_IsEmptySoTodaysDenyListStays()
        {
            IReadOnlyList<string> names = MeshPuppetMotionSetting.PuppetExtraTypeNames(MeshPuppetMotion.Steered);

            Assert.That(names, Is.Empty);
        }

        [Test]
        public void PuppetExtraTypeNames_Kinematic_IsTheItemLockComponent()
        {
            IReadOnlyList<string> names = MeshPuppetMotionSetting.PuppetExtraTypeNames(MeshPuppetMotion.Kinematic);

            Assert.That(names, Is.EqualTo(new[] { typeof(PlayerCharacterItemLockAndExpireComponent).Name }));
        }

        [Test]
        public void PuppetExtraTypeNames_KinematicNames_AreManagedUpdatesTheDefaultListDoesNotSilence()
        {
            Assert.That(typeof(IManagedUpdate).IsAssignableFrom(typeof(PlayerCharacterItemLockAndExpireComponent)), Is.True,
                "Ghostify silences managed updates by type name; a name that is not one silences nothing");
            foreach (string name in MeshPuppetMotionSetting.PuppetExtraTypeNames(MeshPuppetMotion.Kinematic))
                Assert.That(GhostSuppression.DefaultSuppressTypeNames, Does.Not.Contain(name),
                    "only the recorded extras bring it back at Unghostify, so it must not be silenced for steered puppets too");
        }

        [TestCase(MeshPuppetMotion.Steered)]
        [TestCase(MeshPuppetMotion.Kinematic)]
        public void PuppetExtraTypeNames_RepeatedCalls_ReturnTheSameInstance(MeshPuppetMotion motion)
        {
            Assert.That(MeshPuppetMotionSetting.PuppetExtraTypeNames(motion), Is.SameAs(MeshPuppetMotionSetting.PuppetExtraTypeNames(motion)));
        }
    }
}
