using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshCarriedActionTests
    {
        private static MeshCarriedAction RoundTrip(MeshCarriedAction action)
        {
            NetDataWriter writer = new NetDataWriter();
            action.Serialize(writer);
            MeshCarriedAction read = default;
            read.Deserialize(new NetDataReader(writer.CopyData()));
            return read;
        }

        [TestCase(0, 3)]
        [TestCase(1, 2)]
        [TestCase(3, 0)]
        [TestCase(5, 0)]
        public void TriggersAfter_IsWhatIsLeftToPayFor(int paid, int expected)
        {
            MeshCarriedAction action = new MeshCarriedAction { kind = MeshCarriedActionKind.Skill, triggerDurations = new[] { 0.1f, 0.2f, 0.3f } };
            Assert.That(action.TriggersAfter(paid), Is.EqualTo(expected));
        }

        [Test]
        public void TriggersAfter_NothingWithoutTriggers()
        {
            Assert.That(default(MeshCarriedAction).TriggersAfter(0), Is.EqualTo(0));
        }

        [Test]
        public void IsSet_AChargeHasNoTriggers()
        {
            Assert.That(new MeshCarriedAction { kind = MeshCarriedActionKind.Charge }.IsSet, Is.True);
        }

        [Test]
        public void IsSet_AnAttackNeedsItsTriggers()
        {
            Assert.That(new MeshCarriedAction { kind = MeshCarriedActionKind.Attack }.IsSet, Is.False);
            Assert.That(new MeshCarriedAction { kind = MeshCarriedActionKind.Attack, triggerDurations = new[] { 0.1f } }.IsSet, Is.True);
        }

        [Test]
        public void IsSet_NoneIsNot()
        {
            Assert.That(default(MeshCarriedAction).IsSet, Is.False);
        }

        [Test]
        public void Serialize_KeepsACharge()
        {
            MeshCarriedAction read = RoundTrip(new MeshCarriedAction { kind = MeshCarriedActionKind.Charge, weaponHandlingState = (byte)WeaponHandlingState.IsLeftHand });
            Assert.That(read.IsSet, Is.True);
            Assert.That(read.kind, Is.EqualTo(MeshCarriedActionKind.Charge));
            Assert.That(((WeaponHandlingState)read.weaponHandlingState).Has(WeaponHandlingState.IsLeftHand), Is.True);
        }

        [Test]
        public void Serialize_KeepsASkill()
        {
            MeshCarriedAction read = RoundTrip(new MeshCarriedAction { kind = MeshCarriedActionKind.Skill, seed = 77, triggerDurations = new[] { 0.5f, 0.25f }, skillDataId = 9, skillLevel = 3, itemDataId = 12 });
            Assert.That(read.seed, Is.EqualTo(77));
            Assert.That(read.triggerDurations, Is.EqualTo(new[] { 0.5f, 0.25f }));
            Assert.That(read.skillDataId, Is.EqualTo(9));
            Assert.That(read.skillLevel, Is.EqualTo(3));
            Assert.That(read.itemDataId, Is.EqualTo(12));
        }
    }
}
