// Server Meshing add-on — an operator's layout command as one line (Slice 2b decision 2; the rig writes it to
// Temp/ServerMesh.LayoutCommand.txt and its PlayRunner hands it to MeshCentralNetworkManager.SubmitLayoutCommand):
// `<seq> split <cell> <x|z> <at> <+|-> <childId>`, (Slice 2c) `<seq> merge <a> <b>`, (2d) `<seq> kill <cell>` or (0.85)
// `<seq> unfreeze <base>`. Pure: parsed and judged here, run by the coordinator.
using System;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutCommandKind is what a layout command asks for.</summary>
    public enum MeshLayoutCommandKind
    {
        Split,
        Merge,
        /// <summary>Kill (Slice 2d): have a cell's spawner kill its process (an operator's escape for a hung cell).</summary>
        Kill,
        /// <summary>Unfreeze (DESIGN 0.85): resolve a family frozen after a central restart, or a frozen operation of it (unfreeze_design.md).</summary>
        Unfreeze,
    }

    /// <summary>MeshLayoutCommand is one parsed layout command.</summary>
    public sealed class MeshLayoutCommand
    {
        public const string Usage = "<seq> split <cell> <x|z> <at> <+|-> <childId> | <seq> merge <a> <b> | <seq> kill <cell> | <seq> unfreeze <base>";

        public long seq;
        public MeshLayoutCommandKind kind;
        public string verb;
        /// <summary>mergeA and mergeB are a merge's two cells, in the order given (the coordinator finds the base).</summary>
        public string mergeA;
        public string mergeB;
        public string cellId;
        public MeshSplitAxis axis;
        /// <summary>The line's coordinate on the axis, world frame.</summary>
        public float at;
        public bool childOnPositiveSide;
        public string childId;

        /// <summary>TryParse reads a command line; false with the reason when it is not one.</summary>
        public static bool TryParse(string line, out MeshLayoutCommand command, out string error)
        {
            command = null;
            error = null;
            string[] words = (line ?? string.Empty).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                error = "an empty layout command";
                return false;
            }
            if (!long.TryParse(words[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
            {
                error = $"layout command sequence '{words[0]}' is not a number ({Usage})";
                return false;
            }
            if (words.Length >= 2 && string.Equals(words[1], "merge", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Length != 4)
                {
                    error = $"layout command {seq} has {words.Length} words, a merge takes 4 ({Usage})";
                    return false;
                }
                if (string.Equals(words[2], words[3], StringComparison.OrdinalIgnoreCase))
                {
                    error = $"layout command {seq}: a merge needs two different cells, not {words[2]} twice";
                    return false;
                }
                command = new MeshLayoutCommand { seq = seq, kind = MeshLayoutCommandKind.Merge, verb = "merge", mergeA = words[2], mergeB = words[3] };
                return true;
            }
            if (words.Length >= 2 && string.Equals(words[1], "kill", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Length != 3)
                {
                    error = $"layout command {seq} has {words.Length} words, a kill takes 3 ({Usage})";
                    return false;
                }
                command = new MeshLayoutCommand { seq = seq, kind = MeshLayoutCommandKind.Kill, verb = "kill", cellId = words[2] };
                return true;
            }
            if (words.Length >= 2 && string.Equals(words[1], "unfreeze", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Length != 3)
                {
                    error = $"layout command {seq} has {words.Length} words, an unfreeze takes 3 ({Usage})";
                    return false;
                }
                command = new MeshLayoutCommand { seq = seq, kind = MeshLayoutCommandKind.Unfreeze, verb = "unfreeze", cellId = words[2] };
                return true;
            }
            if (words.Length < 2 || !string.Equals(words[1], "split", StringComparison.OrdinalIgnoreCase))
            {
                error = words.Length < 2 ? $"layout command {seq} has no verb ({Usage})" : $"layout command {seq}: unknown verb '{words[1]}' (split, merge, kill or unfreeze)";
                return false;
            }
            if (words.Length != 7)
            {
                error = $"layout command {seq} has {words.Length} words, a split takes 7 ({Usage})";
                return false;
            }
            MeshSplitAxis axis;
            if (string.Equals(words[3], "x", StringComparison.OrdinalIgnoreCase))
                axis = MeshSplitAxis.X;
            else if (string.Equals(words[3], "z", StringComparison.OrdinalIgnoreCase))
                axis = MeshSplitAxis.Z;
            else
            {
                error = $"layout command {seq}: axis '{words[3]}' is not x or z";
                return false;
            }
            if (!float.TryParse(words[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float at) || float.IsNaN(at) || float.IsInfinity(at))
            {
                error = $"layout command {seq}: '{words[4]}' is not a coordinate";
                return false;
            }
            string side = words[5];
            // "+" or "-", optionally followed by the axis letter ("+x" reads as the child on the side above x = at).
            if (side.Length == 2 && string.Equals(side.Substring(1), words[3], StringComparison.OrdinalIgnoreCase))
                side = side.Substring(0, 1);
            if (side != "+" && side != "-")
            {
                error = $"layout command {seq}: side '{words[5]}' is not + or -";
                return false;
            }
            command = new MeshLayoutCommand
            {
                seq = seq, kind = MeshLayoutCommandKind.Split, verb = "split", cellId = words[2], axis = axis, at = at, childOnPositiveSide = side == "+", childId = words[6],
            };
            return true;
        }
    }
}
