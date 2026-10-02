using System.Collections.Generic;
using NUnit.Framework;
using MultiplayerARPG.MMO.Mesh.Editor;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    public class MeshPackageStrayReferenceTests
    {
        private static KeyValuePair<string, string> Script(string path, string text) => new KeyValuePair<string, string>(path, text);

        private static bool ShipsMesh(string path) => path.StartsWith("Assets/ServerMeshing/Mesh/");

        [Test]
        public void ShippedScriptNamingALeftBehindType_IsReported()
        {
            List<string> strays = ServerMeshPackageBuilder.FindStrayReferences(new[]
            {
                Script("Assets/ServerMeshing/Mesh/A.cs", "public class A { // the rig's Placement\n}"),
                Script("Assets/ServerMeshing/TestMap/B.cs", "    internal struct Placement { }"),
            }, ShipsMesh);

            Assert.That(strays, Is.EqualTo(new[] { "Assets/ServerMeshing/Mesh/A.cs uses Placement (Assets/ServerMeshing/TestMap/B.cs)" }));
        }

        [TestCase("    public readonly struct Placement { }")]
        [TestCase("    internal ref struct Placement { }")]
        [TestCase("    readonly partial struct Placement { }")]
        [TestCase("    public record Placement(int x);")]
        [TestCase("    public record struct Placement(int x);")]
        [TestCase("    public delegate void Placement(int x);")]
        [TestCase("    delegate Dictionary<string, int> Placement<T>(T x);")]
        public void LeftBehindDeclarationForm_IsReported(string declaration)
        {
            List<string> strays = ServerMeshPackageBuilder.FindStrayReferences(new[]
            {
                Script("Assets/ServerMeshing/Mesh/A.cs", "class A { Placement p; }"),
                Script("Assets/ServerMeshing/TestMap/B.cs", declaration),
            }, ShipsMesh);

            Assert.That(strays, Is.EqualTo(new[] { "Assets/ServerMeshing/Mesh/A.cs uses Placement (Assets/ServerMeshing/TestMap/B.cs)" }));
        }

        [Test]
        public void AnonymousDelegate_IsNoDeclaration()
        {
            List<string> strays = ServerMeshPackageBuilder.FindStrayReferences(new[]
            {
                Script("Assets/ServerMeshing/Mesh/A.cs", "class A { Placement p; }"),
                Script("Assets/ServerMeshing/TestMap/B.cs", "    Func<int, int> f = delegate (int x) { return Placement(x); };\n    delegate (int y) { Placement(y); }"),
            }, ShipsMesh);

            Assert.That(strays, Is.Empty);
        }

        [Test]
        public void TypeCarriedByADependency_IsNotReported()
        {
            List<string> strays = ServerMeshPackageBuilder.FindStrayReferences(new[]
            {
                Script("Assets/ServerMeshing/Mesh/A.cs", "class A { Placement p; }"),
                Script("Assets/ServerMeshing/TestMap/B.cs", "struct Placement { }"),
            }, path => true);

            Assert.That(strays, Is.Empty);
        }

        [Test]
        public void ClassNamedInAComment_IsNoDeclaration_AndCompoundNamesAreNoUse()
        {
            List<string> strays = ServerMeshPackageBuilder.FindStrayReferences(new[]
            {
                Script("Assets/ServerMeshing/Mesh/A.cs", "class A { MeshHeldPlacementQueue<int> q; Ghost g; }"),
                Script("Assets/ServerMeshing/TestMap/B.cs", "// a class Ghost would go here\nenum Placement { }"),
            }, ShipsMesh);

            Assert.That(strays, Is.Empty);
        }

        [Test]
        public void EveryPackageOfThisProject_HasNoStrayReference()
        {
            Assert.That(ServerMeshPackageBuilder.StrayReferencesOfEveryPackage(), Is.Empty);
        }
    }
}
