// Server Meshing test rig — runs the add-on's EditMode tests for the harness (regress.py --unit).
//
// ServerMeshPlayRunner hands a Temp/ServerMesh.TestRequest.txt to Run; this starts the Test Runner on the EditMode tests
// of Assembly-CSharp-Editor whose names start with the add-on's namespace, and writes
//   Temp/ServerMesh.TestStatus.txt    state=running|done|error, seq, selected, passed, failed, skipped, failure= lines
//   Temp/ServerMesh.TestResults.xml   the NUnit XML of the run
// The request's first line may carry a sequence number the harness waits for; it is echoed as seq=. A run that cannot
// start (a dirty scene would open the Test Runner's save dialog; another run is in progress) or that the Test Runner
// aborts ends in state=error with the reason, so the harness never waits for a result that cannot come.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    /// <summary>ServerMeshTestRunner runs the add-on's EditMode tests and reports them to Temp for the harness.</summary>
    public static class ServerMeshTestRunner
    {
        public const string NamespacePrefix = "MultiplayerARPG.MMO.Mesh";
        private static readonly string Root = Path.GetDirectoryName(Application.dataPath) ?? "";
        public static readonly string StatusPath = Path.Combine(Root, "Temp", "ServerMesh.TestStatus.txt");
        public static readonly string ResultsPath = Path.Combine(Root, "Temp", "ServerMesh.TestResults.xml");

        /// <summary>IsRunning is true while a run started by <see cref="Run"/> has not finished.</summary>
        public static bool IsRunning { get; private set; }

        private static TestRunnerApi _api;
        private static Callbacks _callbacks;

        /// <summary>Run starts the EditMode tests of the add-on; progress and the result go to <see cref="StatusPath"/>.</summary>
        public static void Run(string seq)
        {
            if (IsRunning)
            {
                WriteStatus("error", seq, 0, 0, 0, 0, new List<string> { "a test run is already in progress" });
                return;
            }
            for (int i = 0; i < SceneManager.sceneCount; ++i)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    // The Test Runner would ask to save it in a modal dialog, and a cancel there aborts the run silently.
                    WriteStatus("error", seq, 0, 0, 0, 0, new List<string> { $"scene {(scene.path.Length > 0 ? scene.path : scene.name)} has unsaved changes; save or discard them first" });
                    return;
                }
            }
            if (_api == null)
                _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            if (_callbacks != null)
                _api.UnregisterCallbacks(_callbacks);
            _callbacks = new Callbacks(seq);
            _api.RegisterCallbacks(_callbacks);
            IsRunning = true;
            WriteStatus("running", seq, 0, 0, 0, 0, new List<string>());
            Filter filter = new Filter
            {
                testMode = TestMode.EditMode,
                assemblyNames = new[] { "Assembly-CSharp-Editor" },
                groupNames = new[] { "^" + NamespacePrefix.Replace(".", "\\.") },
            };
            _api.Execute(new ExecutionSettings(filter) { runSynchronously = false });
        }

        private static void WriteStatus(string state, string seq, int selected, int passed, int failed, int skipped, List<string> failures)
        {
            StringBuilder text = new StringBuilder();
            text.Append("state=").Append(state).Append('\n');
            text.Append("seq=").Append(seq).Append('\n');
            text.Append("selected=").Append(selected).Append('\n');
            text.Append("passed=").Append(passed).Append('\n');
            text.Append("failed=").Append(failed).Append('\n');
            text.Append("skipped=").Append(skipped).Append('\n');
            foreach (string failure in failures)
                text.Append("failure=").Append(failure.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
            File.WriteAllText(StatusPath, text.ToString());
        }

        private sealed class Callbacks : IErrorCallbacks
        {
            private readonly string _seq;
            private int _selected;

            public Callbacks(string seq)
            {
                _seq = seq;
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                _selected = CountLeaves(testsToRun);
            }

            /// <summary>The Test Runner aborted the run (a cancel or an exception): RunFinished never follows.</summary>
            public void OnError(string message)
            {
                IsRunning = false;
                WriteStatus("error", _seq, _selected, 0, 0, 0, new List<string> { "the Test Runner aborted the run: " + message });
                Debug.LogError("[MeshTests] run aborted: " + message);
            }

            private static int CountLeaves(ITestAdaptor node)
            {
                if (node == null)
                    return 0;
                if (!node.HasChildren)
                    return node.IsSuite ? 0 : 1;
                int count = 0;
                foreach (ITestAdaptor child in node.Children)
                    count += CountLeaves(child);
                return count;
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                IsRunning = false;
                List<string> failures = new List<string>();
                int passed = 0, failed = 0, skipped = 0;
                Collect(result, failures, ref passed, ref failed, ref skipped);
                try
                {
                    TestRunnerApi.SaveResultToFile(result, ResultsPath);
                }
                catch (Exception ex)
                {
                    failures.Add("could not save the NUnit XML: " + ex.Message);
                }
                WriteStatus("done", _seq, _selected, passed, failed, skipped, failures);
                Debug.Log($"[MeshTests] {passed} passed, {failed} failed, {skipped} skipped");
            }

            private static void Collect(ITestResultAdaptor node, List<string> failures, ref int passed, ref int failed, ref int skipped)
            {
                if (!node.HasChildren)
                {
                    switch (node.TestStatus)
                    {
                        case TestStatus.Passed: passed++; break;
                        case TestStatus.Failed: failed++; failures.Add(node.FullName + ": " + node.Message); break;
                        default: skipped++; break;
                    }
                    return;
                }
                foreach (ITestResultAdaptor child in node.Children)
                    Collect(child, failures, ref passed, ref failed, ref skipped);
            }
        }
    }
}
