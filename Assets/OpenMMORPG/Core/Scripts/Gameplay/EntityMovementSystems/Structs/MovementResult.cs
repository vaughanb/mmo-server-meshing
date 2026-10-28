namespace MultiplayerARPG
{
    public struct MovementResult
    {
        public uint objectId;
        public bool reliably;
        public uint movementState;
        public byte extraMovementState;
        public EncodedPosition position;
        public byte compressedYAngle;
    }
}
