using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>The small pure rules of Slice 5's rig and login: the hello fault, the editor role switch, the login's wait.</summary>
    public class MeshRecoveryRigTests
    {
        [Test]
        public void HelloFires_OnTheNamedCellsNthHelloOnly()
        {
            Assert.That(MeshFault.HelloFires("C:2", "C", 2), Is.True);
            Assert.That(MeshFault.HelloFires("C:2", "C", 1), Is.False);
            Assert.That(MeshFault.HelloFires("C:2", "C", 3), Is.False);
            Assert.That(MeshFault.HelloFires("C:2", "C.1", 2), Is.False);
            Assert.That(MeshFault.HelloFires("C.1:1", "C.1", 1), Is.True);
            Assert.That(MeshFault.HelloFires("C", "C", 1), Is.False, "no count");
            Assert.That(MeshFault.HelloFires("C:0", "C", 0), Is.False, "counts start at 1");
            Assert.That(MeshFault.HelloFires("C:x", "C", 1), Is.False);
            Assert.That(MeshFault.HelloFires(null, "C", 1), Is.False);
        }

        [Test]
        public void EditorRole_ClientOnlyOnZero()
        {
            Assert.That(ServerMeshEditorRole.ClientOnly("0"), Is.True);
            Assert.That(ServerMeshEditorRole.ClientOnly(" 0 "), Is.True);
            Assert.That(ServerMeshEditorRole.ClientOnly("1"), Is.False);
            Assert.That(ServerMeshEditorRole.ClientOnly(""), Is.False);
            Assert.That(ServerMeshEditorRole.ClientOnly(null), Is.False);
            Assert.That(ServerMeshEditorRole.Line, Is.EqualTo("Editor role: client only (MESH_EDITOR_SERVERS=0)"));
        }

        [Test]
        public void RecoveryWait_KeepsWaitingWithinTheBudget_AndSaysWhatItDid()
        {
            Assert.That(MeshRecoveryWait.KeepWaiting(true, 3f, 15f), Is.True);
            Assert.That(MeshRecoveryWait.KeepWaiting(false, 3f, 15f), Is.False);
            Assert.That(MeshRecoveryWait.KeepWaiting(true, 15f, 15f), Is.False);
            Assert.That(MeshRecoveryWait.Line(false, false, 0f, out _), Is.Null);
            Assert.That(MeshRecoveryWait.Line(true, false, 2.5f, out bool warning), Is.EqualTo("Login routing: waited 2.5 s for the layout recovery"));
            Assert.That(warning, Is.False);
            Assert.That(MeshRecoveryWait.Line(true, true, 15f, out warning), Is.EqualTo("Login routing: the layout recovery is not done after 15 s; routing by the layout as it stands"));
            Assert.That(warning, Is.True);
        }

        [Test]
        public void CommandFile_StillParsesFromTheRuntimeNamespace()
        {
            Assert.That(ServerMeshLayoutCommandFile.TryParse("5 split C x 0 + C.1", out string seq, out string line, out _), Is.True);
            Assert.That((seq, line), Is.EqualTo(("5", "5 split C x 0 + C.1")));
            Assert.That(ServerMeshLayoutCommandFile.StatusLine("auto", "Merge merge-1: resumed at DrainGate after a central restart"), Is.EqualTo("auto Merge merge-1: resumed at DrainGate after a central restart"));
        }
    }
}
