// Server Meshing add-on — the layout command files the play runner serves (Slice 2b, slices/slice2b_design.md
// decisions 2 and 17; Slice 2c, slices/slice2c_design.md decision 15; Slice 2d, slices/slice2d_design.md decision 15).
// Temp/ServerMesh.LayoutCommand.txt holds one line, `<seq> split <baseCell> <x|z> <at> <+|-> <childId>`, `<seq> merge
// <a> <b>` or `<seq> kill <cell>`; the runner hands it to the central whatever its verb (the central dispatches on it)
// and mirrors the coordinator's `Split `, `Merge `, `Layout op ` and `Supervisor:` lines into
// Temp/ServerMesh.LayoutCommandStatus.txt, one per line, prefixed with the latest command's seq (a late `Split ` line
// lands under a merge's seq, and the supervisor's lines under whichever command came last: graders filter by kind).
// Pure text: no editor state.

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>ServerMeshLayoutCommandFile parses the command file and shapes the status lines.</summary>
    public static class ServerMeshLayoutCommandFile
    {
        /// <summary>CommandFileName is the command file's name under Temp/.</summary>
        public const string CommandFileName = "ServerMesh.LayoutCommand.txt";
        /// <summary>StatusFileName is the status file's name under Temp/.</summary>
        public const string StatusFileName = "ServerMesh.LayoutCommandStatus.txt";
        /// <summary>NoSeq stands for the seq of a command whose seq could not be read.</summary>
        public const string NoSeq = "?";

        private static readonly string[] s_mirroredPrefixes = { "Split ", "Merge ", "Layout op ", "Supervisor:" };

        /// <summary>
        /// TryParse reads the command file's text: the first line that is neither blank nor a # comment, trimmed. Its first
        /// word is the seq and at least one word must follow (the verb, split or merge, is the central's to judge); line is
        /// that whole line, which the central receives. False
        /// with an error (one line) otherwise; seq is then the first word when there is one, else <see cref="NoSeq"/>.
        /// </summary>
        public static bool TryParse(string text, out string seq, out string line, out string error)
        {
            seq = NoSeq;
            line = null;
            error = null;
            string first = null;
            foreach (string raw in (text ?? string.Empty).Split('\n'))
            {
                string candidate = raw.Trim();
                if (candidate.Length == 0 || candidate.StartsWith("#"))
                    continue;
                first = candidate;
                break;
            }
            if (first == null)
            {
                error = "empty command file";
                return false;
            }
            string[] words = first.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            seq = words[0];
            if (words.Length < 2)
            {
                error = "no command after seq " + OneLine(seq);
                return false;
            }
            line = string.Join(" ", words);
            return true;
        }

        /// <summary>IsBlank is true for text TryParse finds no line in (a file still being written, or emptied).</summary>
        public static bool IsBlank(string text)
        {
            return !TryParse(text, out _, out _, out string error) && error == "empty command file";
        }

        /// <summary>
        /// MirroredPayload is the part of a console message the status file mirrors, or null: the message's first line with
        /// the kit logger's prefix (` INFO Mesh [date time] - `) or a `[tag] ` prefix removed, when it then starts with
        /// `Split `, `Merge `, `Layout op ` or `Supervisor:`.
        /// </summary>
        public static string MirroredPayload(string message)
        {
            if (string.IsNullOrEmpty(message))
                return null;
            int newline = message.IndexOfAny(new[] { '\r', '\n' });
            string first = (newline >= 0 ? message.Substring(0, newline) : message).Trim();
            if (IsMirrored(first))
                return first;
            int kit = first.IndexOf("] - ", System.StringComparison.Ordinal);
            if (kit >= 0)
            {
                string rest = first.Substring(kit + 4).Trim();
                if (IsMirrored(rest))
                    return rest;
            }
            if (first.StartsWith("["))
            {
                int close = first.IndexOf("] ", System.StringComparison.Ordinal);
                if (close > 0)
                {
                    string rest = first.Substring(close + 2).Trim();
                    if (IsMirrored(rest))
                        return rest;
                }
            }
            return null;
        }

        private static bool IsMirrored(string text)
        {
            for (int i = 0; i < s_mirroredPrefixes.Length; ++i)
                if (text.StartsWith(s_mirroredPrefixes[i], System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        /// <summary>StatusLine is a status file line: the seq, a space, the text on one line.</summary>
        public static string StatusLine(string seq, string text) => OneLine(string.IsNullOrEmpty(seq) ? NoSeq : seq) + " " + OneLine(text);

        /// <summary>RefusalLine is the status line of a command refused before the coordinator ruled on it.</summary>
        public static string RefusalLine(string seq, string error) => StatusLine(seq, "Layout op refused: " + (string.IsNullOrEmpty(error) ? "unknown" : error));

        /// <summary>SubmittedLine is the status line written when a command goes to the central.</summary>
        public static string SubmittedLine(string seq, string line) => StatusLine(seq, "Layout command submitted: " + line);

        /// <summary>IsRefusal is true for a mirrored payload that refuses the command.</summary>
        public static bool IsRefusal(string payload) => payload != null && payload.StartsWith("Layout op refused", System.StringComparison.Ordinal);

        /// <summary>OneLine keeps text on one physical line and free of braces.</summary>
        public static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            return text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('{', '(').Replace('}', ')');
        }
    }
}
