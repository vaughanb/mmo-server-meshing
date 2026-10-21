// Server Meshing add-on — client side of enter-game when the map server never answered (DESIGN.md 0.87).
//
// The kit answers a request that timed out (or could not be written) with a ResponseHandlerData whose Reader is null,
// and BaseGameNetworkManager.ReadExtraEnterGameResponse reads a UITextKeys from that reader unconditionally: the
// client throws a NullReferenceException, never runs the kit's refusal path (dialog, StopClient), and sits connected to
// a map server with no character until something else gives up. Here a login's unanswered enter-game is handed to the
// kit as the refusal it is (UI_ERROR_REQUEST_TIMEOUT), so the kit stops the client and its listeners hear the answer; a
// migration hop's is left to ClientMigrationController (it already reads any non-success as MigrationRefused). The
// count and code of the last answer let a caller tell this start's answer from an earlier one (the rig's auto client
// selects again on a refusal).
using LiteNetLib.Utils;
using LiteNetLibManager;

namespace MultiplayerARPG.MMO.Mesh
{
    public partial class MeshMapNetworkManager
    {
        /// <summary>EnterGameAnswers counts the enter-game answers this client has handled, timeouts included.</summary>
        public int EnterGameAnswers { get; private set; }
        /// <summary>LastEnterGameAnswer is the code of the latest of them.</summary>
        public AckResponseCode LastEnterGameAnswer { get; private set; }

        /// <summary>
        /// HandleEnterGameResponse's hook: records the answer and, when the map server never sent one, gives the kit a
        /// reader carrying the refusal it reads. True (skip the kit) only for a migration hop's unanswered request.
        /// </summary>
        private bool EnterGameUnanswered(ref ResponseHandlerData responseHandler, AckResponseCode responseCode)
        {
            EnterGameAnswers++;
            LastEnterGameAnswer = responseCode;
            if (responseHandler != null && responseHandler.Reader != null)
                return false;
            if (IsMigrating && !IsServer)
                return true;    // the controller falls back (MigrationRefused); the kit's handler would only throw
            UITextKeys message = UnansweredEnterGameMessage(responseCode);
            Logging.LogWarning("Mesh", $"Enter-game {responseCode} without an answer from the map server; refused as {message}");
            NetDataWriter writer = new NetDataWriter();
            writer.PutPackedUShort((ushort)message);
            responseHandler = new ResponseHandlerData(responseHandler?.RequestId ?? 0, responseHandler?.TransportHandler ?? Client, responseHandler?.ConnectionId ?? -1, new NetDataReader(writer));
            return false;
        }

        /// <summary>UnansweredEnterGameMessage is the refusal an unanswered enter-game is read as: a timeout, or a request that never left.</summary>
        public static UITextKeys UnansweredEnterGameMessage(AckResponseCode responseCode)
        {
            return responseCode == AckResponseCode.Timeout ? UITextKeys.UI_ERROR_REQUEST_TIMEOUT : UITextKeys.UI_ERROR_SERVICE_NOT_AVAILABLE;
        }
    }
}
