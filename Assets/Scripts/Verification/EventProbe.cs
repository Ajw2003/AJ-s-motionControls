using EventSystems;
using EventTypes.SkipEvents;
using UnityEngine;

namespace Verification
{
    /// <summary>
    /// Throwaway subscriber used by <see cref="EventSystemVerification"/>. Deliberately
    /// does NOT unsubscribe in OnDestroy — its whole purpose is to be destroyed by a
    /// scene transition while still subscribed, which is the case that breaks without
    /// the destroyed-subscriber fix in EventManager.
    /// </summary>
    public class EventProbe : MonoBehaviour
    {
        public int Received;

        public void Listen()
        {
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, _ => Received++);
        }
    }
}
