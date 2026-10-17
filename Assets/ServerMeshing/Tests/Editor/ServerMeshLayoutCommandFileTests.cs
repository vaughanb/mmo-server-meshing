using MultiplayerARPG.MMO.Mesh.Editor;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class ServerMeshLayoutCommandFileTests
    {
        [Test]
        public void TryParse_OneLine_GivesSeqAndTheWholeLine()
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse("7 split C x 0 + C.1\r\n", out string seq, out string line, out string error);

            Assert.That(ok, Is.True);
            Assert.That(seq, Is.EqualTo("7"));
            Assert.That(line, Is.EqualTo("7 split C x 0 + C.1"));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void TryParse_SkipsBlankAndCommentLines_AndCollapsesSpacing()
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse("\n# written by regress\n  12\tsplit  E x 200 + E.1  \nignored", out string seq, out string line, out _);

            Assert.That(ok, Is.True);
            Assert.That(seq, Is.EqualTo("12"));
            Assert.That(line, Is.EqualTo("12 split E x 200 + E.1"));
        }

        [TestCase((string)null)]
        [TestCase("")]
        [TestCase("  \r\n \n")]
        [TestCase("# only a comment")]
        public void TryParse_NoLine_FailsAsEmpty(string text)
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse(text, out string seq, out string line, out string error);

            Assert.That(ok, Is.False);
            Assert.That(seq, Is.EqualTo(ServerMeshLayoutCommandFile.NoSeq));
            Assert.That(line, Is.Null);
            Assert.That(error, Is.EqualTo("empty command file"));
            Assert.That(ServerMeshLayoutCommandFile.IsBlank(text), Is.True);
        }

        [Test]
        public void TryParse_SeqAlone_FailsWithTheSeq()
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse("9\n", out string seq, out _, out string error);

            Assert.That(ok, Is.False);
            Assert.That(seq, Is.EqualTo("9"));
            Assert.That(error, Is.EqualTo("no command after seq 9"));
            Assert.That(ServerMeshLayoutCommandFile.IsBlank("9\n"), Is.False);
        }

        [Test]
        public void MirroredPayload_KitLoggerLine_IsStrippedToTheMessage()
        {
            string payload = ServerMeshLayoutCommandFile.MirroredPayload(" INFO Mesh [2026-10-16 12:00:01] - Split split-1: e1 staged");

            Assert.That(payload, Is.EqualTo("Split split-1: e1 staged"));
        }

        [Test]
        public void MirroredPayload_KitWarningAndTaggedLines_AreMirrored()
        {
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(" WARN Mesh [2026-10-16 12:00:01] - Layout op refused: maxCellsPerBase"), Is.EqualTo("Layout op refused: maxCellsPerBase"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("[Mesh] Split split-2: done"), Is.EqualTo("Split split-2: done"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("Split split-3: frozen e2 ack timeout"), Is.EqualTo("Split split-3: frozen e2 ack timeout"));
        }

        [Test]
        public void MirroredPayload_OnlyTheFirstLine()
        {
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("[Mesh] Split split-1: done\nsecond line"), Is.EqualTo("Split split-1: done"));
        }

        [TestCase((string)null)]
        [TestCase("")]
        [TestCase(" INFO Mesh [2026-10-16 12:00:01] - Layout v3 applied: C owns x [-100, 0)")]
        [TestCase("[MeshRunner] Layout command 7 handed to the central")]
        [TestCase("Splitting hairs")]
        [TestCase("[Mesh] Handoff START Bot1 (id) zone C->C.1 Split split-1")]
        [TestCase("Layout operation")]
        public void MirroredPayload_OtherLines_AreNotMirrored(string message)
        {
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(message), Is.Null);
        }

        [Test]
        public void StatusLine_PrefixesTheSeq_OnOneLineWithoutBraces()
        {
            Assert.That(ServerMeshLayoutCommandFile.StatusLine("7", "Split split-1: done"), Is.EqualTo("7 Split split-1: done"));
            Assert.That(ServerMeshLayoutCommandFile.StatusLine("", "a\r\nb{c}"), Is.EqualTo("? a b(c)"));
        }

        [Test]
        public void RefusalLine_UsesTheCoordinatorsWording()
        {
            Assert.That(ServerMeshLayoutCommandFile.RefusalLine("7", "no central server in this Play"), Is.EqualTo("7 Layout op refused: no central server in this Play"));
            Assert.That(ServerMeshLayoutCommandFile.RefusalLine("7", null), Is.EqualTo("7 Layout op refused: unknown"));
        }

        [Test]
        public void SubmittedLine_IsNotMistakenForACoordinatorLine()
        {
            string line = ServerMeshLayoutCommandFile.SubmittedLine("7", "7 split C x 0 + C.1");

            Assert.That(line, Is.EqualTo("7 Layout command submitted: 7 split C x 0 + C.1"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(line.Substring(2)), Is.Null);
        }

        [Test]
        public void IsRefusal_OnlyForRefusedPayloads()
        {
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal("Layout op refused: tiling"), Is.True);
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal("Split split-1: aborted e1 ack timeout"), Is.False);
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal(null), Is.False);
        }
    }
}
