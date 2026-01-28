namespace SharedMusicPlayer
{
    /// <summary>
    /// Commands that can be executed on the radio system
    /// </summary>
    public enum RadioCommand
    {
        PlayToggle,
        Next,
        Prev,
        Stop
    }

    /// <summary>
    /// Context information for a radio command execution
    /// </summary>
    public struct CommandContext
    {
        public RadioCommand Command;
        public bool IsAutoAdvance;
        public bool IsRemoteCall;
        public ulong? SourcePlayerId;

        public CommandContext(RadioCommand command, bool isAutoAdvance = false, bool isRemoteCall = false, ulong? sourcePlayerId = null)
        {
            Command = command;
            IsAutoAdvance = isAutoAdvance;
            IsRemoteCall = isRemoteCall;
            SourcePlayerId = sourcePlayerId;
        }
    }
}
