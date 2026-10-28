using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// IMeshStateSource is an entity whose server-to-client state MeshStateSender sends. The game owns every byte WriteState
    /// writes and how its client reads them; the sender decides only when WriteState runs and which subscribers receive an
    /// unchanged copy, as an EntityState (119) record.
    /// </summary>
    public interface IMeshStateSource
    {
        /// <summary>ObjectId is the network object id and the record key.</summary>
        uint ObjectId { get; }

        /// <summary>Identity is the network identity whose subscribers receive the state; the sender never changes it.</summary>
        LiteNetLibIdentity Identity { get; }

        /// <summary>
        /// DiscreteVersion changes whenever every observer must see the next sample (a teleport sequence, a mode byte, an
        /// epoch); never for motion.
        /// </summary>
        uint DiscreteVersion { get; }

        /// <summary>
        /// IsAtRest is true when a velocity-extrapolating observer would see no change (parked, docked, stopped): linear speed
        /// under 0.01 m/s, angular speed under 0.001 rad/s, and nothing pending (an unacknowledged teleport, a frame change
        /// awaiting its ack). The sender decides before it writes, so a pending one-shot must never read as at rest.
        /// </summary>
        bool IsAtRest { get; }

        /// <summary>
        /// WriteState appends the complete payload as of sampleTimestamp. It is called at most once per build, only appends
        /// (never Reset or SetPosition), changes no state (a second call writes identical bytes), and returns false to send
        /// nothing this build. sendReliably routes the record to ReliableOrdered for every subscriber.
        /// </summary>
        bool WriteState(long sampleTimestamp, NetDataWriter writer, out bool sendReliably);
    }

    /// <summary>IMeshCompactStateSource adds an optional smaller encoding that its client tells apart by the payload's own header.</summary>
    public interface IMeshCompactStateSource : IMeshStateSource
    {
        /// <summary>WriteCompactState appends the compact payload under WriteState's rules; it is never sent reliably.</summary>
        bool WriteCompactState(long sampleTimestamp, NetDataWriter writer);
    }

    /// <summary>
    /// IMeshSenderOwnedSource is a direct source that is also the kit's movement handler for its object, so it writes
    /// nothing to the kit while the sender holds it.
    /// </summary>
    public interface IMeshSenderOwnedSource : IMeshStateSource
    {
        /// <summary>
        /// OnSenderReleased runs, on the main thread, whenever the sender lets go of the source without an Unregister call
        /// (server stop, object destroyed, dropped by the fan-out); the source then goes back to being written by the kit.
        /// It only clears the source's own state and never calls the registry.
        /// </summary>
        void OnSenderReleased();
    }

    /// <summary>IMeshStateRegistry is the sender's registration surface on the network manager's GameObject.</summary>
    public interface IMeshStateRegistry
    {
        /// <summary>Register hands the source to the sender; false keeps it on the kit path (no sender, duplicate id).</summary>
        bool Register(IMeshStateSource source);

        /// <summary>Unregister forgets the source (despawn, handoff).</summary>
        void Unregister(IMeshStateSource source);

        /// <summary>MarkRelevanceChanged re-classifies one pair on the next build (a lock, an attack, a group, a seat).</summary>
        void MarkRelevanceChanged(uint objectId, long observerConnectionId);

        /// <summary>MarkSourceChanged re-classifies every pair of one source on the next build.</summary>
        void MarkSourceChanged(uint objectId);

        /// <summary>MarkObserverChanged re-classifies every pair of one observer on the next build.</summary>
        void MarkObserverChanged(long observerConnectionId);
    }

    /// <summary>MeshStateVariant is the encoding one pair receives.</summary>
    public enum MeshStateVariant : byte
    {
        /// <summary>Full is WriteState's payload.</summary>
        Full = 0,

        /// <summary>Compact is WriteCompactState's payload; only an IMeshCompactStateSource has one.</summary>
        Compact = 1,
    }

    /// <summary>MeshObserverContext describes one (source, observer) pair to the policy.</summary>
    public readonly struct MeshObserverContext
    {
        /// <summary>MeshObserverContext creates a pair description.</summary>
        public MeshObserverContext(long observerConnectionId, float distance, bool isOwner)
        {
            ObserverConnectionId = observerConnectionId;
            Distance = distance;
            IsOwner = isOwner;
        }

        /// <summary>ObserverConnectionId is the receiving client.</summary>
        public long ObserverConnectionId { get; }

        /// <summary>
        /// Distance is metres from the observer's anchor (its character; when seated, the seat) to the source, 3D or X/Z as
        /// the sender is configured; NaN when unknown.
        /// </summary>
        public float Distance { get; }

        /// <summary>IsOwner is true when the observer owns the source.</summary>
        public bool IsOwner { get; }
    }

    /// <summary>MeshSendRate is one observer's routine rate for one source.</summary>
    public readonly struct MeshSendRate
    {
        /// <summary>
        /// Withhold sends nothing to an observer that does not own the source, not even urgent, reliable or first-send
        /// records, so that pair loses one-shots for good; the sender treats Withhold on an owner pair as Hz 0.
        /// </summary>
        public static readonly MeshSendRate Withhold = new MeshSendRate(0, 0, MeshStateVariant.Full, true);

        /// <summary>MeshSendRate creates a Full-variant rate.</summary>
        public MeshSendRate(byte hz, byte priority) : this(hz, priority, MeshStateVariant.Full, false) { }

        /// <summary>MeshSendRate creates a rate with the encoding for routine records.</summary>
        public MeshSendRate(byte hz, byte priority, MeshStateVariant variant) : this(hz, priority, variant, false) { }

        private MeshSendRate(byte hz, byte priority, MeshStateVariant variant, bool isWithheld)
        {
            Hz = hz;
            Priority = priority;
            Variant = variant;
            IsWithheld = isWithheld;
        }

        /// <summary>Hz is 0, 1, 2, 5, 10 or 20 (a whole divisor of the send rate); 0 still receives urgent and first-send records.</summary>
        public byte Hz { get; }

        /// <summary>Priority orders routine records when a budget is short; Phase 0 carries it without a budget.</summary>
        public byte Priority { get; }

        /// <summary>Variant is the encoding for routine records; Full whenever the source has no compact encoding.</summary>
        public MeshStateVariant Variant { get; }

        /// <summary>IsWithheld is true only for Withhold.</summary>
        public bool IsWithheld { get; }
    }

    /// <summary>IMeshSendPolicy decides routine rates; the first found on the network manager's GameObject wins. Classify must not allocate.</summary>
    public interface IMeshSendPolicy
    {
        /// <summary>Classify returns the rate for one pair: when the pair is new, at the classify rate, and after a Mark call or a DiscreteVersion change.</summary>
        MeshSendRate Classify(IMeshStateSource source, in MeshObserverContext observer);
    }
}
