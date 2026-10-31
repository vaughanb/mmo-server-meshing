using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;

namespace OpenMMORPG.KitUpdater.Tests
{
    /// <summary>
    /// A kit the pipeline installer switched to HDRP holds the HDRP archive's files in place of the kit's own. The scanner and
    /// the updater must see those as the kit's files, not as edits, and an update must write the target release's HDRP
    /// versions, never its URP ones.
    /// </summary>
    public class KitPipelineTests
    {
        private const string Root = "Assets/OpenMMORPG";
        private const string Mat = "Demo/Sea.mat";       // differs between the pipelines
        private const string Script = "Demo/Sky.cs";     // differs too
        private const string Asset = "Demo/DemoURP.asset"; // URP only: an HDRP install removes it
        private const string Extra = "Demo/HDRPSky.cs";  // HDRP only
        private const string Readme = "README.md";        // the same in both

        private class FakeProject : IKitProject
        {
            public readonly Dictionary<string, string> paths = new Dictionary<string, string>();
            public readonly Dictionary<string, string> hashes = new Dictionary<string, string>();
            public readonly Dictionary<string, string> metas = new Dictionary<string, string>();

            public string PathOf(string guid) { return paths.TryGetValue(guid, out string p) ? p : null; }
            public string HashOf(string projectPath) { return hashes.TryGetValue(projectPath, out string h) ? h : null; }
            public string MetaHashOf(string projectPath) { return metas.TryGetValue(projectPath, out string m) ? m : null; }
            public bool Exists(string projectPath) { return paths.ContainsValue(projectPath); }

            public void Put(string guid, string kitPath, string contents, string meta)
            {
                string path = Root + "/" + kitPath;
                paths[guid] = path;
                hashes[path] = H(contents);
                metas[path] = H(meta);
            }
        }

