// Server Meshing add-on — transports that wrap another transport, and the walk down such a chain to the one underneath
// (the LiteNetLib transport whose statistics and peer counts the Mesh stats and Work window lines read).
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// IMeshWrappedTransport is a transport that forwards to another one, so code that needs a transport further down the
    /// chain can find it with <see cref="MeshTransportUnwrap.Innermost{T}"/>.
    /// </summary>
    public interface IMeshWrappedTransport
    {
        /// <summary>Inner is the transport this one forwards to, or null when it has none.</summary>
        ITransport Inner { get; }
    }

    /// <summary>MeshTransportUnwrap walks a chain of wrapped transports.</summary>
    public static class MeshTransportUnwrap
    {
        /// <summary>MaxDepth is how many Inner links the walk follows at most, so a chain that loops still ends.</summary>
        public const int MaxDepth = 8;

        /// <summary>
        /// Innermost returns the deepest transport of type <typeparamref name="T"/> in the chain that starts at
        /// <paramref name="transport"/> (the transport itself included), or null when there is none. It follows
        /// <see cref="IMeshWrappedTransport.Inner"/> at most <see cref="MaxDepth"/> times. A transport that does not
        /// implement IMeshWrappedTransport ends the walk: the kit's MixTransport is opaque, so a chain through it yields
        /// null, exactly as a plain type test on the outer transport does.
        /// </summary>
        public static T Innermost<T>(ITransport transport) where T : class, ITransport
        {
            T found = null;
            ITransport current = transport;
            for (int depth = 0; current != null && depth <= MaxDepth; ++depth)
            {
                if (current is T match)
                    found = match;
                if (!(current is IMeshWrappedTransport wrapped))
                    break;
                current = wrapped.Inner;
            }
            return found;
        }
    }
}
