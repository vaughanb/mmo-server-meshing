using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// The door between the island and the crypt: the kit's warp portal, sent through
    /// once per character.
    ///
    /// A character carries more than one collider tagged as the player - its capsule
    /// and its hit boxes - and each of them entering the trigger is an enter event, so
    /// one step through the door asks for the warp twice in the same frame. The kit's
    /// own guard is the character's warping flag, which the server only raises once the
    /// warp is under way, after the first request's save has begun; the second request
    /// arrives before that and starts a second scene change on top of the first. In
    /// the editor's LAN host that deadlocked the editor. So the gate remembers who it
    /// has just sent and ignores them until the map has moved on, which it does within
    /// a few seconds or not at all.
    /// </summary>
    public class DeferredWarpPortalEntity : WarpPortalEntity
    {
        [Tooltip("How long, in seconds, a character who has just been sent through is ignored by this gate.")]
        public float cooldown = 5f;

        private readonly Dictionary<uint, float> _sent = new Dictionary<uint, float>();

        public override void EnterWarp(BasePlayerCharacterEntity playerCharacterEntity)
        {
            if (playerCharacterEntity == null)
                return;
            float now = Time.unscaledTime;
            float sentAt;
            if (_sent.TryGetValue(playerCharacterEntity.ObjectId, out sentAt) && now - sentAt < cooldown)
                return;
            _sent[playerCharacterEntity.ObjectId] = now;
            // Not from inside the physics step. The trigger fires during FixedUpdate,
            // and a scene load begun there has its first frame's loading time cut to
            // almost nothing, so the new map is still held short of activation when the
            // frame ends - and the editor's LAN host deadlocked on exactly that. Begun
            // from Update, the same load has a whole frame's head start.
            StartCoroutine(WarpNextFrame(playerCharacterEntity));
        }

        private IEnumerator WarpNextFrame(BasePlayerCharacterEntity playerCharacterEntity)
        {
            yield return null;
            if (playerCharacterEntity != null)
                base.EnterWarp(playerCharacterEntity);
        }
    }
}
