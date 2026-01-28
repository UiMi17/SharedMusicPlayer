using UnityEngine;

namespace SharedMusicPlayer
{
    public class Logger
    {
        private static readonly string ModName = "SharedMusicPlayer";

        public static void Log(object message, string category = null)
        {
            string cat = category ?? ModName;
            InGameLogger.AddLog(LogLevel.Info, cat, message?.ToString() ?? "null");
        }

        public static void LogDebug(object message, string category = null)
        {
            string cat = category ?? ModName;
            InGameLogger.AddLog(LogLevel.Debug, cat, message?.ToString() ?? "null");
        }

        public static void LogWarn(object message, string category = null)
        {
            string cat = category ?? ModName;
            InGameLogger.AddLog(LogLevel.Warn, cat, message?.ToString() ?? "null");
        }

        public static void LogError(object message, string category = null)
        {
            string cat = category ?? ModName;
            InGameLogger.AddLog(LogLevel.Error, cat, message?.ToString() ?? "null");
        }
    }
}