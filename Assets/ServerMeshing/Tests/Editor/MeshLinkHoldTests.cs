using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// The mesh link hold (MeshLinkHold, DESIGN 0.79): a map server reads its mesh link only once its server scene is
    /// ready, on every map. Slice 2d held it on Dynamic maps only; a Static map's cell met the same early puppets when a
    /// neighbour still held its address (the September two-machine runs), so the hold no longer reads the map's mode.
    /// </summary>
    public class MeshLinkHoldTests
    {
        [Test]
        public void Server_HeldFromStartUntilTheSceneIsReady()
        {
            MeshLinkHold hold = new MeshLinkHold();
            hold.Start(10f);

            Assert.That(hold.Tick(true, 10f, out bool warn), Is.True, "the first frame after the start");
            Assert.That(warn, Is.False);
            Assert.That(hold.Tick(true, 10.2f, out _), Is.True);
            Assert.That(hold.HeldFrames, Is.EqualTo(2));

            Assert.That(hold.SceneReady(), Is.True, "the release: once");
            Assert.That(hold.IsReady, Is.True);
            Assert.That(hold.Tick(true, 10.3f, out _), Is.False, "read from now");
            Assert.That(hold.HeldFrames, Is.EqualTo(2), "a frame read is not counted");
        }

        [Test]
        public void SceneReady_ReleasesOncePerStart()
        {
            MeshLinkHold hold = new MeshLinkHold();
            hold.Start(0f);
            Assert.That(hold.SceneReady(), Is.True);
            Assert.That(hold.SceneReady(), Is.False, "a second scene-loaded call logs no second release");

            hold.Start(50f);
            Assert.That(hold.IsReady, Is.False, "a new server start holds again");
            Assert.That(hold.HeldFrames, Is.EqualTo(0));
            Assert.That(hold.StartedAt, Is.EqualTo(50f));
            Assert.That(hold.Tick(true, 50f, out _), Is.True);
            Assert.That(hold.SceneReady(), Is.True);
        }

        [Test]
        public void Client_NeverHeld()
        {
            MeshLinkHold hold = new MeshLinkHold();
            Assert.That(hold.Tick(false, 0f, out _), Is.False, "a manager that never started a server");
            hold.Start(0f);
            Assert.That(hold.Tick(false, 1f, out bool warn), Is.False);
            Assert.That(warn, Is.False);
            Assert.That(hold.HeldFrames, Is.EqualTo(0));
        }

        [Test]
        public void LongHold_WarnsOnceAndStaysHeld()
        {
            MeshLinkHold hold = new MeshLinkHold();
            hold.Start(100f);
            Assert.That(hold.Tick(true, 100f + MeshLinkHold.WarnAfterSeconds - 0.01f, out bool early), Is.True);
            Assert.That(early, Is.False);
            Assert.That(hold.Tick(true, 100f + MeshLinkHold.WarnAfterSeconds, out bool warn), Is.True, "still held: the scene is not ready");
            Assert.That(warn, Is.True);
            Assert.That(hold.Tick(true, 200f, out bool again), Is.True);
            Assert.That(again, Is.False, "one warning per start");

            hold.Start(300f);
            Assert.That(hold.Tick(true, 300f + MeshLinkHold.WarnAfterSeconds, out bool next), Is.True);
            Assert.That(next, Is.True, "a new start warns again");
        }

        [Test]
        public void ManagerHold_DoesNotReadTheMapMode()
        {
            // The Static-map extension is one condition gone; this pins it: the hook asks the hold alone, and the hold has
            // no map input. (Slice 2d's other readiness parts, the back-off and the registration reports, stay Dynamic.)
            string text = File.ReadAllText(Path.Combine(Application.dataPath, "ServerMeshing", "Mesh", "MeshMapNetworkManager_Readiness.cs"));
            Match hook = Regex.Match(text, @"private bool HoldLinksUntilSceneReady\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);

            Assert.That(hook.Success, Is.True, "the GhostingUpdate hook is where the hold is asked");
            Assert.That(hook.Groups["body"].Value, Does.Contain("_linkHold.Tick(IsServer,"));
            Assert.That(hook.Groups["body"].Value, Does.Not.Contain("IsDynamicMap"), "Static maps hold too since 0.79");
            Assert.That(typeof(MeshLinkHold).GetMethod(nameof(MeshLinkHold.Tick)).GetParameters().Length, Is.EqualTo(3), "isServer, now, warn: nothing about the map");
        }
    }
}
