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
            // Parameter is named 'evt', not '_', so the discard below is actually a
            // discard rather than an assignment to the lambda parameter.
            EventManager.Instance?.Subscribe<SkipOccurredEvent>(this, evt =>
            {
                Received++;

                // Touch a Unity-native member on purpose. A destroyed MonoBehaviour
                // tolerates plain C# field access, so a probe that only did Received++
                // would sit there as a harmless zombie. Real handlers read transforms
                // and components, which throw MissingReferenceException once the object
                // is destroyed — that is the failure this probe needs to reproduce.
                _ = transform.position;
            });
        }
    }
}
