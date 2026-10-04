using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshShipWatchTests
    {
        private const string ZeroLine = "Bot ships 0.00 s: seen 0 records 0 pathed 0 parked 0 unpathed 0 | path n 0 p50 0.000 p95 0.000 p99 0.000 max 0.000 m"
            + " lag p50 0 ms | parked max 0.0000 m | unknownPath 0 badLength 0";

        private const string FullLine = "Bot ships 5.00 s: seen 48 records 4800 pathed 3840 parked 960 unpathed 0 | path n 4 p50 0.050 p95 0.120 p99 0.120 max 0.120 m"
            + " lag p50 20 ms | parked max 0.0004 m | unknownPath 1 badLength 2";

        [Test]
        public void WindowLine_NullPathList_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipWatch.WindowLine(5, 0, 0, 0, 0, 0, null, new List<float>(), 0f, 0, 0));
            Assert.That(ex.ParamName, Is.EqualTo("pathMetres"));
        }

        [Test]
        public void WindowLine_NullLagList_ThrowsNamingIt()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => ServerMeshShipWatch.WindowLine(5, 0, 0, 0, 0, 0, new List<float>(), null, 0f, 0, 0));
            Assert.That(ex.ParamName, Is.EqualTo("lagMs"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_Empty_IsTheZerosLine()
        {
            string line = ServerMeshShipWatch.WindowLine(double.NaN, -1, -1, 0, 0, 0, new List<float>(), new List<float>(), float.NaN, -2, 0);

            Assert.That(line, Is.EqualTo(ZeroLine));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_ExactLine()
        {
            List<float> path = new List<float> { 0.12f, 0.01f, 0.05f, 0.08f };
            List<float> lag = new List<float> { 32f, 20f, 40f, -5f };

            string line = ServerMeshShipWatch.WindowLine(5.0, 48, 4800, 3840, 960, 0, path, lag, 0.0004f, 1, 2);

            Assert.That(line, Is.EqualTo(FullLine));
            Assert.That(line, Does.Not.Contain("{").And.Not.Contain("}").And.Not.Contain("Exception"));
        }

        [Test]
        [SetCulture("de-DE")]
        public void WindowLine_RecordsLeadingTheirPath_KeepTheLagSign()
        {
            string line = ServerMeshShipWatch.WindowLine(5.0, 1, 1, 1, 0, 0, new List<float> { 0.001f }, new List<float> { -12.6f }, 0f, 0, 0);

            StringAssert.Contains(" lag p50 -13 ms ", line);
        }
    }
}
