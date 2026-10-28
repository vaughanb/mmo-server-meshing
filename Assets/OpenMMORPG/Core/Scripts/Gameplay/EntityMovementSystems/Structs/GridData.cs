namespace MultiplayerARPG
{
    public struct GridData
    {
        public ushort cellSize;
        public CompressionRange range;

        public GridData(ushort cellSize, CompressionRange range) : this()
        {
            this.cellSize = cellSize;
            this.range = range;
        }
    }
}
