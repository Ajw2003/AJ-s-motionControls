namespace EventTypes.AudioEvents
{
    /// <summary>
    /// Which mixer group a sound is routed through, and so which volume slider owns it.
    /// </summary>
    public enum AudioChannel
    {
        Sfx,
        Music,
        Ambient,
        Ui,
    }
}
