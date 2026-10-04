using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshDistanceSendPolicyTests
    {
        private const float R0 = 80f;

        private MeshFakeIdentities _identities;

        [SetUp]
        public void SetUp()
        {
            _identities = new MeshFakeIdentities();
        }

        [TearDown]
        public void TearDown()
        {
            _identities.Dispose();
        }

        private static IEnumerable<TestCaseData> BadRadiusCases()
        {
            yield return new TestCaseData(0f).SetName("Constructor_R0Zero_ThrowsNamingIt");
            yield return new TestCaseData(-1f).SetName("Constructor_R0Negative_ThrowsNamingIt");
            yield return new TestCaseData(float.NaN).SetName("Constructor_R0NaN_ThrowsNamingIt");
            yield return new TestCaseData(float.PositiveInfinity).SetName("Constructor_R0Infinite_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(BadRadiusCases))]
        public void Constructor_BadRadius_ThrowsNamingIt(float r0)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshDistanceSendPolicy(r0, 20, 10, 5, 20, MeshStateVariant.Full));

            Assert.That(thrown.ParamName, Is.EqualTo("r0"));
        }

        private static IEnumerable<TestCaseData> BadHzCases()
        {
            yield return new TestCaseData((byte)3, (byte)10, (byte)5, (byte)20, "nearHz").SetName("Constructor_NearHz3_ThrowsNamingIt");
            yield return new TestCaseData((byte)20, (byte)21, (byte)5, (byte)20, "midHz").SetName("Constructor_MidHz21_ThrowsNamingIt");
            yield return new TestCaseData((byte)20, (byte)10, (byte)3, (byte)20, "farHz").SetName("Constructor_FarHz3_ThrowsNamingIt");
            yield return new TestCaseData((byte)20, (byte)10, (byte)5, (byte)21, "ownerHz").SetName("Constructor_OwnerHz21_ThrowsNamingIt");
        }

        [TestCaseSource(nameof(BadHzCases))]
        public void Constructor_HzOutsideTheSet_ThrowsNamingIt(byte near, byte mid, byte far, byte owner, string field)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => new MeshDistanceSendPolicy(R0, near, mid, far, owner, MeshStateVariant.Full));

            Assert.That(thrown.ParamName, Is.EqualTo(field));
        }

        private static IEnumerable<TestCaseData> TierCases()
        {
            yield return new TestCaseData(0f, false, (byte)20).SetName("Classify_AtTheAnchor_IsNear");
            yield return new TestCaseData(40f, false, (byte)20).SetName("Classify_AtHalfR0_IsNear");
            yield return new TestCaseData(40.01f, false, (byte)10).SetName("Classify_JustPastHalfR0_IsMid");
            yield return new TestCaseData(80f, false, (byte)10).SetName("Classify_AtR0_IsMid");
            yield return new TestCaseData(80.01f, false, (byte)5).SetName("Classify_JustPastR0_IsFar");
            yield return new TestCaseData(float.NaN, false, (byte)20).SetName("Classify_UnknownDistance_IsNear");
            yield return new TestCaseData(500f, true, (byte)2).SetName("Classify_FarOwner_GetsTheOwnerRate");
            yield return new TestCaseData(float.NaN, true, (byte)2).SetName("Classify_OwnerWithUnknownDistance_GetsTheOwnerRate");
        }

        [TestCaseSource(nameof(TierCases))]
        public void Classify_GivesTheTierOfTheDistance(float distance, bool isOwner, byte expectedHz)
        {
            MeshDistanceSendPolicy policy = new MeshDistanceSendPolicy(R0, 20, 10, 5, 2, MeshStateVariant.Full);

            MeshSendRate rate = policy.Classify(new MeshFakeStateSource(42, _identities.New(42)), new MeshObserverContext(7, distance, isOwner));

            Assert.That(rate.Hz, Is.EqualTo(expectedHz));
            Assert.That(rate.IsWithheld, Is.False);
            Assert.That(rate.Variant, Is.EqualTo(MeshStateVariant.Full));
        }

        [Test]
        public void Classify_FarVariantCompact_IsCompactOnlyForACompactSource()
        {
            MeshDistanceSendPolicy policy = new MeshDistanceSendPolicy(R0, 20, 10, 5, 20, MeshStateVariant.Compact);
            MeshObserverContext far = new MeshObserverContext(7, 200f, false);
            MeshObserverContext near = new MeshObserverContext(7, 10f, false);
            MeshFakeCompactStateSource compact = new MeshFakeCompactStateSource(42, _identities.New(42));

            Assert.That(policy.Classify(compact, far).Variant, Is.EqualTo(MeshStateVariant.Compact));
            Assert.That(policy.Classify(compact, near).Variant, Is.EqualTo(MeshStateVariant.Full));
            Assert.That(policy.Classify(new MeshFakeStateSource(43, _identities.New(43)), far).Variant, Is.EqualTo(MeshStateVariant.Full));
            Assert.That(policy.Classify(compact, far).Hz, Is.EqualTo(5));
        }

        [Test]
        public void Properties_KeepTheTiers()
        {
            MeshDistanceSendPolicy policy = new MeshDistanceSendPolicy(R0, 20, 10, 5, 2, MeshStateVariant.Compact);

            Assert.That(policy.R0, Is.EqualTo(R0));
            Assert.That(policy.NearHz, Is.EqualTo(20));
            Assert.That(policy.MidHz, Is.EqualTo(10));
            Assert.That(policy.FarHz, Is.EqualTo(5));
            Assert.That(policy.OwnerHz, Is.EqualTo(2));
            Assert.That(policy.FarVariant, Is.EqualTo(MeshStateVariant.Compact));
        }

        private static IEnumerable<TestCaseData> BadTierTextCases()
        {
            yield return new TestCaseData("junk").SetName("TryParseTiers_Junk_Fails");
            yield return new TestCaseData("20/10").SetName("TryParseTiers_TwoParts_Fails");
            yield return new TestCaseData("20/10/5/2/1").SetName("TryParseTiers_FiveParts_Fails");
            yield return new TestCaseData("3/10/5").SetName("TryParseTiers_HzOutsideTheSet_Fails");
            yield return new TestCaseData("20/-10/5").SetName("TryParseTiers_NegativeHz_Fails");
            yield return new TestCaseData("20//5").SetName("TryParseTiers_EmptyPart_Fails");
        }

        [TestCaseSource(nameof(BadTierTextCases))]
        public void TryParseTiers_BadText_FailsWithTheDefaults(string value)
        {
            bool parsed = MeshDistanceSendPolicy.TryParseTiers(value, out byte near, out byte mid, out byte far, out byte owner, out string error);

            Assert.That(parsed, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(error, Does.Not.Contain("{"));
            Assert.That(new[] { near, mid, far, owner }, Is.EqualTo(new byte[] { 20, 20, 20, 20 }));
        }

        private static IEnumerable<TestCaseData> TierTextCases()
        {
            yield return new TestCaseData(null, new byte[] { 20, 20, 20, 20 }).SetName("TryParseTiers_Null_IsUnset");
            yield return new TestCaseData("", new byte[] { 20, 20, 20, 20 }).SetName("TryParseTiers_Empty_IsUnset");
            yield return new TestCaseData("20/10/5", new byte[] { 20, 10, 5, 20 }).SetName("TryParseTiers_ThreeParts_OwnerDefaultsTo20");
            yield return new TestCaseData("20/10/5/2", new byte[] { 20, 10, 5, 2 }).SetName("TryParseTiers_FourParts_SetsTheOwner");
            yield return new TestCaseData(" 10 / 5 / 0 ", new byte[] { 10, 5, 0, 20 }).SetName("TryParseTiers_Spaces_AreIgnored");
        }

        [TestCaseSource(nameof(TierTextCases))]
        public void TryParseTiers_GoodText_SetsTheTiers(string value, byte[] expected)
        {
            bool parsed = MeshDistanceSendPolicy.TryParseTiers(value, out byte near, out byte mid, out byte far, out byte owner, out string error);

            Assert.That(parsed, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(new[] { near, mid, far, owner }, Is.EqualTo(expected));
        }
    }
}
