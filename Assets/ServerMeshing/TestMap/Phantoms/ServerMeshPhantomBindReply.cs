// Server Meshing add-on — rig phantom bind reply: what the binder tells a sink peer about the phantom its hello bound.
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>
    /// ServerMeshPhantomBindReply is the binder's answer to a sink hello: [packed ushort 65001][packed uint object id][float
    /// x][float z][byte kind 0 character 1 eye 2 ship][byte payload][byte hz][byte stand 0|1][float speed][float centre
    /// x][float centre z][float half side], and for kind 2 only [float centre y][int32 seed][int32 flight path][float
    /// scale]. For characters (x, z) is the spawn point, payload 0 transform 1 input, speed the move speed, and the centre
    /// and half side the square it walks in. For ships payload is the upload wire (0 legacy54, 1 header56), speed the
    /// path's top speed, (x, z) and the centre the path centre, and seed, flight path and scale rebuild the path.
    /// </summary>
    public struct ServerMeshPhantomBindReply
    {
        /// <summary>objectId is the bound object's network id.</summary>
        public uint objectId;
        /// <summary>x is the spawn point's x (scene frame).</summary>
        public float x;
        /// <summary>z is the spawn point's z (scene frame).</summary>
        public float z;
        /// <summary>kind is 0 for a character, 1 for an eye, 2 for a ship.</summary>
        public byte kind;
        /// <summary>payload is 0 for transform uploads, 1 for input uploads; for a ship 0 legacy54, 1 header56.</summary>
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
        /// <summary>centreY is a ship path's centre height (kind 2 only).</summary>
        public float centreY;
        /// <summary>seed is a ship path's seed (kind 2 only).</summary>
        public int seed;
        /// <summary>flightPath is a ship path's index (kind 2 only).</summary>
        public int flightPath;
        /// <summary>scale is a ship's scale factor (kind 2 only).</summary>
        public float scale;

        /// <summary>FixedBytes is the reply's length after its packed type and object id: two floats, four bytes, four floats.</summary>
        public const int FixedBytes = 4 + 4 + 4 + 4 + 4 + 4 + 4;
        /// <summary>ShipKind is the kind byte of a ship phantom.</summary>
        public const byte ShipKind = (byte)ServerMeshPhantomKind.Ship;
        /// <summary>ShipBytes is what a ship reply adds after the fixed part: centre y, seed, flight path, scale.</summary>
        public const int ShipBytes = 4 + 4 + 4 + 4;

        /// <summary>Region is the walk square.</summary>
        public Rect Region => new Rect(centreX - halfSide, centreZ - halfSide, 2f * halfSide, 2f * halfSide);

        /// <summary>
        /// IsFollowableShip is true for a ship reply a sink can rebuild a path from: kind 2, a phantom path index (at least
        /// 0), a finite scale from 0.001 to 10 and a finite centre.
        /// </summary>
        public bool IsFollowableShip => kind == ShipKind && flightPath >= 0 && Finite(scale) && scale >= 0.001f && scale <= 10f
            && Finite(centreX) && Finite(centreY) && Finite(centreZ);

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
            if (kind != ShipKind)
                return;
            writer.Put(centreY);
            writer.Put(seed);
            writer.Put(flightPath);
            writer.Put(scale);
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
            if (reader.AvailableBytes < idBytes + FixedBytes || (reader.RawData[reader.Position + idBytes + 8] == ShipKind && reader.AvailableBytes < idBytes + FixedBytes + ShipBytes))
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
            if (reply.kind != ShipKind)
                return true;
            reply.centreY = reader.GetFloat();
            reply.seed = reader.GetInt();
            reply.flightPath = reader.GetInt();
            reply.scale = reader.GetFloat();
            return true;
        }
    }
}
