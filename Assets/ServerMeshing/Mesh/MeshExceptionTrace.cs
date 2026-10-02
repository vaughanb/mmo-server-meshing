// Server Meshing add-on — every exception a map server logs, with its managed stack, on one line of the server's own
// log. The kit's file log keeps only the exception's message, and an exception that happens once in a long load run
// cannot be reproduced at will, so without the stack it cannot be traced at all.
using System.Text;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshExceptionTrace writes the stack of every exception Unity logs on the main thread into the kit's log.</summary>
    public static class MeshExceptionTrace
    {
        /// <summary>The longest line written: a runaway stack would otherwise swamp the log.</summary>
        public const int MaxLength = 4000;
        private static bool s_installed;

        /// <summary>Install hooks Unity's log; calling it again does nothing.</summary>
        public static void Install()
        {
            if (s_installed)
                return;
            s_installed = true;
            Application.logMessageReceived += OnLog;
        }

        /// <summary>Uninstall removes the hook (an editor that hosts a map server keeps its statics between Play sessions).</summary>
        public static void Uninstall()
        {
            if (!s_installed)
                return;
            s_installed = false;
            Application.logMessageReceived -= OnLog;
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            // Errors are left alone: this line is itself an error, so reacting to errors would echo it for ever.
            if (type != LogType.Exception || string.IsNullOrEmpty(stackTrace))
                return;
            Logging.LogError("Mesh", Format(condition, stackTrace));
        }

        /// <summary>
        /// Format puts an exception and its stack on one line, "Exception stack: &lt;condition&gt; at &lt;frame&gt; &lt;- &lt;caller&gt; ...":
        /// the rig's tools read the log line by line and keep only lines that carry a stamp.
        /// </summary>
        public static string Format(string condition, string stackTrace)
        {
            StringBuilder line = new StringBuilder("Exception stack: ");
            line.Append((condition ?? string.Empty).Trim().Replace('\n', ' ').Replace('\r', ' '));
            string separator = " at ";
            foreach (string raw in (stackTrace ?? string.Empty).Split('\n'))
            {
                string frame = raw.Trim();
                if (frame.Length == 0)
                    continue;
                line.Append(separator).Append(frame);
                separator = " <- ";
                if (line.Length >= MaxLength)
                    break;
            }
            return line.Length > MaxLength ? line.ToString(0, MaxLength) + " ..." : line.ToString();
        }
    }
}