        private static string H(string text)
        {
            return KitHash.Of(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>A release: its kit manifest (the URP version) and its HDRP pipeline content.</summary>
        private class Release
        {
            public KitManifest manifest;
            public KitPipelineContent hdrp;
            public string pipelinesJson;
            public byte[] archive;
        }

        /// <param name="urp">guid to (path, contents) of the kit's own files.</param>
        /// <param name="hdrp">guid to (path, contents) of the HDRP archive's files.</param>
        private static Release MakeRelease(string version, Dictionary<string, (string path, string contents)> urp,
            Dictionary<string, (string path, string contents)> hdrp, params string[] removedForHdrp)
        {
            var manifest = new KitManifest
            {
                format = 1, version = version, tag = "v" + version,
                dependencies = new Dictionary<string, string>(), files = new Dictionary<string, KitManifestEntry>(),
            };
            manifest.files["dD"] = new KitManifestEntry { path = "Demo", folder = true, meta = H("folder") };
            foreach (var kv in urp)
                manifest.files[kv.Key] = new KitManifestEntry { path = kv.Value.path, hash = H(kv.Value.contents), meta = H("meta-" + kv.Key), size = kv.Value.contents.Length };

            var files = new List<KeyValuePair<string, byte[]>>();
            foreach (var kv in hdrp)
            {
                files.Add(new KeyValuePair<string, byte[]>(kv.Key + "/pathname", Encoding.UTF8.GetBytes(Root + "/" + kv.Value.path + "\n00")));
                files.Add(new KeyValuePair<string, byte[]>(kv.Key + "/asset", Encoding.UTF8.GetBytes(kv.Value.contents)));
                files.Add(new KeyValuePair<string, byte[]>(kv.Key + "/asset.meta", Encoding.UTF8.GetBytes("meta-" + kv.Key)));
            }
            byte[] archive = Gzip(Tar(files));
            string json = "{\"schema\":1,\"prefix\":\"" + Root + "\",\"pipelines\":[" +
                          "{\"id\":\"urp\",\"package\":\"OpenMMORPG_URP.unitypackage\",\"files\":[],\"remove\":[\"" + Extra + "\"]}," +
                          "{\"id\":\"hdrp\",\"package\":\"OpenMMORPG_HDRP.unitypackage\",\"name\":\"HDRP\",\"files\":[],\"remove\":[" +
                          string.Join(",", removedForHdrp.Select(r => "\"" + r + "\"")) + "]}]}";
            return new Release { manifest = manifest, pipelinesJson = json, archive = archive, hdrp = KitPipelineContent.Read(json, archive, "hdrp") };
        }

        private Release _installed;
        private FakeProject _project;

        [SetUp]
        public void SetUp()
        {
            _installed = MakeRelease("1.1.5",
                new Dictionary<string, (string, string)> { ["m"] = (Mat, "urp sea"), ["s"] = (Script, "urp sky"), ["u"] = (Asset, "urp asset"), ["r"] = (Readme, "readme") },
                new Dictionary<string, (string, string)> { ["m"] = (Mat, "hdrp sea"), ["s"] = (Script, "hdrp sky"), ["x"] = (Extra, "hdrp extra") },
                Asset);
            // The project as the pipeline installer left it after an HDRP install.
            _project = new FakeProject();
            _project.paths["dD"] = Root + "/Demo";
            _project.Put("m", Mat, "hdrp sea", "meta-m");
            _project.Put("s", Script, "hdrp sky", "meta-s");
            _project.Put("x", Extra, "hdrp extra", "meta-x");
            _project.Put("r", Readme, "readme", "meta-r");
        }

        [Test]
        public void Read_TakesEachFileWithItsGuidAndHashes()
        {
            KitPipelineContent hdrp = _installed.hdrp;
            Assert.That(hdrp.files.Keys, Is.EquivalentTo(new[] { "m", "s", "x" }));
            Assert.That(hdrp.files["m"].path, Is.EqualTo(Mat));
            Assert.That(hdrp.files["m"].hash, Is.EqualTo(H("hdrp sea")));
            Assert.That(hdrp.files["m"].meta, Is.EqualTo(H("meta-m")));
            Assert.That(hdrp.remove, Is.EquivalentTo(new[] { Asset }));
            Assert.That(Encoding.UTF8.GetString(hdrp.data[Mat]), Is.EqualTo("hdrp sea"));
        }

        [Test]
        public void TheProjectHoldsTheVersionMostOfItsDifferingFilesMatch()
        {
            Assert.That(_installed.hdrp.IsInstalled(_installed.manifest, _project), Is.True);

            _project.Put("m", Mat, "urp sea", "meta-m");
            _project.Put("s", Script, "urp sky", "meta-s");
            _project.paths.Remove("x");
            Assert.That(_installed.hdrp.IsInstalled(_installed.manifest, _project), Is.False, "the kit's own URP content");
        }

        [Test]
        public void AnHdrpKit_ScansClean()
        {
            KitScan scan = KitScanner.Scan(_installed.hdrp.Apply(_installed.manifest), Root, _project);
            Assert.That(scan.changes, Is.Empty, "neither the HDRP files are edits nor the removed URP asset a deletion");
            Assert.That(scan.unchanged, Is.EqualTo(4));
        }

        [Test]
        public void AnHdrpKit_StillShowsTheUsersOwnEdits()
        {
            _project.Put("m", Mat, "my sea", "meta-m");
            KitScan scan = KitScanner.Scan(_installed.hdrp.Apply(_installed.manifest), Root, _project);
            Assert.That(scan.changes.Single().kind, Is.EqualTo(KitChangeKind.Edited));
            Assert.That(scan.changes.Single().guid, Is.EqualTo("m"));
        }

        [Test]
        public void AnUpdate_WritesTheTargetsHdrpVersions_AndKeepsTheUsersEdits()
        {
            // 1.1.6 changes the sea in both pipelines, the sky in URP only, and adds an HDRP-only file.
            Release target = MakeRelease("1.1.6",
                new Dictionary<string, (string, string)> { ["m"] = (Mat, "urp sea 2"), ["s"] = (Script, "urp sky 2"), ["u"] = (Asset, "urp asset"), ["r"] = (Readme, "readme") },
                new Dictionary<string, (string, string)> { ["m"] = (Mat, "hdrp sea 2"), ["s"] = (Script, "hdrp sky"), ["x"] = (Extra, "hdrp extra"), ["n"] = ("Demo/HDRPNew.cs", "hdrp new") },
                Asset);
            var source = new PipelineKitSource(new FakeSource(target), target.hdrp);

            KitUpdatePlan plan = KitUpdatePlan.Make(_installed.hdrp.Apply(_installed.manifest), source.Manifest, Root, _project);

            KitAction sea = plan.actions.Single(a => a.guid == "m");
            Assert.That(sea.kind, Is.EqualTo(KitActionKind.Update));
            Assert.That(sea.WritesContent, Is.True);
            Assert.That(plan.actions.Any(a => a.guid == "s"), Is.False, "a change to the URP version only leaves the HDRP one alone");
            Assert.That(plan.actions.Any(a => a.guid == "u"), Is.False, "the URP-only asset stays out of an HDRP kit");
            Assert.That(plan.actions.Single(a => a.guid == "n").kind, Is.EqualTo(KitActionKind.Add));

            byte[] written = source.FetchAsync(Mat, CancellationToken.None).Result;
            Assert.That(Encoding.UTF8.GetString(written), Is.EqualTo("hdrp sea 2"), "the HDRP version comes from the target's archive");
            Assert.That(KitHash.Of(written), Is.EqualTo(source.Manifest.files["m"].hash), "and passes the download's hash check");
            Assert.That(source.ManifestText, Is.EqualTo("release manifest"), "the release's own manifest is what gets recorded");

            _project.Put("m", Mat, "my sea", "meta-m");
            plan = KitUpdatePlan.Make(_installed.hdrp.Apply(_installed.manifest), source.Manifest, Root, _project);
            Assert.That(plan.actions.Single(a => a.guid == "m").kind, Is.EqualTo(KitActionKind.EditedByYou));
        }

        [Test]
        public void TheKitsOwnPipelinePacksReadBack()
        {
            // The real archives shipped in this kit: every file they hold must map to a kit path, with contents.
            string json = AssetDatabase.GUIDToAssetPath("1da19d9091854adbaac8c7fc8d995078");
            string archive = AssetDatabase.GUIDToAssetPath("24cb647b30e64e0b8bd513347d1ebe30");
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(archive))
                Assert.Ignore("This project has no pipeline packs.");
            KitPipelineContent hdrp = KitPipelineContent.Read(File.ReadAllText(json), File.ReadAllBytes(archive), "hdrp");
            Assert.That(hdrp.files.Count, Is.GreaterThan(100));
            Assert.That(hdrp.files.Values.All(f => !f.path.StartsWith("Assets/") && f.hash != null && f.meta != null), Is.True);
            Assert.That(hdrp.remove, Is.Not.Empty);
        }

        private class FakeSource : IKitSource
        {
            private readonly Release _release;
            public FakeSource(Release release) { _release = release; }
            public string Description { get { return "fake"; } }
            public KitManifest Manifest { get { return _release.manifest; } }
            public string ManifestText { get { return "release manifest"; } }
            public Task<byte[]> FetchAsync(string kitRelativePath, CancellationToken cancel) { throw new IOException("not in the fake: " + kitRelativePath); }
            public string CompareUrl(string fromTag) { return null; }
        }

        private static byte[] Tar(List<KeyValuePair<string, byte[]>> files)
        {
            var tar = new MemoryStream();
            foreach (var file in files)
            {
                var header = new byte[512];
                Encoding.ASCII.GetBytes(file.Key).CopyTo(header, 0);
                Encoding.ASCII.GetBytes("0000644\0").CopyTo(header, 100);
                Encoding.ASCII.GetBytes(Convert.ToString(file.Value.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
                header[156] = (byte)'0';
                Encoding.ASCII.GetBytes("ustar\0" + "00").CopyTo(header, 257);
                tar.Write(header, 0, 512);
                tar.Write(file.Value, 0, file.Value.Length);
                tar.Write(new byte[(512 - file.Value.Length % 512) % 512], 0, (512 - file.Value.Length % 512) % 512);
            }
            tar.Write(new byte[1024], 0, 1024);
            return tar.ToArray();
        }

        private static byte[] Gzip(byte[] data)
        {
            var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
                gzip.Write(data, 0, data.Length);
            return output.ToArray();
        }
    }
}
