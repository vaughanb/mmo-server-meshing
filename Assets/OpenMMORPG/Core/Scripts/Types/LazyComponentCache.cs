using UnityEngine;

namespace MultiplayerARPG
{
    public sealed class LazyComponentCache<T>
        where T : Component
    {
        private bool _isCached = false;
        private T _value =  null;
        private GameObject _prevTarget = null;

        public T Get(GameObject target)
        {
            if (_prevTarget != target)
            {
                _prevTarget = target;
                _isCached = false;
            }
            if (!_isCached)
            {
                _isCached = true;
                if (target != null)
                    _value = target.GetComponentInChildren<T>();
            }
            return _value;
        }

        public void Clear()
        {
            _isCached = false;
            _value = null;
            _prevTarget = null;
        }
    }
}