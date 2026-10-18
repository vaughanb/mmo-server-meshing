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
        public void TryParse_MergeLine_GivesSeqAndTheWholeLine()
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse("8  merge C   C.1\n", out string seq, out string line, out string error);

            Assert.That(ok, Is.True);
            Assert.That(seq, Is.EqualTo("8"));
            Assert.That(line, Is.EqualTo("8 merge C C.1"));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void MirroredPayload_MergeLines_AreMirrored()
        {
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(" INFO Mesh [2026-10-17 12:00:01] - Merge merge-2: e1 broadcast 1a2b/5"), Is.EqualTo("Merge merge-2: e1 broadcast 1a2b/5"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(" WARN Mesh [2026-10-17 12:00:01] - Merge merge-2: drain straggling 92 s: characters=1"), Is.EqualTo("Merge merge-2: drain straggling 92 s: characters=1"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("[Mesh] Merge merge-2: channel C.1 removed"), Is.EqualTo("Merge merge-2: channel C.1 removed"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("Merge merge-2: done"), Is.EqualTo("Merge merge-2: done"));
        }

        [Test]
        public void MirroredPayload_SupervisorLines_AreMirrored()
        {
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(" INFO Mesh [2026-10-17 12:00:01] - Supervisor: C.1 down 21 s, restart 1 of 3 requested"), Is.EqualTo("Supervisor: C.1 down 21 s, restart 1 of 3 requested"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload(" WARN Mesh [2026-10-17 12:00:01] - Supervisor: C.1 restart 2 failed (no hello in 60 s)"), Is.EqualTo("Supervisor: C.1 restart 2 failed (no hello in 60 s)"));
            Assert.That(ServerMeshLayoutCommandFile.MirroredPayload("[Mesh] Supervisor: C.1 back, incarnation 8dcf1a2b3c4d5e6"), Is.EqualTo("Supervisor: C.1 back, incarnation 8dcf1a2b3c4d5e6"));
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal("Supervisor: C.1 cannot collapse (Slice 3); left down"), Is.False);
        }

        [Test]
        public void TryParse_KillLine_GivesSeqAndTheWholeLine()
        {
            bool ok = ServerMeshLayoutCommandFile.TryParse("11 kill  C.1\n", out string seq, out string line, out string error);

            Assert.That(ok, Is.True);
            Assert.That(seq, Is.EqualTo("11"));
            Assert.That(line, Is.EqualTo("11 kill C.1"));
            Assert.That(error, Is.Null);
        }

        [Test]
        public void IsRefusal_NotForMergeLines()
        {
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal("Merge merge-2: aborted e1 gate shut"), Is.False);
            Assert.That(ServerMeshLayoutCommandFile.IsRefusal(ServerMeshLayoutCommandFile.MirroredPayload("Layout op refused: merge of two children (Slice 3)")), Is.True);
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
        [TestCase("Merged two ranges")]
        [TestCase("[Mesh] Handoff START Bot1 (id) zone C.1->C Merge merge-2")]
        [TestCase(" INFO Mesh [2026-10-17 12:00:01] - Retired zone C.1: link closed, 0 record(s) released")]
        [TestCase("Supervisor C.1 down")]
        [TestCase(" INFO Mesh [2026-10-17 12:00:01] - Spawner: killed C.1 pid 4242 (restart 1)")]
        [TestCase("[Mesh] Fence: C.1 incarnation 8dcf fenced (registration lost): saving 2 player(s)")]
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
