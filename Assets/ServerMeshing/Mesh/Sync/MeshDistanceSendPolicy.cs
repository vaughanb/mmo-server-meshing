// Server Meshing add-on — the state sender's default policy: rate tiers by distance, owners at their own rate.
using System;
using System.Globalization;

namespace MultiplayerARPG.MMO.Mesh.Sync
{
    /// <summary>
    /// MeshDistanceSendPolicy gives an owner pair OwnerHz and every other pair a tier by distance: near up to half of R0
    /// (and when the distance is unknown), mid up to R0, far beyond, the far tier in FarVariant when the source has a
    /// compact encoding. It never withholds and allocates nothing.
    /// </summary>
    public sealed class MeshDistanceSendPolicy : IMeshSendPolicy
    {
        /// <summary>DefaultHz is every tier's rate when no tiers are given.</summary>
        public const byte DefaultHz = 20;

        private readonly float _nearLimit;
        private readonly MeshSendRate _owner;
        private readonly MeshSendRate _near;
        private readonly MeshSendRate _mid;
        private readonly MeshSendRate _far;
        private readonly MeshSendRate _farCompact;

        /// <summary>
        /// MeshDistanceSendPolicy checks and keeps the tiers: <paramref name="r0"/> positive and finite metres, every Hz one
        /// of 0, 1, 2, 5, 10 or 20.
        /// </summary>
        public MeshDistanceSendPolicy(float r0, byte nearHz, byte midHz, byte farHz, byte ownerHz, MeshStateVariant farVariant)
        {
            if (!(r0 > 0f) || float.IsInfinity(r0))
                throw new ArgumentOutOfRangeException(nameof(r0), r0, "the distance tier radius must be positive and finite");
            CheckHz(nearHz, nameof(nearHz));
            CheckHz(midHz, nameof(midHz));
            CheckHz(farHz, nameof(farHz));
            CheckHz(ownerHz, nameof(ownerHz));
            R0 = r0;
            NearHz = nearHz;
            MidHz = midHz;
            FarHz = farHz;
            OwnerHz = ownerHz;
            FarVariant = farVariant;
            _nearLimit = 0.5f * r0;
            _owner = new MeshSendRate(ownerHz, 3);
            _near = new MeshSendRate(nearHz, 2);
            _mid = new MeshSendRate(midHz, 1);
            _far = new MeshSendRate(farHz, 0);
            _farCompact = new MeshSendRate(farHz, 0, farVariant);
        }

        /// <summary>R0 is the far edge of the mid tier in metres; the near tier ends at half of it.</summary>
        public float R0 { get; }

        /// <summary>NearHz is the rate up to half of R0.</summary>
        public byte NearHz { get; }

        /// <summary>MidHz is the rate up to R0.</summary>
        public byte MidHz { get; }

        /// <summary>FarHz is the rate beyond R0.</summary>
        public byte FarHz { get; }

        /// <summary>OwnerHz is the rate of a pair whose observer owns the source.</summary>
        public byte OwnerHz { get; }

        /// <summary>FarVariant is the far tier's encoding for a source that has a compact one.</summary>
        public MeshStateVariant FarVariant { get; }

        /// <summary>Classify returns the owner rate for an owner pair, else the tier of the pair's distance.</summary>
        public MeshSendRate Classify(IMeshStateSource source, in MeshObserverContext observer)
        {
            if (observer.IsOwner)
                return _owner;
            float distance = observer.Distance;
            if (float.IsNaN(distance) || distance <= _nearLimit)
                return _near;
            if (distance <= R0)
                return _mid;
            return source is IMeshCompactStateSource ? _farCompact : _far;
        }

        /// <summary>
        /// TryParseTiers reads "near/mid/far" or "near/mid/far/owner" (owner defaults to 20), each 0, 1, 2, 5, 10 or 20.
        /// Null, empty or blank is unset: every tier 20 and no error. On failure every tier is 20 and
        /// <paramref name="error"/> says why.
        /// </summary>
        public static bool TryParseTiers(string value, out byte near, out byte mid, out byte far, out byte owner, out string error)
        {
            near = mid = far = owner = DefaultHz;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
                return true;
            string[] parts = value.Trim().Split('/');
            if (parts.Length != 3 && parts.Length != 4)
            {
                error = $"tiers \"{value}\" must be near/mid/far or near/mid/far/owner";
                return false;
            }
            byte[] rates = new byte[4] { DefaultHz, DefaultHz, DefaultHz, DefaultHz };
            for (int i = 0; i < parts.Length; ++i)
            {
                if (!byte.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rates[i]) || !MeshStateFanout.IsAllowedHz(rates[i]))
                {
                    error = $"tiers \"{value}\": \"{parts[i]}\" is not 0, 1, 2, 5, 10 or 20";
                    return false;
                }
            }
            near = rates[0];
            mid = rates[1];
            far = rates[2];
            owner = rates[3];
            return true;
        }

        private static void CheckHz(byte hz, string name)
        {
            if (!MeshStateFanout.IsAllowedHz(hz))
                throw new ArgumentOutOfRangeException(name, hz, "a rate must be 0, 1, 2, 5, 10 or 20 Hz");
        }
    }
}
