// Server Meshing add-on — Setup window, the DISABLE_ADDRESSABLES check (DESIGN.md 0.99). The add-on compiles only with
// the define (MeshAddressablesGuard.cs), and each build target keeps its own list: a project whose Windows target has it
// and whose Dedicated Server target does not opens and plays in the editor, then fails the headless build.
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Profile;

namespace MultiplayerARPG.MMO.Mesh.Editor
{
    public partial class ServerMeshSetupWindow
    {
        private const string AddressablesDefine = "DISABLE_ADDRESSABLES";

        /// <summary>
        /// The define on the two targets a meshed game builds (the players and bots on Standalone, the headless servers on
        /// Dedicated Server), and on the active build profile, whose own defines add to its target's.
        /// </summary>
        private void AddDefineChecks()
        {
            AddTargetDefineCheck(NamedBuildTarget.Standalone, "Windows/Mac/Linux");
            AddTargetDefineCheck(NamedBuildTarget.Server, "Dedicated Server");

            BuildProfile profile = BuildProfile.GetActiveBuildProfile();
            if (profile == null)
                return;
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            NamedBuildTarget profileTarget = group == BuildTargetGroup.Standalone && EditorUserBuildSettings.standaloneBuildSubtarget == StandaloneBuildSubtarget.Server
                ? NamedBuildTarget.Server
                : NamedBuildTarget.FromBuildTargetGroup(group);
            bool inProfile = HasDefine(profile.scriptingDefines);
            bool inTarget = HasDefine(PlayerSettings.GetScriptingDefineSymbols(profileTarget).Split(';'));
            _checks.Add(new Check
            {
                ok = inProfile || inTarget,
                text = inProfile || inTarget
                    ? $"Build profile {profile.name}: {AddressablesDefine} defined" + (inProfile ? "." : $" (by the {profileTarget.TargetName} target).")
                    : $"Build profile {profile.name}: {AddressablesDefine} is not defined, neither in the profile nor on its {profileTarget.TargetName} target; the add-on does not compile in its builds.",
                fixLabel = "Add define",
                fix = () => AddProfileDefine(profile),
            });
        }

        private void AddTargetDefineCheck(NamedBuildTarget target, string label)
        {
            bool ok = HasDefine(PlayerSettings.GetScriptingDefineSymbols(target).Split(';'));
            _checks.Add(new Check
            {
                ok = ok,
                text = ok
                    ? $"{label} target: {AddressablesDefine} defined."
                    : $"{label} target: {AddressablesDefine} is not in its Scripting Define Symbols; the add-on does not compile for it (Player Settings, {label} tab).",
                fixLabel = "Add define",
                fix = () => AddTargetDefine(target),
            });
        }

        private static bool HasDefine(string[] defines)
        {
            if (defines == null) return false;
            foreach (string define in defines)
                if (define.Trim() == AddressablesDefine) return true;
            return false;
        }

        private static void AddTargetDefine(NamedBuildTarget target)
        {
            string current = PlayerSettings.GetScriptingDefineSymbols(target);
            PlayerSettings.SetScriptingDefineSymbols(target, string.IsNullOrEmpty(current) ? AddressablesDefine : current + ";" + AddressablesDefine);
            AssetDatabase.SaveAssets();
        }

        private static void AddProfileDefine(BuildProfile profile)
        {
            string[] current = profile.scriptingDefines ?? new string[0];
            string[] defines = new string[current.Length + 1];
            current.CopyTo(defines, 0);
            defines[current.Length] = AddressablesDefine;
            profile.scriptingDefines = defines;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
        }
    }
}
