using System;
using NUnit.Framework;

namespace OpenMMORPG.KitUpdater.Tests
{
    public class KitUpdateCheckTests
    {
        [TestCase("v1.1.6", "1.1.5", true)]
        [TestCase("v1.2", "1.1.9", true)]
        [TestCase("v2.0.0", "1.10.0", true)]
        [TestCase("v1.10.0", "1.9.0", true)]
        [TestCase("v1.1.5", "1.1.5", false)]
        [TestCase("v1.1.5", "1.1.6", false)]
        [TestCase("v1.1", "1.1.0", false)]
        [TestCase("v1.2.0-rc1", "1.1.5", true)]
        [TestCase("v1.2.0", "develop", false)]
        [TestCase("v1.2.0", "dev", false)]
        [TestCase(null, "1.1.5", false)]
        [TestCase("nightly", "1.1.5", false)]
        public void IsNewer(string tag, string installed, bool newer)
        {
            Assert.That(KitUpdateCheck.IsNewer(tag, installed), Is.EqualTo(newer));
        }

        [Test]
        public void Due_OncePerDay()
        {
            DateTime now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
            Assert.That(KitUpdateCheck.Due(null, now), Is.True, "never checked");
            Assert.That(KitUpdateCheck.Due(At(now.AddHours(-23)), now), Is.False);
            Assert.That(KitUpdateCheck.Due(At(now.AddHours(-24)), now), Is.True);
            Assert.That(KitUpdateCheck.Due(At(now.AddDays(3)), now), Is.True, "a check dated in the future is not trusted");
        }

        private static KitUpdateCheck.Result At(DateTime utc)
        {
            return new KitUpdateCheck.Result { checkedTicks = utc.Ticks };
        }
    }
}
