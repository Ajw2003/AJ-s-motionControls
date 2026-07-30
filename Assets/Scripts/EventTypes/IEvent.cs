namespace EventSystems
{
    /// <summary>
    /// Marker interface for anything publishable through <see cref="EventManager"/>.
    /// The ISomeFunctionality syntax is reserved for interfaces, which are promises to
    /// have some sort of functionality. Interfaces are not classes.
    /// </summary>
    public interface IEvent
    {
    }
}
