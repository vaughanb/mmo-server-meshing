using MultiplayerARPG.MMO.Mesh.Editor;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshCrossingSetupChecksTests
    {
        [Test]
        public void SteeredShows_OnlyWhenSteeredOnAStaticMap()
        {
            Assert.That(MeshCrossingSetupChecks.SteeredShows(MeshPuppetMotion.Steered, true), Is.True);
            Assert.That(MeshCrossingSetupChecks.SteeredShows(MeshPuppetMotion.Steered, false), Is.False, "a Dynamic map's cells are Kinematic anyway");
            Assert.That(MeshCrossingSetupChecks.SteeredShows(MeshPuppetMotion.Kinematic, true), Is.False);
        }

        [TestCase(20, true)]
        [TestCase(30, true)]
        [TestCase(59, true)]
        [TestCase(60, false)]
        [TestCase(120, false)]
        [TestCase(0, false)]
        [TestCase(-1, false)]
        public void RateIsLow(int rate, bool expected)
        {
            Assert.That(MeshCrossingSetupChecks.RateIsLow(rate), Is.EqualTo(expected));
        }

        [Test]
        public void LowRateTexts_NameTheRate()
        {
            Assert.That(MeshCrossingSetupChecks.LowFrameRateText(30), Does.Contain("rate 30"));
            Assert.That(MeshCrossingSetupChecks.LowNetworkTickText(20), Does.Contain("updateFps) 20"));
        }

        [Test]
        public void NewMapManager_IsKinematic()
        {
            GameObject host = new GameObject("MeshCrossingSetupChecksTests");
            try
            {
                Assert.That(host.AddComponent<MeshMapNetworkManager>().puppetMotion, Is.EqualTo(MeshPuppetMotion.Kinematic));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }
    }
}
