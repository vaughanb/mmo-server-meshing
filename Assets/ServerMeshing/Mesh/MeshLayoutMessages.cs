// Server Meshing add-on — the cluster messages of dynamic cells (DESIGN.md 0.72, plan D3): the central's layout
// snapshots and notes to the map servers, and their hellos and acks back. Ids in MeshClusterMsg (30014-30018).
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshLayoutKind says why a layout was sent.</summary>
    public enum MeshLayoutKind : byte
    {
        /// <summary>The layout every cell holds from now on.</summary>
        Broadcast = 0,
        /// <summary>A step sent first to the one cell that must apply it before the others see it.</summary>
        Staged = 1,
        /// <summary>The answer to one cell's hello: the layout to hold, which opens its gate.</summary>
        HelloAnswer = 2,
    }

    /// <summary>MeshLayoutMessage carries a layout snapshot from the central to a map server.</summary>
    public sealed class MeshLayoutMessage : INetSerializable
    {
        public MeshLayoutKind kind;
        /// <summary>For a staged step or a hello answer: the cell it is meant for (empty for a broadcast).</summary>
        public string targetCellId;
        /// <summary>...and that cell's incarnation, so a restarted cell ignores an answer meant for its predecessor.</summary>
        public string targetIncarnation;
        /// <summary>The operation step this layout belongs to (empty until splits and merges exist).</summary>
        public string stepId;
        public MeshLayoutSnapshot snapshot = new MeshLayoutSnapshot();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)kind);
            writer.Put(targetCellId ?? string.Empty);
            writer.Put(targetIncarnation ?? string.Empty);
            writer.Put(stepId ?? string.Empty);
            (snapshot ?? new MeshLayoutSnapshot()).Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            kind = (MeshLayoutKind)MeshLayoutWire.GetEnum(reader, (byte)MeshLayoutKind.HelloAnswer, "layout kind");
            targetCellId = reader.GetString();
            targetIncarnation = reader.GetString();
            stepId = reader.GetString();
            snapshot = new MeshLayoutSnapshot();
            snapshot.Deserialize(reader);
        }
    }

    /// <summary>MeshLayoutHelloMessage is a map server's hello once it is registered: who it is and the layout it holds.</summary>
    public sealed class MeshLayoutHelloMessage : INetSerializable
    {
        public string cellId;
        public string mapId;
        public string incarnation;
        /// <summary>Whether the cell runs its map Dynamic (a mismatch with the central is logged on both sides).</summary>
        public bool dynamicMap;
        public MeshLayoutSnapshot held = new MeshLayoutSnapshot();

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(cellId ?? string.Empty);
            writer.Put(mapId ?? string.Empty);
            writer.Put(incarnation ?? string.Empty);
            writer.Put(dynamicMap);
            (held ?? new MeshLayoutSnapshot()).Serialize(writer);
        }

        public void Deserialize(NetDataReader reader)
        {
            cellId = reader.GetString();
            mapId = reader.GetString();
            incarnation = reader.GetString();
            dynamicMap = reader.GetBool();
            held = new MeshLayoutSnapshot();
            held.Deserialize(reader);
        }
    }

    /// <summary>MeshLayoutAckResult is what a map server did with a layout.</summary>
    public enum MeshLayoutAckResult : byte
    {
        Applied = 0,
        Rejected = 1,
    }

    /// <summary>MeshLayoutAckMessage is a map server's answer to a layout.</summary>
    public struct MeshLayoutAckMessage : INetSerializable
    {
        public string cellId;
        public string incarnation;
        public MeshLayoutPair pair;
        public MeshLayoutAckResult result;
        public string reason;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(cellId ?? string.Empty);
            writer.Put(incarnation ?? string.Empty);
            MeshLayoutWire.Put(writer, pair);
            writer.Put((byte)result);
            writer.Put(reason ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            cellId = reader.GetString();
            incarnation = reader.GetString();
            pair = MeshLayoutWire.GetPair(reader);
            result = (MeshLayoutAckResult)MeshLayoutWire.GetEnum(reader, (byte)MeshLayoutAckResult.Rejected, "layout ack result");
            reason = reader.GetString();
        }
    }

    /// <summary>MeshRetireMessage tells a map server its cell is gone from the layout.</summary>
    public struct MeshRetireMessage : INetSerializable
    {
        public string cellId;
        public string incarnation;
        public MeshLayoutPair pair;
        public string reason;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(cellId ?? string.Empty);
            writer.Put(incarnation ?? string.Empty);
            MeshLayoutWire.Put(writer, pair);
            writer.Put(reason ?? string.Empty);
        }

        public void Deserialize(NetDataReader reader)
        {
            cellId = reader.GetString();
            incarnation = reader.GetString();
            pair = MeshLayoutWire.GetPair(reader);
            reason = reader.GetString();
        }
    }

    /// <summary>MeshNoteLevel is how a map server logs a coordinator note.</summary>
    public enum MeshNoteLevel : byte
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// MeshCoordinatorNoteMessage is a decision, refusal or recovery of the central's coordinator, sent to the cells it
    /// concerns so it lands in their logs (the central in the editor has no file log of its own).
    /// </summary>
    public struct MeshCoordinatorNoteMessage : INetSerializable
    {
        public MeshNoteLevel level;
        public string subjectCellId;
        /// <summary>A stable slug for tools: recovery-adopted, recovery-frozen, store-unwritable, mode-mismatch, hello-refused.</summary>
        public string code;
        public string text;
        public MeshLayoutPair pair;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)level);
            writer.Put(subjectCellId ?? string.Empty);
            writer.Put(code ?? string.Empty);
            writer.Put(text ?? string.Empty);
            MeshLayoutWire.Put(writer, pair);
        }

        public void Deserialize(NetDataReader reader)
        {
            level = (MeshNoteLevel)MeshLayoutWire.GetEnum(reader, (byte)MeshNoteLevel.Error, "note level");
            subjectCellId = reader.GetString();
            code = reader.GetString();
            text = reader.GetString();
            pair = MeshLayoutWire.GetPair(reader);
        }
    }

    /// <summary>MeshWholeMessage reads a cluster message that must end where its packet does (another version's is dropped).</summary>
    public static class MeshWholeMessage
    {
        /// <summary>
        /// TryRead deserializes into <paramref name="message"/> itself, so a struct message keeps what was read. On false,
        /// <paramref name="error"/> says why and the message must not be used.
        /// </summary>
        public static bool TryRead<T>(NetDataReader reader, out T message, out string error) where T : INetSerializable, new()
        {
            message = new T();
            try
            {
                message.Deserialize(reader);
            }
            catch (System.Exception ex)
            {
                error = $"unreadable: {ex.Message}";
                return false;
            }
            if (reader.AvailableBytes != 0)
            {
                error = $"{reader.AvailableBytes} unread bytes";
                return false;
            }
            error = null;
            return true;
        }
    }
}
