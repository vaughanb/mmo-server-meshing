// Server Meshing add-on — rig phantom bind reply: what the binder tells a sink peer about the phantom its hello bound.
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomBindReply is the binder's answer to a sink hello: [packed ushort 65001][packed uint object id][float
    /// x][float z][byte kind 0 character 1 eye][byte payload 0 transform 1 input][byte hz][byte stand 0|1][float speed][float
    /// centre x][float centre z][float half side]. (x, z) is the spawn point, speed the character's move speed, and the
    /// centre and half side the square it walks in.
    /// </summary>
    public struct ServerMeshPhantomBindReply
    {
        /// <summary>objectId is the bound object's network id.</summary>
        public uint objectId;
        /// <summary>x is the spawn point's x (scene frame).</summary>
        public float x;
        /// <summary>z is the spawn point's z (scene frame).</summary>
        public float z;
        /// <summary>kind is 0 for a character, 1 for an eye.</summary>
        public byte kind;
        /// <summary>payload is 0 for transform uploads, 1 for input uploads.</summary>
        public byte payload;
        /// <summary>hz is the upload rate.</summary>
        public byte hz;
        /// <summary>stand is 1 when the phantom stands.</summary>
        public byte stand;
        /// <summary>speed is the character's move speed in m/s (0 for an eye).</summary>
        public float speed;
        /// <summary>centreX is the walk square's centre x.</summary>
        public float centreX;
        /// <summary>centreZ is the walk square's centre z.</summary>
        public float centreZ;
        /// <summary>halfSide is half the walk square's side.</summary>
        public float halfSide;

        /// <summary>FixedBytes is the reply's length after its packed type and object id: two floats, four bytes, four floats.</summary>
        public const int FixedBytes = 4 + 4 + 4 + 4 + 4 + 4 + 4;

        /// <summary>Region is the walk square.</summary>
        public Rect Region => new Rect(centreX - halfSide, centreZ - halfSide, 2f * halfSide, 2f * halfSide);

        /// <summary>Write resets writer and writes the reply, type first.</summary>
        public void Write(NetDataWriter writer)
        {
            TransportHandler.WritePacket(writer, ServerMeshPhantomTransport.BindReplyMessageType);
            writer.PutPackedUInt(objectId);
            writer.Put(x);
            writer.Put(z);
            writer.Put(kind);
            writer.Put(payload);
            writer.Put(hz);
            writer.Put(stand);
            writer.Put(speed);
            writer.Put(centreX);
            writer.Put(centreZ);
            writer.Put(halfSide);
        }

        /// <summary>TryRead reads a reply from reader's position, type included; false (reader position kept) when it is not a whole reply.</summary>
        public static bool TryRead(NetDataReader reader, out ServerMeshPhantomBindReply reply)
        {
            reply = default;
            if (reader == null || reader.RawData == null)
                return false;
            int start = reader.Position;
            if (!MeshSendMeter.TryReadMessageType(reader.RawData, start, reader.RawDataSize, out ushort type, out int typeBytes) || type != ServerMeshPhantomTransport.BindReplyMessageType)
                return false;
            reader.SetPosition(start + typeBytes);
            if (reader.AvailableBytes < 1)
            {
                reader.SetPosition(start);
                return false;
            }
            byte lead = reader.RawData[reader.Position];
            int idBytes = lead <= 240 ? 1 : lead <= 248 ? 2 : lead == 249 ? 3 : lead == 250 ? 4 : 5;
            if (reader.AvailableBytes < idBytes + FixedBytes)
            {
                reader.SetPosition(start);
                return false;
            }
            reply.objectId = reader.GetPackedUInt();
            reply.x = reader.GetFloat();
            reply.z = reader.GetFloat();
            reply.kind = reader.GetByte();
            reply.payload = reader.GetByte();
            reply.hz = reader.GetByte();
            reply.stand = reader.GetByte();
            reply.speed = reader.GetFloat();
            reply.centreX = reader.GetFloat();
            reply.centreZ = reader.GetFloat();
            reply.halfSide = reader.GetFloat();
            return true;
        }
    }
}
