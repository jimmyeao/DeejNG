namespace DeejNG.Models
{
    /// <summary>
    /// Defines the action types that can be assigned to a physical button.
    /// </summary>
    public enum ButtonPressType : byte
    {
        /// <summary>
        /// No press (weird this should't happen)
        /// </summary>
        None = 0,

        /// <summary>
        /// Short press
        /// </summary>
        Short = 1,

        /// <summary>
        /// Long press
        /// </summary>
        Long = 2,
    }
}
