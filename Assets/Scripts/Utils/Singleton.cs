using UnityEngine;

namespace Singleton
{
    /// <summary>
    /// Generic singleton base class to ensure a single instance of a MonoBehaviour-derived class.
    /// The syntax for creating a new singleton is <c>ClassName : SingletonBase&lt;ClassName&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Ported from FactoryTM. Unused <c>using</c> directives for FactoryTM-only namespaces
    /// (Managers, Placeables, Ui, Weapons, Nodes, EventTypes) were stripped — they do not
    /// exist in this project and would not compile.
    /// </remarks>
    public class SingletonBase<T> : MonoBehaviour where T : MonoBehaviour
    {
        private static T _instance;
        private static bool _isQuitting;

        public static bool HasInstance => _instance != null;

        public static T Instance
        {
            get
            {
                if (_isQuitting)
                {
                    // Hand back the instance if it still exists so late teardown can
                    // unsubscribe safely, but never resurrect a destroyed one.
                    return _instance;
                }

                if (_instance == null)
                {
                    _instance = FindFirstObjectByType<T>();

                    if (_instance == null)
                    {
                        var singletonObject = new GameObject(typeof(T).Name);
                        _instance = singletonObject.AddComponent<T>();
                    }
                }

                return _instance;
            }
        }

        [SerializeField] protected bool persistBetweenScenes = false;
        [SerializeField] protected bool detachFromParent = true;

        protected virtual void Awake()
        {
            // Statics survive play sessions when Enter Play Mode has domain reload
            // disabled, which would leave _isQuitting stuck true from the last run
            // and make Instance permanently return null.
            _isQuitting = false;

            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this as T;

            // DontDestroyOnLoad only works on root objects, so detach first.
            if (detachFromParent && transform.parent != null)
            {
                transform.SetParent(null);
            }

            if (persistBetweenScenes)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        protected virtual void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }
        }

        protected virtual void OnApplicationQuit()
        {
            _isQuitting = true;
        }
    }
}
