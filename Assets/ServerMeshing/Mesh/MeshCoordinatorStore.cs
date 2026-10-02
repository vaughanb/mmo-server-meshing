// Server Meshing add-on — where the central's coordinator keeps its layout between restarts (DESIGN.md 0.72, plan D3):
// one JSON file on the central's host, written whole through a temporary file so a crash mid-write never leaves half a
// layout. Every IO failure is reported, never thrown: a coordinator that cannot write refuses operations, it does not die.
using System;
using System.IO;
using System.Security;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshCoordinatorStoreData is what the store file holds.</summary>
    [Serializable]
    public sealed class MeshCoordinatorStoreData
    {
        public const int CurrentFormat = 1;

        public int format = CurrentFormat;
        /// <summary>The last coordinator incarnation that started, so the next one is always newer.</summary>
        public long lastIncarnation;
        /// <summary>The layout every cell was told to hold.</summary>
        public MeshLayoutSnapshot broadcast = new MeshLayoutSnapshot();
        /// <summary>Whether a step was sent to its first cell but not yet to everyone (JsonUtility cannot write a null object).</summary>
        public bool hasStaged;
        public MeshLayoutSnapshot staged = new MeshLayoutSnapshot();
        public string stagedStepId = "";
        public string stagedFirstApplier = "";
        public string writtenAtUtc = "";
    }

    /// <summary>MeshCoordinatorStore reads and writes the coordinator's store file.</summary>
    public sealed class MeshCoordinatorStore
    {
        /// <summary>Rig override for the store's path.</summary>
        public const string ENV_PATH = "MESH_COORDINATOR_STORE";
        /// <summary>The default: beside the process, git-ignored, and not in Config (which holds tracked files).</summary>
        public const string DefaultRelativePath = "Temp/ServerMesh.Coordinator.json";

        public string Path { get; }
        /// <summary>False from a failed write until a write succeeds.</summary>
        public bool Writable { get; private set; } = true;
        public string LastError { get; private set; } = "";

        public MeshCoordinatorStore(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("the coordinator store needs a path", nameof(path));
            Path = System.IO.Path.GetFullPath(path);
        }

        /// <summary>ResolvePath picks the store's path: MESH_COORDINATOR_STORE, else the registry's setting, else the default, made absolute.</summary>
        public static string ResolvePath(string configured)
        {
            string env = Environment.GetEnvironmentVariable(ENV_PATH);
            string path = !string.IsNullOrEmpty(env) ? env : !string.IsNullOrEmpty(configured) ? configured : DefaultRelativePath;
            return System.IO.Path.GetFullPath(path);
        }

        /// <summary>TryLoad reads the file: true with null data when there is none, false (with the reason) when it cannot be trusted.</summary>
        public bool TryLoad(out MeshCoordinatorStoreData data, out string error)
        {
            data = null;
            error = null;
            string text;
            try
            {
                if (!File.Exists(Path))
                    return true;
                text = File.ReadAllText(Path);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                error = $"coordinator store {Path}: read failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
            try
            {
                data = JsonUtility.FromJson<MeshCoordinatorStoreData>(text);
            }
            catch (Exception ex)
            {
                data = null;
                error = $"coordinator store {Path}: not readable JSON: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
            if (data == null)
            {
                error = $"coordinator store {Path}: empty or not an object";
                return false;
            }
            if (data.format != MeshCoordinatorStoreData.CurrentFormat)
            {
                error = $"coordinator store {Path}: format {data.format}, expected {MeshCoordinatorStoreData.CurrentFormat}";
                data = null;
                return false;
            }
            if (data.broadcast == null || data.broadcast.entries == null || (data.hasStaged && (data.staged == null || data.staged.entries == null)))
            {
                error = $"coordinator store {Path}: a snapshot is missing";
                data = null;
                return false;
            }
            return true;
        }

        /// <summary>TrySave writes the whole file through a temporary one; on failure the store is not Writable until a save succeeds.</summary>
        public bool TrySave(MeshCoordinatorStoreData data, out string error)
        {
            error = null;
            string tmp = Path + ".tmp";
            try
            {
                data.format = MeshCoordinatorStoreData.CurrentFormat;
                data.writtenAtUtc = DateTime.UtcNow.ToString("o");
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.Write(JsonUtility.ToJson(data, true));
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(Path))
                    File.Replace(tmp, Path, null);
                else
                    File.Move(tmp, Path);
                Writable = true;
                LastError = "";
                return true;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                error = $"coordinator store {Path}: write failed: {ex.GetType().Name}: {ex.Message}";
                Writable = false;
                LastError = error;
                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch (Exception cleanup) when (IsIoFailure(cleanup))
                {
                    error += $" (and the temporary file {tmp} could not be removed: {cleanup.Message})";
                }
                return false;
            }
        }

        /// <summary>TryMoveAside renames an untrusted file out of the way (kept for an operator), so the next save starts clean.</summary>
        public bool TryMoveAside(string suffix, out string movedTo, out string error)
        {
            movedTo = Path + suffix;
            error = null;
            try
            {
                if (File.Exists(Path))
                    File.Move(Path, movedTo);
                return true;
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                error = $"coordinator store {Path}: could not move it to {movedTo}: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool IsIoFailure(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is NotSupportedException || ex is ArgumentException;
        }
    }
}
