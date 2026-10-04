// Server Meshing add-on — rig CE-12 check (MESH_CE12_CHECK=1), pure part: names the shape of a kit handler's one-shot
// server flags from what a fixed write sequence observed, and formats the check's one log line.
using System;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshCe12Shape classifies how a kit movement handler clears its one-shot server flags (IsTeleport here), from
    /// the sequence Teleport, write at T, write at T, write at T+50, ConsumeServerStateFlags when the handler has it, write
    /// at T+100: absent (no successful first write, or the first write lacks the flag), perCall (1,0,0: the write clears),
    /// stamp (1,1,0: cleared once the timestamp changes), pass (1,1,1, consume available, 0 after it: the send loop's
    /// consume clears) and stuck (1,1,1 with no consume, or still set after the consume). Pure.
    /// </summary>
    public static class ServerMeshCe12Shape
    {
        /// <summary>Absent is the shape when the handler never wrote, or its first write after the teleport lacked the flag.</summary>
        public const string Absent = "absent";
        /// <summary>PerCall is the shape of a write that clears the flag itself.</summary>
        public const string PerCall = "perCall";
        /// <summary>Stamp is the shape of a flag held for one write timestamp.</summary>
        public const string Stamp = "stamp";
        /// <summary>Pass is the shape of a flag held until the send loop's ConsumeServerStateFlags.</summary>
        public const string Pass = "pass";
        /// <summary>Stuck is the shape of a flag nothing clears.</summary>
        public const string Stuck = "stuck";

        /// <summary>
        /// Classify names the shape of one observed sequence: <paramref name="firstWrote"/> is whether the first write
        /// returned true; <paramref name="first"/>, <paramref name="second"/>, <paramref name="next"/> and
        /// <paramref name="after"/> are whether the write at T, T, T+50 and (after the consume) T+100 carried the flag;
        /// <paramref name="consumeAvailable"/> is whether the handler implements the consume. A flag that comes back at
        /// T+50 after the second write lacked it counts as stuck.
        /// </summary>
        public static string Classify(bool firstWrote, bool first, bool second, bool next, bool consumeAvailable, bool after)
        {
            if (!firstWrote || !first)
                return Absent;
            if (!second)
                return next ? Stuck : PerCall;
            if (!next)
                return Stamp;
            if (!consumeAvailable || after)
                return Stuck;
            return Pass;
        }

        /// <summary>
        /// Line is the check's one log line: "CE-12 check: cell C shape=S first=b second=b next=b consume=yes|no after=b
        /// reliable=b/b/b/b handler=H", with bits as 0 or 1, the shape from Classify and H the handler's type name (none
        /// when there is no handler, or when it is empty).
        /// </summary>
        public static string Line(string cell, bool firstWrote, bool first, bool second, bool next, bool consumeAvailable, bool after,
            bool reliableFirst, bool reliableSecond, bool reliableNext, bool reliableAfter, string handler)
        {
            string shape = Classify(firstWrote, first, second, next, consumeAvailable, after);
            string shownCell = string.IsNullOrEmpty(cell) ? "none" : cell.Replace(' ', '_');
            string shownHandler = string.IsNullOrEmpty(handler) ? "none" : handler.Replace(' ', '_');
            return FormattableString.Invariant(
                $"CE-12 check: cell {shownCell} shape={shape} first={Bit(first)} second={Bit(second)} next={Bit(next)} consume={(consumeAvailable ? "yes" : "no")} after={Bit(after)} reliable={Bit(reliableFirst)}/{Bit(reliableSecond)}/{Bit(reliableNext)}/{Bit(reliableAfter)} handler={shownHandler}");
        }

        private static int Bit(bool value) => value ? 1 : 0;
    }
}
