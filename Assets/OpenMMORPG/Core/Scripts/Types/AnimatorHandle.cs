using UnityEngine;

namespace MultiplayerARPG
{
    public class AnimatorHandle : MonoBehaviour
    {
        private uint _id = 0;
        public uint Id
        {
            get
            {
                AssignId();
                return _id;
            }
        }
        private static uint _nextId = 1;
        public System.Action<AnimatorHandle> OnDestroyed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void ResetInstance()
        {
            _nextId = 1;
        }

        void OnDestroy()
        {
            OnDestroyed?.Invoke(this);
            OnDestroyed = null;
        }

        public void AssignId()
        {
            if (_id == 0)
                _id = _nextId++;
        }
    }
}