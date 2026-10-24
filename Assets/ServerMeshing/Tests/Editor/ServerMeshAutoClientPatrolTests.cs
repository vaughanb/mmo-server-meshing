using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The auto client's patrol lines (DESIGN.md 0.91's soak), read by Tools/ServerMeshing/rigsoak.py.</summary>
    public class ServerMeshAutoClientPatrolTests
    {
        [Test]
        public void PatrolLegLine_IsTheContractFormat()
        {
            Assert.That(ServerMeshAutoClient.PatrolLegLine(3, -70f, true, -69.4f, -30.25f), Is.EqualTo("Patrol leg 3 to x=-70: reached at -69.4,-30.25"));
            Assert.That(ServerMeshAutoClient.PatrolLegLine(4, 70f, false, float.NaN, 0f), Is.EqualTo("Patrol leg 4 to x=70: missed at 0,0"));
        }

        [Test]
        public void PatrolResultHead_IsTheContractFormat()
        {
            Assert.That(ServerMeshAutoClient.PatrolResultHead(120, 2), Is.EqualTo("RESULT patrol: legs=120 missed=2"));
            Assert.That(ServerMeshAutoClient.PatrolResultHead(-1, -1), Is.EqualTo("RESULT patrol: legs=0 missed=0"));
        }
    }
}
