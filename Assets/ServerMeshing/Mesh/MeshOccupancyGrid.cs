// Server Meshing add-on — where a cell's load stands (DESIGN.md 0.71): counts per world-aligned bucket, sent to the
// central in each load report so it can find a cut that separates the crowd, and predict what each side would hold.
using System;
using System.IO;
using LiteNetLib.Utils;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>MeshOccupancyLayer names what a grid counts.</summary>
    public enum MeshOccupancyLayer : byte
    {
        OwnedPlayers = 0,
        OwnedMonsters = 1,
        Puppets = 2,
        Clients = 3,
    }

    /// <summary>
    /// MeshOccupancyGrid counts entities per square bucket of a world rectangle, one count array per layer. Buckets are
    /// aligned to multiples of the bucket size in world coordinates, so the grids of two cells line up.
    /// </summary>
    public sealed class MeshOccupancyGrid : INetSerializable
    {
        public const int LayerCount = 4;
        /// <summary>Buckets per side at most: past this the bucket grows (a huge map with a tiny band would otherwise send megabytes).</summary>
        public const int MaxSide = 256;

        public float bucketSize;
        /// <summary>World coordinates of the grid's minimum corner (a multiple of bucketSize).</summary>
        public float originX, originZ;
        public int columns, rows;
        public ushort[][] layers = new ushort[LayerCount][];

        /// <summary>
        /// Covering returns an empty grid whose buckets cover the world rectangle [minX,maxX) x [minZ,maxZ). The bucket is
        /// the requested size, or larger when that size would need more than <see cref="MaxSide"/> buckets on a side.
        /// </summary>
        public static MeshOccupancyGrid Covering(float minX, float minZ, float maxX, float maxZ, float bucketSize)
        {
            if (bucketSize <= 0f)
                throw new ArgumentOutOfRangeException(nameof(bucketSize), bucketSize, "bucket size must be positive");
            if (maxX < minX || maxZ < minZ)
                throw new ArgumentException($"empty rectangle x [{minX}, {maxX}) z [{minZ}, {maxZ})");
            float extent = Math.Max(maxX - minX, maxZ - minZ);
            if (extent / bucketSize > MaxSide - 2)
                bucketSize = (float)Math.Ceiling(extent / (MaxSide - 2));
            MeshOccupancyGrid grid = new MeshOccupancyGrid { bucketSize = bucketSize };
            grid.originX = (float)Math.Floor(minX / bucketSize) * bucketSize;
            grid.originZ = (float)Math.Floor(minZ / bucketSize) * bucketSize;
            grid.columns = Math.Max(1, (int)Math.Ceiling((maxX - grid.originX) / bucketSize));
            grid.rows = Math.Max(1, (int)Math.Ceiling((maxZ - grid.originZ) / bucketSize));
            for (int i = 0; i < LayerCount; ++i)
                grid.layers[i] = new ushort[grid.columns * grid.rows];
            return grid;
        }

        /// <summary>Add counts one entity at a world position; a position outside the grid is ignored and reported false.</summary>
        public bool Add(MeshOccupancyLayer layer, float worldX, float worldZ)
        {
            int col = (int)Math.Floor((worldX - originX) / bucketSize);
            int row = (int)Math.Floor((worldZ - originZ) / bucketSize);
            if (col < 0 || row < 0 || col >= columns || row >= rows)
                return false;
            ushort[] counts = layers[(int)layer];
            int index = row * columns + col;
            if (counts[index] < ushort.MaxValue)
                counts[index]++;
            return true;
        }

        /// <summary>Count returns a bucket's count.</summary>
        public int Count(MeshOccupancyLayer layer, int col, int row) => layers[(int)layer][row * columns + col];

        /// <summary>Total returns the sum of a layer's buckets whose world rectangle lies inside [minX,maxX) x [minZ,maxZ).</summary>
        public int Total(MeshOccupancyLayer layer, float minX, float minZ, float maxX, float maxZ)
        {
            ushort[] counts = layers[(int)layer];
            int total = 0;
            for (int row = 0; row < rows; ++row)
            {
                float z = originZ + (row + 0.5f) * bucketSize;
                if (z < minZ || z >= maxZ)
                    continue;
                for (int col = 0; col < columns; ++col)
                {
                    float x = originX + (col + 0.5f) * bucketSize;
                    if (x >= minX && x < maxX)
                        total += counts[row * columns + col];
                }
            }
            return total;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put(bucketSize);
            writer.Put(originX);
            writer.Put(originZ);
            writer.Put((ushort)columns);
            writer.Put((ushort)rows);
            for (int i = 0; i < LayerCount; ++i)
            {
                ushort[] counts = layers[i];
                for (int j = 0; j < counts.Length; ++j)
                    writer.Put(counts[j]);
            }
        }

        public void Deserialize(NetDataReader reader)
        {
            bucketSize = reader.GetFloat();
            originX = reader.GetFloat();
            originZ = reader.GetFloat();
            columns = reader.GetUShort();
            rows = reader.GetUShort();
            if (bucketSize <= 0f || columns == 0 || rows == 0 || columns > MaxSide || rows > MaxSide)
                throw new InvalidDataException($"occupancy grid header out of range: bucket {bucketSize}, {columns} x {rows}");
            if ((long)columns * rows * LayerCount * sizeof(ushort) > reader.AvailableBytes)
                throw new InvalidDataException($"occupancy grid of {columns} x {rows} is longer than the {reader.AvailableBytes} bytes left");
            for (int i = 0; i < LayerCount; ++i)
            {
                ushort[] counts = new ushort[columns * rows];
                for (int j = 0; j < counts.Length; ++j)
                    counts[j] = reader.GetUShort();
                layers[i] = counts;
            }
        }
    }
}
