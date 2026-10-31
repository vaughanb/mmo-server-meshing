using System;
using System.Collections.Generic;
using MultiplayerARPG.MMO;
using Newtonsoft.Json;
using NUnit.Framework;

namespace OpenMMORPG.Tests
{
    /// <summary>
    /// How the servers read their command line, environment and config values. The map spawn
    /// server launches every map server with its channel, map, ports and cluster address on the
    /// command line, so a misread argument starts a map on the wrong port or channel, and an
    /// environment variable must win over the config file (that is how containers configure the
    /// servers). Environment variables set here have unique names and are removed afterwards.
    /// </summary>
    [Category(TestCategories.Logic)]
    public class ConfigReaderTests : KitTestFixture
    {
        private readonly List<string> _setVariables = new List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string name in _setVariables)
                Environment.SetEnvironmentVariable(name, null);
            _setVariables.Clear();
        }

        private string SetEnv(string value)
        {
            string name = "OPENMMORPG_TEST_" + Guid.NewGuid().ToString("N");
            _setVariables.Add(name);
            Environment.SetEnvironmentVariable(name, value);
            return name;
        }

        [Test]
        public void AnArgumentIsTheValueAfterItsName()
        {
            string[] args = { "-batchmode", "-channelId", "eu", "-mapName", "Town", "-mapPort", "8001" };

            Assert.That(ConfigReader.ReadArgs(args, "-mapName", out string mapName, "Default"), Is.True);
            Assert.That(mapName, Is.EqualTo("Town"));
            Assert.That(ConfigReader.ReadArgs(args, "-channelId", out string channel, "default"), Is.True);
            Assert.That(channel, Is.EqualTo("eu"));
            Assert.That(ConfigReader.ReadArgs(args, "-mapPort", out int port, 7000), Is.True);
            Assert.That(port, Is.EqualTo(8001));
            Assert.That(ConfigReader.IsArgsProvided(args, "-batchmode"), Is.True);
            Assert.That(ConfigReader.IsArgsProvided(args, "-nographics"), Is.False);
        }

        [Test]
        public void AMissingArgumentKeepsTheDefaultAndAnUnparsableOneIsNotRead()
        {
            string[] args = { "-mapPort", "eighty", "-ratio", "x" };

            Assert.That(ConfigReader.ReadArgs(args, "-loginPort", out int loginPort, 6000), Is.False);
            Assert.That(loginPort, Is.EqualTo(6000));
            // An unparsable value reports false, so the caller keeps its own setting.
            Assert.That(ConfigReader.ReadArgs(args, "-mapPort", out int _, 7000), Is.False);
            Assert.That(ConfigReader.ReadArgs(args, "-ratio", out float _, 1.5f), Is.False);
            Assert.That(ConfigReader.ReadArgs(null, "-mapName", out string mapName, "Town"), Is.False);
            Assert.That(mapName, Is.EqualTo("Town"));
        }

        [Test]
        public void AnArgumentNameWithNoValueAfterItKeepsTheDefault()
        {
            string[] args = { "-batchmode", "-mapName" };

            Assert.That(ConfigReader.ReadArgs(args, "-mapName", out string mapName, "Town"), Is.False);
            Assert.That(mapName, Is.EqualTo("Town"));
        }

        [Test]
        public void ListArgumentsAndVariablesSplitOnBars()
        {
            string[] args = { "-spawnChannels", "eu|us|asia" };
            string variable = SetEnv("Town|Forest");

            Assert.That(ConfigReader.ReadArgs(args, "-spawnChannels", out List<string> channels, null), Is.True);
            Assert.That(channels, Is.EqualTo(new[] { "eu", "us", "asia" }));
            Assert.That(ConfigReader.ReadEnv(variable, out List<string> maps, null), Is.True);
            Assert.That(maps, Is.EqualTo(new[] { "Town", "Forest" }));

            Assert.That(ConfigReader.ReadArgs(args, "-spawnMaps", out List<string> fallback, new List<string> { "Town" }), Is.False);
            Assert.That(fallback, Is.EqualTo(new[] { "Town" }), "a missing list falls back to a copy of the default");
        }

        [Test]
        public void EnvironmentVariablesAreReadByTypeAndBlankOrUnparsableOnesAreNot()
        {
            string port = SetEnv("6010");
            string flag = SetEnv("True");
            string blank = SetEnv("   ");
            string bad = SetEnv("not-a-number");

            Assert.That(ConfigReader.ReadEnv(port, out int portValue, 0), Is.True);
            Assert.That(portValue, Is.EqualTo(6010));
            Assert.That(ConfigReader.ReadEnv(flag, out bool flagValue, false), Is.True);
            Assert.That(flagValue, Is.True);
            Assert.That(ConfigReader.ReadEnv(blank, out string blankValue, "fallback"), Is.False);
            Assert.That(blankValue, Is.EqualTo("fallback"));
            Assert.That(ConfigReader.ReadEnv(bad, out int _, 42), Is.False);
        }

        [Test]
        public void ConfigFileValuesAreReadByType()
        {
            Dictionary<string, object> config = JsonConvert.DeserializeObject<Dictionary<string, object>>(
                "{ \"address\": \"10.0.0.2\", \"port\": 6010, \"rate\": 0.5, \"secure\": true, \"maps\": [\"Town\", \"Forest\"] }");

            Assert.That(ConfigReader.ReadConfigs(config, "address", out string address), Is.True);
            Assert.That(address, Is.EqualTo("10.0.0.2"));
            Assert.That(ConfigReader.ReadConfigs(config, "port", out int port), Is.True);
            Assert.That(port, Is.EqualTo(6010));
            Assert.That(ConfigReader.ReadConfigs(config, "rate", out float rate), Is.True);
            Assert.That(rate, Is.EqualTo(0.5f));
            Assert.That(ConfigReader.ReadConfigs(config, "secure", out bool secure), Is.True);
            Assert.That(secure, Is.True);
            Assert.That(ConfigReader.ReadConfigs(config, "maps", out List<string> maps), Is.True);
            Assert.That(maps, Is.EqualTo(new[] { "Town", "Forest" }));
            Assert.That(ConfigReader.ReadConfigs(config, "missing", out int missing, 9), Is.False);
            Assert.That(missing, Is.EqualTo(9));
        }

        [Test]
        public void AnEnvironmentVariableWinsOverTheConfigFile()
        {
            string name = SetEnv("7777");
            Dictionary<string, object> config = new Dictionary<string, object>() { { name, 6010L } };

            Assert.That(ConfigReader.ReadConfigs(config, name, out int port), Is.True);
            Assert.That(port, Is.EqualTo(7777));
            Assert.That(ConfigReader.ReadConfigs(null, name, out string text), Is.True, "the variable is read even with no config file");
            Assert.That(text, Is.EqualTo("7777"));
        }
    }
}
