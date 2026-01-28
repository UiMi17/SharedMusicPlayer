namespace SharedMusicPlayer
{
    /// <summary>
    /// Shared constants used across the mod
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Number of bytes to read from file head for CRC32 calculation (64KB)
        /// </summary>
        public const int Crc32HeadBytes = 65536;

        /// <summary>
        /// Maximum number of frames to wait for audio source to be ready
        /// </summary>
        public const int MaxAudioSourceWaitFrames = 60;

        /// <summary>
        /// Time threshold in seconds to ignore remote song changes after local changes
        /// </summary>
        public const float RemoteChangeIgnoreThresholdSeconds = 0.3f;
    }
}
