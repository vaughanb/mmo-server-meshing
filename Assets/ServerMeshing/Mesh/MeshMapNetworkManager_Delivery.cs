// Server Meshing add-on — MeshMapNetworkManager: what an exchange across a line owes a player this cell no longer holds
// goes by mail (DESIGN.md 1.51).
//
// A trade's or a sale's answer can come after its player has left the cell that waits for it: a logout (the kit's safe
// disconnect saves and destroys the character at once), a disconnect, or a hop once the exchange was released after a
// minute without an answer. The result is then the kit's mail to the player's account (`ServerMailHandlers.SendMail`,
// claimed on any character): the other trader's offer or the player's own offer back, a vendor's gold or its item back.
// Mail is the kit's own store for things owed to an absent player; it is written by the database server, so nothing
// races the character's own saves.
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
#if (UNITY_EDITOR || UNITY_SERVER || !EXCLUDE_SERVER_CODES) && UNITY_STANDALONE
        /// <summary>The sender name on the add-on's mail.</summary>
        public const string MeshMailSender = "Server Meshing";

        /// <summary>The player registered on this cell by character id (the kit's own registry), or null.</summary>
        private BasePlayerCharacterEntity RegisteredPlayer(string characterId)
        {
            if (string.IsNullOrEmpty(characterId))
                return null;
            return ServerUserHandlers.TryGetPlayerCharacterById(characterId, out IPlayerCharacterData data) ? data as BasePlayerCharacterEntity : null;
        }

        /// <summary>Mails items and gold to the account of a character this cell no longer holds; logged either way.</summary>
        private void MailToAbsent(string userId, string characterName, string title, string content, IList<CharacterItem> items, int gold, string what)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Logging.LogError("Mesh", $"{what}: {characterName} is gone and its account is unknown; {items?.Count ?? 0} item(s) and {gold} gold are lost");
                return;
            }
            Mail mail = new Mail
            {
                SenderId = string.Empty,
                SenderName = MeshMailSender,
                ReceiverId = userId,
                Title = title,
                Content = content,
                Gold = gold,
            };
            if (items != null)
            {
                foreach (CharacterItem item in items)
                    if (!item.IsEmptySlot())
                        mail.Items.Add(item.Clone(false));
            }
            SendMeshMail(mail, characterName, what).Forget();
        }

        private async UniTaskVoid SendMeshMail(Mail mail, string characterName, string what)
        {
            bool sent = GameInstance.ServerMailHandlers != null && await GameInstance.ServerMailHandlers.SendMail(mail);
            if (sent)
                Logging.Log("Mesh", $"{what}: mailed to {characterName} (account {mail.ReceiverId}): {mail.Items.Count} item(s), {mail.Gold} gold, '{mail.Title}'");
            else
                Logging.LogError("Mesh", $"{what}: the mail to {characterName} (account {mail.ReceiverId}) was refused; {mail.Items.Count} item(s) and {mail.Gold} gold are lost");
        }
#endif
    }
}
