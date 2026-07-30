using System;
using System.Collections.Generic;
using Singleton;
using UnityEngine;

namespace EventSystems
{
    /// <summary>
    /// Central publish/subscribe hub. Subscribers are held weakly, so a destroyed
    /// listener is dropped on the next publish even if it forgot to unsubscribe.
    /// </summary>
    /// <remarks>
    /// Ported from FactoryTM with four changes, all required by this project's
    /// multi-scene structure (Bootstrap -> Lobby -> Game -> Results):
    /// <list type="number">
    /// <item>persistBetweenScenes is now true, so the manager survives scene loads.</item>
    /// <item>Destroyed-subscriber detection accounts for Unity's fake-null semantics.</item>
    /// <item>OnDestroy calls base.OnDestroy so the singleton static is cleared.</item>
    /// <item>UnsubscribeFromAllEvents no longer uses reflection, and removes every
    /// matching subscription rather than only the first.</item>
    /// </list>
    /// The unused FactoryTM-only using directives were also stripped.
    /// </remarks>
    public class EventManager : SingletonBase<EventManager>
    {
        /// <summary>Set true while wiring new events; publish failures are otherwise quiet.</summary>
        public static bool DebugLoggingEnabled = false;

        private readonly Dictionary<Type, List<EventSubscription>> _subscriptions = new();
        private readonly List<EventSubscription> _pendingRemovals = new();
        private bool _isPublishing;

        protected override void Awake()
        {
            // Must be true. SingletonBase only calls DontDestroyOnLoad when this is
            // set, and the manager has to outlive every scene transition. Hardcoded
            // rather than left to the SerializeField because SingletonBase.Instance
            // can auto-create this component, and an auto-created instance never has
            // an inspector value applied.
            persistBetweenScenes = true;
            base.Awake();
        }

        private class EventSubscription
        {
            public WeakReference TargetReference { get; set; }
            public Delegate Callback { get; set; }
            public bool MarkedForRemoval { get; set; }
        }

        /// <summary>
        /// Listen for an event:
        /// <c>EventManager.Instance?.Subscribe&lt;SomeEvent&gt;(this, e =&gt; Handler(e.Value));</c>
        /// Always pair with <see cref="UnsubscribeFromAllEvents"/> in OnDestroy.
        /// </summary>
        public void Subscribe<T>(object target, Action<T> callback) where T : IEvent
        {
            var eventType = typeof(T);

            if (!_subscriptions.TryGetValue(eventType, out var subscriptionList))
            {
                subscriptionList = new List<EventSubscription>();
                _subscriptions[eventType] = subscriptionList;
            }

            subscriptionList.Add(new EventSubscription
            {
                TargetReference = new WeakReference(target),
                Callback = callback
            });
        }

        public void Unsubscribe<T>(object target) where T : IEvent
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var subscriptionList))
                return;

            RemoveMatching(subscriptionList, target);
        }

        /// <summary>
        /// Drops every subscription belonging to <paramref name="target"/>.
        /// Call from OnDestroy — mandatory with multiple scenes, since each transition
        /// destroys every subscriber in the outgoing scene.
        /// </summary>
        public void UnsubscribeFromAllEvents(object target)
        {
            foreach (var kvp in _subscriptions)
            {
                RemoveMatching(kvp.Value, target);
            }
        }

        public void UnsubscribeAllTargets()
        {
            foreach (var target in GetAllSubscribedTargets())
            {
                UnsubscribeFromAllEvents(target);
            }
        }

        /// <summary>
        /// Number of subscription records held for an event type, including any not yet
        /// pruned. Diagnostics only — used to assert that dead subscribers get dropped.
        /// </summary>
        public int GetSubscriptionCount<T>() where T : IEvent
        {
            return _subscriptions.TryGetValue(typeof(T), out var list) ? list.Count : 0;
        }

        public IEnumerable<object> GetAllSubscribedTargets()
        {
            var targets = new HashSet<object>();

            foreach (var subscriptionList in _subscriptions.Values)
            {
                foreach (var subscription in subscriptionList)
                {
                    var target = subscription.TargetReference.Target;
                    if (!IsDead(target))
                        targets.Add(target);
                }
            }

            return targets;
        }

        public void Publish<T>(T eventData) where T : IEvent
        {
            var eventType = typeof(T);
            if (DebugLoggingEnabled) Debug.Log($"EventManager: Attempting to publish event of type {eventType.Name}");

            if (!_subscriptions.TryGetValue(eventType, out var subscriptionList))
            {
                if (DebugLoggingEnabled) Debug.Log($"EventManager: No subscriptions found for event type {eventType.Name}");
                return;
            }

            if (DebugLoggingEnabled) Debug.Log($"EventManager: Found {subscriptionList.Count} subscriptions for {eventType.Name}");
            _isPublishing = true;

            for (int i = subscriptionList.Count - 1; i >= 0; i--)
            {
                var subscription = subscriptionList[i];

                if (subscription.MarkedForRemoval)
                    continue;

                var target = subscription.TargetReference.Target;

                if (IsDead(target))
                {
                    subscription.MarkedForRemoval = true;
                    _pendingRemovals.Add(subscription);
                    continue;
                }

                try
                {
                    ((Action<T>)subscription.Callback).Invoke(eventData);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error publishing event {eventType.Name} on target {target.GetType().Name}: {e.Message}");
                }
            }

            _isPublishing = false;
            CleanupPendingRemovals();
        }

        /// <summary>
        /// True when a subscriber can no longer receive events.
        /// </summary>
        /// <remarks>
        /// The UnityEngine.Object branch is load-bearing. WeakReference.Target is typed
        /// object, so a plain <c>== null</c> is reference equality and bypasses Unity's
        /// overloaded <c>==</c> that reports destroyed objects as null. Without it,
        /// destroyed MonoBehaviours keep getting invoked and throw
        /// MissingReferenceException into the catch above on every single publish —
        /// which with multiple scenes happens on every transition.
        /// </remarks>
        private static bool IsDead(object target)
        {
            if (target == null) return true;
            return target is UnityEngine.Object unityObject && unityObject == null;
        }

        private void RemoveMatching(List<EventSubscription> subscriptionList, object target)
        {
            for (int i = subscriptionList.Count - 1; i >= 0; i--)
            {
                var subscription = subscriptionList[i];
                if (subscription.TargetReference.Target != target)
                    continue;

                if (_isPublishing)
                {
                    subscription.MarkedForRemoval = true;
                    _pendingRemovals.Add(subscription);
                }
                else
                {
                    subscriptionList.RemoveAt(i);
                }
            }
        }

        private void CleanupPendingRemovals()
        {
            if (_pendingRemovals.Count == 0)
                return;

            foreach (var subscription in _pendingRemovals)
            {
                foreach (var kvp in _subscriptions)
                {
                    kvp.Value.Remove(subscription);
                }
            }

            _pendingRemovals.Clear();
        }

        public void Clear()
        {
            _subscriptions.Clear();
            _pendingRemovals.Clear();
            _isPublishing = false;
        }

        protected override void OnDestroy()
        {
            Clear();
            // Without this the SingletonBase static is never cleared, so a stale
            // reference survives teardown when domain reload is disabled.
            base.OnDestroy();
        }
    }
}
