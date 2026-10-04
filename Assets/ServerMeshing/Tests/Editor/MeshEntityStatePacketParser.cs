using System;
using System.Collections.Generic;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh.Tests
{
    /// <summary>
    /// MeshEntityStatePacketParser is a copy of the kit client's EntityState loop (HandleServerEntityStateAtClient), with
    /// its length guard: a record length below zero or past the packet stops the parse. It returns each record's bytes,
    /// or hands each record to the kit's own ReadServerEntityState.
    /// </summary>
    internal static class MeshEntityStatePacketParser
    {
        public static bool TryParse(byte[] data, int length, out ushort type, out long timestamp, out int count, List<(uint id, int length, byte[] bytes)> records, out int end)
        {
            NetDataReader reader = new NetDataReader(data, 0, length);
            type = reader.GetPackedUShort();
            timestamp = reader.GetPackedLong();
            count = reader.GetInt();
            for (int i = 0; i < count; ++i)
            {
                uint objectId = reader.GetPackedUInt();
                int dataLength = reader.GetInt();
                if (dataLength < 0 || dataLength > reader.AvailableBytes)
                {
                    end = reader.Position;
                    return false;
                }
                byte[] bytes = new byte[dataLength];
                reader.GetBytes(bytes, dataLength);
                records.Add((objectId, dataLength, bytes));
            }
            end = reader.Position;
            return true;
        }

        public static List<(uint id, int length, byte[] bytes)> Parse(byte[] data, out long timestamp)
        {
            List<(uint, int, byte[])> records = new List<(uint, int, byte[])>();
            if (!TryParse(data, data.Length, out ushort type, out timestamp, out int count, records, out int end) || type != 119 || end != data.Length || count != records.Count)
                throw new InvalidOperationException($"not one whole EntityState packet: type {type}, {records.Count} of {count} records, ended at {end} of {data.Length}");
            return records;
        }

        public static List<(uint id, int length, byte[] bytes)> Parse(byte[] data) => Parse(data, out _);

        public static bool TryRead(byte[] data, int length, IDictionary<uint, IEntityMovementDataHandler> handlers, List<uint> unknown)
        {
            NetDataReader reader = new NetDataReader(data, 0, length);
            reader.GetPackedUShort();
            long peerTimestamp = reader.GetPackedLong();
            int stateCount = reader.GetInt();
            for (int i = 0; i < stateCount; ++i)
            {
                uint objectId = reader.GetPackedUInt();
                int dataLength = reader.GetInt();
                int positionBeforeRead = reader.Position;
                if (dataLength < 0 || dataLength > reader.AvailableBytes)
                    return false;
                if (!handlers.TryGetValue(objectId, out IEntityMovementDataHandler dataHandler))
                {
                    unknown.Add(objectId);
                    reader.SetPosition(positionBeforeRead);
                    reader.SkipBytes(dataLength);
                    continue;
                }
                BaseGameNetworkManager.ReadServerEntityState(dataHandler, peerTimestamp, reader, dataLength);
            }
            return true;
        }
    }
}
