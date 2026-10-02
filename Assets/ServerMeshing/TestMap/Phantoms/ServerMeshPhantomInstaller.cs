// Server Meshing add-on — rig phantom installer (MESH_PHANTOM): splices the phantom transport factory into a map server's chain before it starts, and creates the binder.
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomInstaller runs once after the first scene loads. It does nothing unless MESH_PHANTOM is set and every
    /// phantom key parses (a refusal logs one warning), refuses in the editor, acts only in a -startMapServer process (the
    /// server has not started yet, so IsServer cannot tell), and needs the manager to be a MeshMapNetworkManager. It adds a
    /// ServerMeshPhantomTransportFactory to the manager, splices it under the metered factory when there is one (above the
    /// kit's factory otherwise), creates the binder on its own DontDestroyOnLoad GameObject and logs one setting line.
    /// </summary>
    public static class ServerMeshPhantomInstaller
    {
        /// <summary>BinderObjectName names the binder's GameObject.</summary>
        public const string BinderObjectName = "[Mesh] PhantomClients";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(ServerMeshPhantomSettings.ENV_PHANTOM)))
                return;
            if (Application.isEditor)
            {
                Logging.LogWarning("Mesh", $"Phantoms: {ServerMeshPhantomSettings.ENV_PHANTOM} is ignored in the editor; nothing installed");
                return;
            }
            if (!ConfigReader.IsArgsProvided(System.Environment.GetCommandLineArgs(), ProcessArguments.ARG_START_MAP_SERVER))
                return;
            if (!ServerMeshPhantomSettings.TryResolve(System.Environment.GetEnvironmentVariable, out ServerMeshPhantomSettings settings, out string warning))
            {
                if (warning != null)
                    Logging.LogWarning("Mesh", warning);
                return;
            }
            MeshMapNetworkManager mesh = BaseGameNetworkManager.Singleton as MeshMapNetworkManager;
            if (mesh == null)
            {
                Logging.LogError("Mesh", $"Phantoms error: {ServerMeshPhantomSettings.ENV_PHANTOM} is set but the map server's manager is not a {nameof(MeshMapNetworkManager)}; nothing installed");
                return;
            }
            if (Object.FindFirstObjectByType<ServerMeshPhantomClients>() != null)
                return;
            ServerMeshPhantomTransportFactory phantom = mesh.gameObject.AddComponent<ServerMeshPhantomTransportFactory>();
            mesh.TransportFactory = Splice(mesh.TransportFactory, phantom);
            GameObject go = new GameObject(BinderObjectName);
            Object.DontDestroyOnLoad(go);
            go.AddComponent<ServerMeshPhantomClients>().Setup(settings, new MeshKitLog());
            Logging.Log("Mesh", settings.SettingLine());
        }

        /// <summary>
        /// Splice puts phantom into a factory chain whose top is root and returns the new top: under a metered root
        /// (phantom.Inner = metered.Inner; metered.Inner = phantom; the root stays on top), otherwise above root
        /// (phantom.Inner = root; phantom is the new top). A null Inner later resolves to the kit's default factory.
        /// It never looks a factory up with GetComponent, which could return a wrapper itself.
        /// </summary>
        public static BaseTransportFactory Splice(BaseTransportFactory root, ServerMeshPhantomTransportFactory phantom)
        {
            if (phantom == null)
                throw new System.ArgumentNullException(nameof(phantom));
            if (root == phantom)
                throw new System.ArgumentException("the phantom factory is already the chain's top", nameof(root));
            MeshMeteredTransportFactory metered = root as MeshMeteredTransportFactory;
            if (metered != null)
            {
                phantom.Inner = metered.Inner == null ? null : metered.Inner;
                metered.Inner = phantom;
                return metered;
            }
            phantom.Inner = root == null ? null : root;
            return phantom;
        }
    }
}
