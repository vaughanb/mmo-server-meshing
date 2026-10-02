using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MultiplayerARPG.MMO.Mesh.Sync;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshSyncContractTests
    {
        private static readonly string[] AllowedUsings = { "LiteNetLib", "LiteNetLib.Utils", "LiteNetLibManager" };

        [Test]
        public void ContractFile_UsesOnlyTheNetworkingNamespaces()
        {
            string text = File.ReadAllText(Path.Combine(Application.dataPath, "ServerMeshing", "Mesh", "Sync", "MeshSyncContracts.cs"));

            string[] usings = Regex.Matches(text, @"^\s*using\s+([\w.]+)\s*;", RegexOptions.Multiline).Cast<Match>().Select(m => m.Groups[1].Value).ToArray();

            Assert.That(usings, Is.SubsetOf(AllowedUsings), "the space team compiles this file alone; it may name nothing else of the add-on or the kit");
        }

        [Test]
        public void Withhold_IsWithheld_AtZeroHz()
        {
            Assert.That(MeshSendRate.Withhold.IsWithheld, Is.True);
            Assert.That(MeshSendRate.Withhold.Hz, Is.EqualTo(0));
        }

        [Test]
        public void TwoArgumentRate_IsFullAndNotWithheld()
        {
            MeshSendRate rate = new MeshSendRate(10, 3);

            Assert.That(rate.Hz, Is.EqualTo(10));
            Assert.That(rate.Priority, Is.EqualTo(3));
            Assert.That(rate.Variant, Is.EqualTo(MeshStateVariant.Full));
            Assert.That(rate.IsWithheld, Is.False);
        }

        [Test]
        public void ThreeArgumentRate_KeepsItsVariant()
        {
            MeshSendRate rate = new MeshSendRate(5, 1, MeshStateVariant.Compact);

            Assert.That(rate.Variant, Is.EqualTo(MeshStateVariant.Compact));
            Assert.That(rate.IsWithheld, Is.False);
        }

        [Test]
        public void ZeroHzRate_IsNotWithheld()
        {
            Assert.That(new MeshSendRate(0, 0).IsWithheld, Is.False, "0 Hz still receives urgent and first-send records");
        }

        [Test]
        public void ObserverContext_KeepsAnUnknownDistance()
        {
            MeshObserverContext observer = new MeshObserverContext(7, float.NaN, true);

            Assert.That(float.IsNaN(observer.Distance), Is.True);
            Assert.That(observer.ObserverConnectionId, Is.EqualTo(7));
            Assert.That(observer.IsOwner, Is.True);
        }
    }
}
