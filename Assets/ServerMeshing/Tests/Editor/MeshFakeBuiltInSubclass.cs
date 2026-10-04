namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>MeshFakeBuiltInSubclass is a subclass of the kit's built-in handler, only ever used as a type (the allow-list is exact).</summary>
    internal sealed class MeshFakeBuiltInSubclass : BuiltInEntityMovementFunctions3D
    {
        private MeshFakeBuiltInSubclass() : base(null, null, null)
        {
        }
    }
}
