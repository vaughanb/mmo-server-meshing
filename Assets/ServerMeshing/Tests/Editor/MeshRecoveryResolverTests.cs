using System.Collections.Generic;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshRecoveryResolverTests
    {
        private static MeshRecoveryInput Input(bool dynamicMap = true, bool trusted = true, int stored = 0, bool helloHeld = false, float elapsed = 0f, string[] registered = null, string[] hello = null)
        {
            MeshRecoveryInput input = new MeshRecoveryInput { mapId = "Map", mapDynamic = dynamicMap, storeTrusted = trusted, storedEntries = stored, helloHeldEntries = helloHeld, elapsedSeconds = elapsed, ceilingSeconds = 60f };
            input.registeredCells.AddRange(registered ?? new string[0]);
            foreach (string id in hello ?? new string[0])
                input.helloCells.Add(id);
            return input;
        }

        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData(Input(stored: 2), MeshRecoveryOutcome.Frozen, "Slice 5 recovery").SetName("Resolve_StoredDynamicEntries_Freeze");
            yield return new TestCaseData(Input(helloHeld: true), MeshRecoveryOutcome.Frozen, "cells hold one").SetName("Resolve_ACellHoldingALayout_Freezes");
            yield return new TestCaseData(Input(elapsed: 60f, registered: new[] { "C", "E" }, hello: new[] { "C" }), MeshRecoveryOutcome.Frozen, "no hello from E").SetName("Resolve_MissingHelloAtTheCeiling_Freezes");
            yield return new TestCaseData(Input(elapsed: 5f, registered: new[] { "C", "E" }, hello: new[] { "C" }), MeshRecoveryOutcome.Waiting, "waiting for the hello of E").SetName("Resolve_MissingHelloBeforeTheCeiling_Waits");
            yield return new TestCaseData(Input(trusted: false, elapsed: 5f), MeshRecoveryOutcome.Waiting, "cannot be trusted").SetName("Resolve_UntrustedStore_Waits");
            yield return new TestCaseData(Input(trusted: false, elapsed: 60f), MeshRecoveryOutcome.AdoptStatic, "could not be trusted").SetName("Resolve_UntrustedStoreAtTheCeiling_AdoptsTheAssetCut");
            yield return new TestCaseData(Input(dynamicMap: false, stored: 3), MeshRecoveryOutcome.AdoptStatic, "3 stored dynamic entries ignored").SetName("Resolve_StaticMapWithStoredEntries_IgnoresThem");
            yield return new TestCaseData(Input(), MeshRecoveryOutcome.AdoptStatic, "no runtime layout anywhere").SetName("Resolve_NothingRegistered_AdoptsAtOnce");
            yield return new TestCaseData(Input(registered: new[] { "C" }, hello: new[] { "c" }), MeshRecoveryOutcome.AdoptStatic, "no runtime layout anywhere").SetName("Resolve_EveryCellSaidHello_Adopts");
        }

        [TestCaseSource(nameof(Cases))]
        public void Resolve(MeshRecoveryInput input, MeshRecoveryOutcome expected, string reasonContains)
        {
            MeshRecoveryDecision decision = MeshRecoveryResolver.Resolve(input);

            Assert.That(decision.outcome, Is.EqualTo(expected));
            Assert.That(decision.reason, Does.Contain(reasonContains));
        }
    }
}
