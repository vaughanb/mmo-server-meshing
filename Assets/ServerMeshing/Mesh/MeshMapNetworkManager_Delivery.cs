// Server Meshing add-on — MeshMapNetworkManager: what an exchange across a line owes a player this cell no longer holds
// goes by mail (DESIGN.md 1.51).
//
// A trade's or a sale's answer can come after its player has left the cell that waits for it: a logout (the kit's safe
// disconnect saves and destroys the character at once), a disconnect, or a hop once the exchange was released after a
// minute without an answer. The result is then the kit's mail to the player's account (`ServerMailHandlers.SendMail`,
// claimed on any character): the other trader's offer or the player's own offer back, a vendor's gold or its item back.
// Mail is the kit's own store for things owed to an absent player; it is written by the database server, so nothing
// races the character's own saves.
//
// DESIGN 1.54: a mail is in the exchanges journal, with a reference written into its text, until the database accepts
// it; one it refuses is tried again every 30 s for 30 minutes, and a restarted cell sends one the journal still holds
// only if the reference is not in the receiver's mailbox (MeshMapNetworkManager_ExchangeSteps).
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
            string reference = System.Guid.NewGuid().ToString("N");
            MeshExchangeJournal.Mail record = new MeshExchangeJournal.Mail
            {
                reference = reference, userId = userId, characterName = characterName, title = title,
                content = $"{content} (reference {reference})", what = what, gold = gold,
                sentAt = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (items != null)
            {
                foreach (CharacterItem item in items)
                    if (!item.IsEmptySlot())
                        record.items.Add(item.Clone(false));
            }
            _mailsInFlight[reference] = record;
            int n = ++_stepsCommitted;
            JournalChanged();
            CrashInStep(n, "before", what);
            SendMeshMail(record, n).Forget();
        }

        /// <summary>Sends a journaled mail; accepted, it leaves the journal; refused, it is tried again every 30 s for 30 minutes.</summary>
        private async UniTaskVoid SendMeshMail(MeshExchangeJournal.Mail record, int step = 0)
        {
            float since = UnityEngine.Time.unscaledTime;
            while (true)
            {
                Mail mail = new Mail
                {
                    SenderId = string.Empty,
                    SenderName = MeshMailSender,
                    ReceiverId = record.userId,
                    Title = record.title,
                    Content = record.content,
                    Gold = record.gold,
                };
                foreach (CharacterItem item in record.items)
                    mail.Items.Add(item.Clone(false));
                bool sent = GameInstance.ServerMailHandlers != null && await GameInstance.ServerMailHandlers.SendMail(mail);
                if (sent)
                {
                    Logging.Log("Mesh", $"{record.what}: mailed to {record.characterName} (account {record.userId}): {mail.Items.Count} item(s), {mail.Gold} gold, '{mail.Title}'");
                    if (step > 0)
                        CrashInStep(step, "after", record.what);
                    if (_mailsInFlight.Remove(record.reference))
                        JournalChanged();
                    return;
                }
                if (UnityEngine.Time.unscaledTime - since >= MeshMailRetrySeconds)
                {
                    if (_mailsInFlight.Remove(record.reference))
                        JournalChanged();
                    Logging.LogError("Mesh", $"{record.what}: the mail to {record.characterName} (account {record.userId}) was refused for {MeshMailRetrySeconds:0} s; {mail.Items.Count} item(s) and {mail.Gold} gold are lost");
                    return;
                }
                Logging.LogWarning("Mesh", $"{record.what}: the mail to {record.characterName} (account {record.userId}) was refused; tried again in 30 s (it stays in the journal)");
                await UniTask.Delay(30000, ignoreTimeScale: true);
            }
        }
#endif
    }
}
