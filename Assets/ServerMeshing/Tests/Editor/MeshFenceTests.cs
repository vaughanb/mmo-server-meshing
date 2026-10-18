using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using NUnit.Framework;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// Slice 2d's map side (slices/slice2d_design.md decisions 5, 8, 9, 12 and 13): the self-fence rule with its witness and
    /// registered-before conditions, neighbours ignoring an older hello, the RegistrationState codec and the handoff-target
    /// exclusion, the fault predicates and the `only:` monster-area form.
    /// </summary>
    public class MeshFenceTests
    {
        private const float Fence = 10f;

        private static string Inc(DateTime utc) => utc.Ticks.ToString("x");

        // ---- MeshFenceRule (decision 9) ----

        [Test]
        public void Fence_NothingBeforeTheFirstRegistration()
        {
            Assert.That(MeshFenceRule.Decide(false, false, 0f, true, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay), "a slow start is the supervisor's");
            Assert.That(MeshFenceRule.Decide(false, false, 0f, true, 5, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay), "a fresh process refused because a zombie holds its key just retries");
        }

        [Test]
        public void Fence_UnregisteredForTheFenceTimeWithAWitness()
        {
            Assert.That(MeshFenceRule.Decide(true, false, 90f, true, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Unregistered));
            Assert.That(MeshFenceRule.Decide(true, false, 90.5f, true, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay), "9.5 s is not yet the fence time");
            Assert.That(MeshFenceRule.Decide(true, true, 0f, true, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay), "registered now");
            Assert.That(MeshFenceRule.Decide(true, false, 50f, false, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay), "no witness: the cluster may be down for everyone");
        }

        [Test]
        public void Fence_ClockRunsFromTheLastRegisteredTick()
        {
            // A process suspended for a minute: its last registered tick is a minute old, so the first witness fences it at once.
            Assert.That(MeshFenceRule.Decide(true, false, 40f, true, 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Unregistered));
        }

        [Test]
        public void Fence_ThreeRefusalsAfterARegistration()
        {
            Assert.That(MeshFenceRule.Decide(true, false, 99f, false, 2, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay));
            Assert.That(MeshFenceRule.Decide(true, false, 99f, false, 3, 100f, Fence), Is.EqualTo(MeshFenceDecision.Refused), "no witness needed: another process holds the key");
        }

        // ---- the witness (a central restart never fences anyone) ----

        [Test]
        public void Witness_ALoneCutOffCellIsWitnessedAndFences()
        {
            // We were last registered at 50; the neighbour never reported "not registered" and reported "registered" at 98.
            Assert.That(MeshFenceRule.IsWitness(true, 98f, -1f, 50f, 100f), Is.True);
            Assert.That(MeshFenceRule.Decide(true, false, 50f, MeshFenceRule.IsWitness(true, 98f, -1f, 50f, 100f), 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Unregistered));
        }

        [Test]
        public void Witness_ACentralOutageLeavesNoWitnessEvenOnceTheCentralIsBack()
        {
            // The central died: we were last registered at 50, the neighbour reported "not registered" at 51 and, the
            // central back, "registered" again at 98. It was not registered continuously since our last tick: no witness.
            Assert.That(MeshFenceRule.IsWitness(true, 98f, 51f, 50f, 100f), Is.False);
            Assert.That(MeshFenceRule.Decide(true, false, 50f, MeshFenceRule.IsWitness(true, 98f, 51f, 50f, 100f), 0, 100f, Fence), Is.EqualTo(MeshFenceDecision.Stay));
            // The neighbour noticed the dead central a moment before we did (cells time out up to a disconnect timeout apart).
            Assert.That(MeshFenceRule.IsWitness(true, 98f, 48f, 50f, 100f), Is.False, "the same outage");
            Assert.That(MeshFenceRule.IsWitness(false, 98f, 99f, 50f, 100f), Is.False, "not registered now");
        }

        [Test]
        public void Witness_ANeighbourBrieflyUnregisteredWellBeforeOurLastTickStillWitnesses()
        {
            Assert.That(MeshFenceRule.IsWitness(true, 98f, 20f, 50f, 100f), Is.True, "its blip ended before we were last registered");
            Assert.That(MeshFenceRule.IsWitness(true, 98f, 50f - MeshFenceRule.OutageSkewSeconds - 0.1f, 50f, 100f), Is.True);
        }

        [Test]
        public void Witness_TheRegisteredReportMustBeRecent()
        {
            Assert.That(MeshFenceRule.IsWitness(true, 95f, -1f, 50f, 100f), Is.True, "5 s old still counts");
            Assert.That(MeshFenceRule.IsWitness(true, 94.9f, -1f, 50f, 100f), Is.False);
            Assert.That(MeshFenceRule.IsWitness(true, -1f, -1f, 50f, 100f), Is.False, "never reported registered");
        }

        [Test]
        public void Fence_OnlyAFencedReasonFencesOnRetire()
        {
            Assert.That(MeshFenceRule.IsFencedRetire("fenced: stale incarnation"), Is.True);
            Assert.That(MeshFenceRule.IsFencedRetire("fenced: aborted"), Is.True);
            Assert.That(MeshFenceRule.IsFencedRetire("merged into C"), Is.False);
            Assert.That(MeshFenceRule.IsFencedRetire("Fenced: x"), Is.False, "the prefix is exact");
            Assert.That(MeshFenceRule.IsFencedRetire(null), Is.False);
        }

        [Test]
        public void Fence_ReasonsAreOneInvariantLineWithoutBraces()
        {
            string unregistered = MeshFenceRule.Reason(MeshFenceDecision.Unregistered, 10.4f, "C", 0);
            Assert.That(unregistered, Is.EqualTo("unregistered 10 s while zone C reports itself registered"));
            Assert.That(MeshFenceRule.Reason(MeshFenceDecision.Refused, 0f, null, 3), Is.EqualTo("3 consecutive registration refusals after a registration"));
            foreach (string reason in new[] { unregistered, MeshFenceRule.Reason(MeshFenceDecision.Refused, 0f, null, 3) })
                Assert.That(reason.IndexOfAny(new[] { '{', '}', '\n' }), Is.EqualTo(-1), reason);
        }

        // ---- incarnations only move forward (decision 5, map part) ----

        [Test]
        public void Hello_AnOlderIncarnationOfADynamicZoneIsIgnored()
        {
            string old = Inc(new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc));
            string live = Inc(new DateTime(2026, 10, 17, 10, 5, 0, DateTimeKind.Utc));
            Assert.That(MeshFenceRule.IsOlderIncarnation(old, live), Is.True);
            Assert.That(MeshFenceRule.IgnoresHello(true, old, live), Is.True, "a zombie's hello");
            Assert.That(MeshFenceRule.IgnoresHello(true, live, old), Is.False, "a restart: newer releases the old one's records as before");
            Assert.That(MeshFenceRule.IgnoresHello(true, live, live), Is.False, "a reconnect of the same process");
            Assert.That(MeshFenceRule.IgnoresHello(false, old, live), Is.False, "a Static map's zone: unchanged");
            Assert.That(MeshFenceRule.IgnoresHello(true, old, null), Is.False, "nothing seen yet");
            Assert.That(MeshFenceRule.IgnoresHello(true, "not-hex", live), Is.False, "unreadable: not older");
        }

        [Test]
        public void Hello_IncarnationsCompareAsTicksNotAsText()
        {
            // "ff" is shorter text than "100" but an older start; hex ticks of different lengths still order by value.
            Assert.That(MeshFenceRule.IsOlderIncarnation("ff", "100"), Is.True);
            Assert.That(MeshFenceRule.IsOlderIncarnation("100", "ff"), Is.False);
            Assert.That(MeshFenceRule.TryTicks("8de0c1a2b3c4d5e", out long ticks) && ticks > 0, Is.True);
        }

        // ---- RegistrationState (decision 8) ----

        [Test]
        public void RegistrationState_IsLinkMessage130AndRoundTrips()
        {
            Assert.That(MeshLinkMessageTypes.RegistrationState, Is.EqualTo((ushort)130));
            HashSet<ushort> ids = new HashSet<ushort>();
            foreach (FieldInfo field in typeof(MeshLinkMessageTypes).GetFields(BindingFlags.Public | BindingFlags.Static))
                Assert.That(ids.Add((ushort)field.GetValue(null)), Is.True, field.Name + " shares its id");
            foreach (bool registered in new[] { true, false })
            {
                NetDataWriter writer = new NetDataWriter();
                new RegistrationStateMessage { registered = registered }.Serialize(writer);
                Assert.That(writer.Length, Is.EqualTo(1));
                RegistrationStateMessage read = default;
                NetDataReader reader = new NetDataReader(writer.CopyData());
                read.Deserialize(reader);
                Assert.That(read.registered, Is.EqualTo(registered));
                Assert.That(reader.AvailableBytes, Is.EqualTo(0));
            }
        }

        [Test]
        public void RegistrationState_AnUnregisteredNeighbourIsNoHandoffTarget()
        {
            Assert.That(MeshFenceRule.BlocksHandoffTo(true, true, false), Is.True);
            Assert.That(MeshFenceRule.BlocksHandoffTo(true, true, true), Is.False);
            Assert.That(MeshFenceRule.BlocksHandoffTo(false, false, false), Is.False, "a Static map's cell never reports and stays a target");
            Assert.That(MeshFenceRule.BlocksHandoffTo(false, true, false), Is.False, "a Static map's cell is never read");
        }

        [Test]
        public void RegistrationState_ADynamicNeighbourWithNoLiveReportIsNotReadyYet()
        {
            // A restarted child: linked within milliseconds of listening, but the central has not answered its hello, it
            // has no link of its own yet, so no report; and its old incarnation's report went with that incarnation's link.
            Assert.That(MeshFenceRule.BlocksHandoffTo(true, false, false), Is.True);
            Assert.That(MeshFenceRule.BlocksHandoffTo(true, false, true), Is.True, "a report that is no longer live is no report");
        }

        // ---- fault predicates (decision 12) ----

        private static MeshLayoutSnapshot Naming(string id, MeshCellState state)
        {
            MeshLayoutSnapshot snapshot = new MeshLayoutSnapshot { pair = new MeshLayoutPair(1, 5) };
            snapshot.entries.Add(new MeshLayoutEntry { id = "C", mapId = "M", baseId = "C", state = MeshCellState.Active });
            snapshot.entries.Add(new MeshLayoutEntry { id = id, mapId = "M", baseId = "C", state = state });
            return snapshot;
        }

        [Test]
        public void Fault_PrewarmFiresOnlyOnABroadcastNamingTheCellPending()
        {
            MeshLayoutSnapshot e1 = Naming("C.1", MeshCellState.Pending);
            Assert.That(MeshFault.PrewarmFires("C.1", "C.1", MeshLayoutKind.Broadcast, e1), Is.True);
            Assert.That(MeshFault.PrewarmFires("C.1", "C.1", MeshLayoutKind.Staged, e1), Is.False, "the staged e1 comes before the broadcast");
            Assert.That(MeshFault.PrewarmFires("C.1", "C.1", MeshLayoutKind.HelloAnswer, e1), Is.False, "the hello answer too");
            Assert.That(MeshFault.PrewarmFires("C.1", "C.1", MeshLayoutKind.Broadcast, Naming("C.1", MeshCellState.Active)), Is.False, "e2 names it Active");
            Assert.That(MeshFault.PrewarmFires("C.1", "C", MeshLayoutKind.Broadcast, e1), Is.False, "another cell");
            Assert.That(MeshFault.PrewarmFires(null, "C.1", MeshLayoutKind.Broadcast, e1), Is.False, "unset");
            Assert.That(MeshFault.PrewarmFires("C.1", "C.1", MeshLayoutKind.Broadcast, null), Is.False);
        }

        [Test]
        public void Fault_DrainFiresOnAChildThatOwnsSomething()
        {
            Assert.That(MeshFault.DrainFires("C.1", "C.1", true, true), Is.True);
            Assert.That(MeshFault.DrainFires("C.1", "C.1", true, false), Is.False, "Pending: nothing owned yet");
            Assert.That(MeshFault.DrainFires("C", "C", false, true), Is.False, "a base cell");
            Assert.That(MeshFault.DrainFires("C.1", "C.2", true, true), Is.False);
            Assert.That(MeshFault.DrainFires(null, "C.1", true, true), Is.False);
        }

        [Test]
        public void Fault_TheNoticeLineAndMarkerKeepTheirScheme()
        {
            Assert.That(MeshFault.NamesCell("E", "E"), Is.True);
            Assert.That(MeshFault.NamesCell(" E ", "E"), Is.True);
            Assert.That(MeshFault.NamesCell("E", "e"), Is.False, "ordinal, as the notice fault always compared");
            Assert.That(MeshFault.NamesCell("", "E"), Is.False);
            Assert.That(MeshFault.FaultLine(MeshFault.DieOnNotice, "handoff notice for abc"),
                Is.EqualTo("FAULT: dying on handoff notice for abc (MESH_FAULT_DIE_ON_NOTICE)"), "the crash scenario greps this");
            Assert.That(MeshFault.MarkerPath(MeshFault.DieOnDrain, "C.1").Replace('\\', '/'), Is.EqualTo("Temp/ServerMesh.FaultFired.MESH_FAULT_DIE_ON_DRAIN.C.1"));
            Assert.That(MeshFault.DieOnPrewarm, Is.EqualTo("MESH_FAULT_DIE_ON_PREWARM"));
        }

        // ---- MESH_MONSTER_AREAS=only: (decision 13) ----

        [Test]
        public void OnlyAreas_ThePrefixIsStrippedAndEveryOtherAreaIsOff()
        {
            string value = "only:Spawn_Interior_C,Spawn_B";
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref value), Is.True);
            Assert.That(value, Is.EqualTo("Spawn_Interior_C,Spawn_B"));
            HashSet<string> names = MeshMonsterAreasToggle.Names(value);
            Assert.That(names, Is.EquivalentTo(new[] { "Spawn_Interior_C", "Spawn_B" }));
            Assert.That(MeshMonsterAreasToggle.SwitchesOff(true, names, "Spawn_Interior_C"), Is.False);
            Assert.That(MeshMonsterAreasToggle.SwitchesOff(true, names, "Spawn_Boundary_X100_Z60"), Is.True, "E's area on C.1's edge");
            Assert.That(MeshMonsterAreasToggle.SwitchesOff(false, names, "Spawn_Boundary_X100_Z60"), Is.False, "the plain list leaves active areas as they are");
        }

        [Test]
        public void OnlyAreas_OtherFormsAreUnchanged()
        {
            string plain = "Spawn_Interior_C";
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref plain), Is.False);
            Assert.That(plain, Is.EqualTo("Spawn_Interior_C"));
            string all = "all";
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref all), Is.False);
            string none = null;
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref none), Is.False);
            string upper = "ONLY: A , ,B";
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref upper), Is.True);
            Assert.That(MeshMonsterAreasToggle.Names(upper), Is.EquivalentTo(new[] { "A", "B" }));
            string empty = "only:";
            Assert.That(MeshMonsterAreasToggle.StripOnly(ref empty), Is.True);
            Assert.That(MeshMonsterAreasToggle.Names(empty), Is.Null);
            Assert.That(MeshMonsterAreasToggle.SwitchesOff(true, null, "A"), Is.True, "only: with no names switches every area off");
        }
    }
}
