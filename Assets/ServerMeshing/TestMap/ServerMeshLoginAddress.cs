// Server Meshing test rig — a bot's login server address (Phase 0 step 8b, -meshLoginAddress host[:port]): a bot on another
// machine connects to machine A's login server instead of the address its build was made with. Absent, nothing changes.
using System;
using System.Globalization;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshLoginAddress reads -meshLoginAddress host[:port] once, after the first scene loaded (the bot's BaseInit-CE
    /// scene, whose MMOClientInstance set its singleton in Awake), and writes it into the login network manager's
    /// networkAddress (and networkPort when given). Every later connect to the login server, the auto client's first and each
    /// reconnect after a map stop or migration, reads those fields. Without the argument it returns and logs nothing.
    /// </summary>
    public static class ServerMeshLoginAddress
    {
        /// <summary>ArgLoginAddress is the command-line argument: host or host:port, port from 1 to 65535.</summary>
        public const string ArgLoginAddress = "-meshLoginAddress";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            string value = ArgumentValue(Environment.GetCommandLineArgs(), ArgLoginAddress);
            if (value == null)
                return;
            if (!TryParse(value, out string host, out int port, out string error))
            {
                Logging.LogError("Mesh", ServerMeshPhantomSettings.Printable($"Login address error: {error}; the build's address is kept"));
                return;
            }
            MMOClientInstance client = MMOClientInstance.Singleton;
            if (client == null)
            {
                Logging.LogError("Mesh", "Login address error: no MMOClientInstance");
                return;
            }
            LoginNetworkManager login = client.LoginNetworkManager;
            if (login == null)
            {
                Logging.LogError("Mesh", "Login address error: no LoginNetworkManager");
                return;
            }
            login.networkAddress = host;
            if (port > 0)
                login.networkPort = port;
            Logging.Log("Mesh", ServerMeshPhantomSettings.Printable(Line(login.networkAddress, login.networkPort)));
        }

        /// <summary>
        /// ArgumentValue is the text after the first <paramref name="name"/> in <paramref name="args"/>, or null when the
        /// argument is absent or last.
        /// </summary>
        public static string ArgumentValue(string[] args, string name)
        {
            if (args == null)
                return null;
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (args[i] == name)
                    return args[i + 1];
            }
            return null;
        }

        /// <summary>
        /// TryParse reads host[:port]: the host is trimmed and must not be empty or hold whitespace; the port, when present,
        /// is an invariant integer from 1 to 65535 with no sign. Without a port, <paramref name="port"/> is 0 (keep the
        /// manager's). More than one colon is refused (no IPv6 literals). False with an error naming the argument otherwise.
        /// </summary>
        public static bool TryParse(string text, out string host, out int port, out string error)
        {
            host = null;
            port = 0;
            error = null;
            string value = text == null ? string.Empty : text.Trim();
            int colon = value.IndexOf(':');
            if (colon >= 0 && value.IndexOf(':', colon + 1) >= 0)
            {
                error = $"{ArgLoginAddress} {text} has more than one colon; use host or host:port";
                return false;
            }
            string h = (colon < 0 ? value : value.Substring(0, colon)).Trim();
            if (h.Length == 0)
            {
                error = $"{ArgLoginAddress} {text} has no host";
                return false;
            }
            for (int i = 0; i < h.Length; ++i)
            {
                if (char.IsWhiteSpace(h[i]))
                {
                    error = $"{ArgLoginAddress} {text}: host {h} holds a space";
                    return false;
                }
            }
            int p = 0;
            if (colon >= 0)
            {
                string portText = value.Substring(colon + 1).Trim();
                if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out p) || p < 1 || p > 65535)
                {
                    error = $"{ArgLoginAddress} {text}: port {portText} is not from 1 to 65535";
                    return false;
                }
            }
            host = h;
            port = p;
            return true;
        }

        /// <summary>Line is the log line once the address is set: "Login address: host:port from -meshLoginAddress".</summary>
        public static string Line(string host, int port)
        {
            return FormattableString.Invariant($"Login address: {host}:{port} from {ArgLoginAddress}");
        }
    }
}
