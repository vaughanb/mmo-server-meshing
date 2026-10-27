using System;

namespace OpenMMORPG.Setup
{
    /// <summary>
    /// What <c>Tools/Install/Pipelines/pipelines.json</c> says, written by <c>Tools~/pipeline_packs.py</c>.
    ///
    /// The kit in the project is one pipeline's version of the demo's content (the repository's own is URP). For each pipeline
    /// the manifest lists the files that make that version's content - each with a hash - and the files that must not exist
    /// beside it, and names the archive that installs it.
    /// </summary>
    [Serializable]
    public class PipelineFile
    {
        /// <summary>Relative to the kit folder.</summary>
        public string path;
        /// <summary>SHA-1 of the file, with CRLF read as LF when it is text (see <see cref="PipelineInstaller.Hash"/>).</summary>
        public string hash;
    }

    [Serializable]
    public class PipelineInfo
    {
        /// <summary>"urp" or "hdrp".</summary>
        public string id;
        public string name;
        /// <summary>The archive that installs this version, beside the manifest.</summary>
        public string package;
        /// <summary>The type name of the render pipeline asset a project on this pipeline uses.</summary>
        public string assetType;
        /// <summary>The Package Manager package that provides the pipeline.</summary>
        public string unityPackage;
        public string testedWith;
        public PipelineFile[] files;
        /// <summary>Files, relative to the kit folder, that belong to the other pipeline's version.</summary>
        public string[] remove;
        /// <summary>Packages this version does not need, which the other one did.</summary>
        public string[] packagesNoLongerNeeded;
    }

    [Serializable]
    public class PipelineManifest
    {
        public int schema;
        public string kitVersion;
        /// <summary>The repository commit the archives were built from ("+" if the tree had changes); for maintainers.</summary>
        public string builtFrom;
        /// <summary>The folder the archives import into: "Assets/OpenMMORPG".</summary>
        public string prefix;
        public PipelineInfo[] pipelines;

        public PipelineInfo Find(string id)
        {
            if (pipelines != null)
            {
                foreach (PipelineInfo p in pipelines)
                {
                    if (p.id == id)
                        return p;
                }
            }
            return null;
        }
    }
}
