// Server Meshing test rig — the login server's character data manager for the demo copy: the kit's own, except that a
// new character starts (and respawns) on one map, whatever its class's start map says. The login server creates
// characters and takes the first ILoginServerDataManager among its children, so ServerMeshDemo8kBuilder puts this
// under DemoMesh8kInit's (server) login manager, and the demo's class assets stay as they are.
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG.MMO.Mesh
{
    /// <summary>DemoMesh8kStartMap starts every new character on startMap at the map's start position.</summary>
    public sealed class DemoMesh8kStartMap : MonoBehaviour
#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        , ILoginServerDataManager
#endif
    {
        public BaseMapInfo startMap;

#if NET || NETCOREAPP || ((UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE)
        private readonly DefaultLoginServerDataManager _kit = new DefaultLoginServerDataManager();

        public string GenerateCharacterId() => _kit.GenerateCharacterId();
        public string GenerateAccessToken(string userId) => _kit.GenerateAccessToken(userId);
        public string GetUserIdFromAccessToken(string accessToken) => _kit.GetUserIdFromAccessToken(accessToken);

        public bool CanCreateCharacter(ref int dataId, ref int entityId, ref int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats, out UITextKeys errorMessage)
        {
            return _kit.CanCreateCharacter(ref dataId, ref entityId, ref factionId, publicBools, publicInts, publicFloats, out errorMessage);
        }

        public void SetNewPlayerCharacterData(PlayerCharacterData playerCharacterData, string characterName, int dataId, int entityId, int factionId, IList<CharacterDataBoolean> publicBools, IList<CharacterDataInt32> publicInts, IList<CharacterDataFloat32> publicFloats)
        {
            _kit.SetNewPlayerCharacterData(playerCharacterData, characterName, dataId, entityId, factionId, publicBools, publicInts, publicFloats);
            if (startMap == null)
                return;
            playerCharacterData.CurrentMapName = startMap.Id;
            playerCharacterData.CurrentPosition = startMap.StartPosition;
            playerCharacterData.CurrentRotation = startMap.StartRotation;
#if !DISABLE_DIFFER_MAP_RESPAWNING
            playerCharacterData.RespawnMapName = startMap.Id;
            playerCharacterData.RespawnPosition = startMap.StartPosition;
#endif
        }
#endif
    }
}
