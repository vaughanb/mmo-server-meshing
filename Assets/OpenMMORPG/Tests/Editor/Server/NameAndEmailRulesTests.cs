using MultiplayerARPG.MMO;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// The rules the login server applies to new usernames, character names, guild names and
    /// emails. A name that slips through with spaces, markup or symbols impersonates another
    /// player in chat and nameplates; a rule that is too strict locks out players whose names use
    /// letters outside A-Z. Lengths are checked by the login server itself (LoginNetworkManagerTests).
    /// </summary>
    [Category(TestCategories.Logic)]
    public class NameAndEmailRulesTests : KitTestFixture
    {
        [TearDown]
        public void TearDown()
        {
            NameExtensions.overrideUsernameValidating = null;
            NameExtensions.overrideCharacterNameValidating = null;
            NameExtensions.overrideGuildNameValidating = null;
        }

        [TestCase("Alice")]
        [TestCase("alice_01")]
        [TestCase("ALICE")]
        public void UsernamesOfLettersDigitsAndUnderscoresAreAccepted(string name)
        {
            Assert.That(NameExtensions.IsValidUsername(name), Is.True);
        }

        [TestCase("alice bob")]
        [TestCase("alice@example.com")]
        [TestCase("alice'--")]
        [TestCase("Zoë")]
        public void UsernamesWithSpacesSymbolsOrNonAsciiLettersAreRefused(string name)
        {
            Assert.That(NameExtensions.IsValidUsername(name), Is.False);
        }

        [TestCase("Rowan")]
        [TestCase("Zoë")]
        [TestCase("Ærin_2")]
        [TestCase("李小龍")]
        [TestCase("Владимир")]
        public void CharacterAndGuildNamesAcceptLettersFromAnyScript(string name)
        {
            Assert.That(NameExtensions.IsValidCharacterName(name), Is.True);
            Assert.That(NameExtensions.IsValidGuildName(name), Is.True);
        }

        [TestCase("Ro wan")]
        [TestCase(" Rowan")]
        [TestCase("Rowan!")]
        [TestCase("<b>Rowan</b>")]
        [TestCase("Rowan\t")]
        [TestCase("Rowan​")]
        public void CharacterAndGuildNamesRefuseSpacesMarkupAndSymbols(string name)
        {
            Assert.That(NameExtensions.IsValidCharacterName(name), Is.False);
            Assert.That(NameExtensions.IsValidGuildName(name), Is.False);
        }

        [Test]
        public void AnOverrideReplacesTheBuiltInRule()
        {
            NameExtensions.overrideCharacterNameValidating = name => name.StartsWith("Sir");

            Assert.That(NameExtensions.IsValidCharacterName("Sir Rowan"), Is.True);
            Assert.That(NameExtensions.IsValidCharacterName("Rowan"), Is.False);
            Assert.That(NameExtensions.IsValidGuildName("Ro wan"), Is.False, "the guild rule is separate");
        }

        [TestCase("a@b.com")]
        [TestCase("first.last+tag@example.co.uk")]
        public void WellFormedEmailsAreAccepted(string email)
        {
            Assert.That(email.IsValidEmail(), Is.True);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("no-at-sign")]
        [TestCase("a@b.")]
        [TestCase("a@b.com ")]
        [TestCase("Alice <a@b.com>")]
        public void MalformedEmailsOrOnesWithExtraTextAreRefused(string email)
        {
            Assert.That(email.IsValidEmail(), Is.False);
        }
    }
}
