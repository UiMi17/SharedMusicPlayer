using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SharedMusicPlayer
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public string Category { get; set; }

        public string ToFormattedString()
        {
            string timeStr = Timestamp.ToString("HH:mm:ss.fff");
            string levelStr = Level switch
            {
                LogLevel.Debug => "DBG",
                LogLevel.Info => "INF",
                LogLevel.Warn => "WRN",
                LogLevel.Error => "ERR",
                _ => "UNK"
            };

            return $"[{timeStr} {levelStr}] {Category}: {Message}";
        }
    }

    public class InGameLogger : MonoBehaviour
    {
        private static InGameLogger _instance;
        public static InGameLogger Instance => _instance;

        private const int MaxLogEntries = 500;
        private const int VisibleLogLines = 30;
        private const KeyCode ToggleKey = KeyCode.F8;

        private readonly Queue<LogEntry> _logEntries = new Queue<LogEntry>();
        private readonly object _logLock = new object();
        private static DateTime _sessionStartTime = DateTime.Now;
        private static bool _hasBeenSaved = false;
        
        private bool _isVisible = false;
        private Vector2 _scrollPosition = Vector2.zero;
        
        // GUI Styles
        private GUIStyle _headerStyle;
        private GUIStyle _logStyle;
        private GUIStyle _boxStyle;
        private bool _stylesInitialized = false;

        private void Awake()
        {
            Debug.Log("[InGameLogger] Awake() called");
            
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning($"[InGameLogger] Instance already exists ({_instance.gameObject.name}), destroying duplicate");
                Destroy(gameObject);
                return;
            }
            
            Debug.Log("[InGameLogger] Setting instance and configuring DontDestroyOnLoad");
            _instance = this;
            DontDestroyOnLoad(gameObject);
            
            // Subscribe to scene load events
            SceneManager.sceneLoaded += OnSceneLoaded;
            Debug.Log("[InGameLogger] Subscribed to SceneManager.sceneLoaded event");
            
            // Ensure GameObject stays active across scenes
            gameObject.SetActive(true);
            Debug.Log($"[InGameLogger] GameObject active state: {gameObject.activeInHierarchy}, enabled: {enabled}");
            
            Debug.Log("[InGameLogger] Awake() complete");
            
            // Add a test log entry to verify the system works
            AddLog(LogLevel.Info, "InGameLogger", "Console initialized successfully!");
            Debug.Log("[InGameLogger] Added test log entry");
        }

        private void OnEnable()
        {
            Debug.Log($"[InGameLogger] OnEnable() called. Instance is this: {_instance == this}, GameObject active: {gameObject.activeInHierarchy}");
            
            // Re-activate if we're the instance and somehow got disabled
            if (_instance == this)
            {
                gameObject.SetActive(true);
                Debug.Log("[InGameLogger] Re-activated GameObject in OnEnable()");
            }
        }

        private void Update()
        {
            // Check for toggle key
            if (Input.GetKeyDown(ToggleKey))
            {
                ToggleConsole();
            }
        }

        private void OnGUI()
        {
            if (!_isVisible) return;
            
            // Initialize styles on first use
            if (!_stylesInitialized)
            {
                InitializeStyles();
            }
            
            // Calculate console area (bottom half of screen)
            float screenWidth = Screen.width;
            float screenHeight = Screen.height;
            float consoleHeight = screenHeight * 0.5f;
            float headerHeight = 30f;
            float logAreaHeight = consoleHeight - headerHeight;
            
            Rect consoleRect = new Rect(0, screenHeight - consoleHeight, screenWidth, consoleHeight);
            Rect headerRect = new Rect(0, screenHeight - consoleHeight, screenWidth, headerHeight);
            Rect logAreaRect = new Rect(0, screenHeight - consoleHeight + headerHeight, screenWidth, logAreaHeight);
            
            // Draw background box
            GUI.Box(consoleRect, "", _boxStyle);
            
            // Draw header
            GUI.Label(headerRect, "SharedMusicPlayer Console (Press F8 to toggle)", _headerStyle);
            
            // Draw scrollable log area
            lock (_logLock)
            {
                List<LogEntry> entries = new List<LogEntry>(_logEntries);
                
                // Calculate content width (account for scrollbar)
                float contentWidth = screenWidth - 30f; // Leave space for scrollbar
                float lineHeight = _logStyle.lineHeight;
                float padding = 5f;
                
                // Calculate total content height by measuring all entries
                float totalContentHeight = padding; // Start with top padding
                foreach (var entry in entries)
                {
                    string formatted = entry.ToFormattedString();
                    // Calculate how many lines this entry will take when wrapped
                    float entryHeight = _logStyle.CalcHeight(new GUIContent(formatted), contentWidth - (padding * 2));
                    totalContentHeight += entryHeight + 2f; // Add small spacing between entries
                }
                totalContentHeight += padding; // Bottom padding
                
                // Ensure minimum height
                float contentHeight = Mathf.Max(logAreaHeight + 10f, totalContentHeight);
                
                // Scroll view - content area matches available width
                Rect scrollViewContent = new Rect(0, 0, contentWidth, contentHeight);
                _scrollPosition = GUI.BeginScrollView(logAreaRect, _scrollPosition, scrollViewContent);
                
                float yPos = padding;
                
                // Show all entries (oldest to newest, scrolling from top)
                foreach (var entry in entries)
                {
                    string formatted = entry.ToFormattedString();
                    Color originalColor = GUI.color;
                    
                    // Set color based on log level
                    switch (entry.Level)
                    {
                        case LogLevel.Debug:
                            GUI.color = Color.gray;
                            break;
                        case LogLevel.Info:
                            GUI.color = Color.white;
                            break;
                        case LogLevel.Warn:
                            GUI.color = Color.yellow;
                            break;
                        case LogLevel.Error:
                            GUI.color = Color.red;
                            break;
                    }
                    
                    // Calculate height for this entry with word wrapping
                    float entryHeight = _logStyle.CalcHeight(new GUIContent(formatted), contentWidth - (padding * 2));
                    Rect entryRect = new Rect(padding, yPos, contentWidth - (padding * 2), entryHeight);
                    
                    // Draw the text with word wrapping
                    GUI.Label(entryRect, formatted, _logStyle);
                    GUI.color = originalColor;
                    
                    yPos += entryHeight + 2f; // Move down for next entry
                }
                
                GUI.EndScrollView();
            }
        }

        private void InitializeStyles()
        {
            // Header style
            _headerStyle = new GUIStyle(GUI.skin.label);
            _headerStyle.fontSize = 16;
            _headerStyle.fontStyle = FontStyle.Bold;
            _headerStyle.normal.textColor = new Color(1f, 0.922f, 0.016f); // Yellow
            _headerStyle.alignment = TextAnchor.MiddleLeft;
            _headerStyle.padding = new RectOffset(10, 10, 5, 5);
            
            // Log text style
            _logStyle = new GUIStyle(GUI.skin.label);
            _logStyle.fontSize = 14;
            _logStyle.normal.textColor = Color.white;
            _logStyle.alignment = TextAnchor.UpperLeft;
            _logStyle.padding = new RectOffset(0, 0, 0, 0);
            _logStyle.wordWrap = true; // CRITICAL: Enable word wrapping
            _logStyle.richText = false; // Keep it simple
            _logStyle.clipping = TextClipping.Overflow; // Allow text to wrap
            
            // Box style for background
            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.85f)); // Semi-transparent black
            
            _stylesInitialized = true;
            Debug.Log("[InGameLogger] GUI styles initialized");
        }

        private Texture2D MakeTex(int width, int height, Color col)
        {
            Color[] pix = new Color[width * height];
            for (int i = 0; i < pix.Length; i++)
                pix[i] = col;
            
            Texture2D result = new Texture2D(width, height);
            result.SetPixels(pix);
            result.Apply();
            return result;
        }

        private int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return 1;
            int count = 1;
            foreach (char c in text)
            {
                if (c == '\n') count++;
            }
            return count;
        }

        private void ToggleConsole()
        {
            _isVisible = !_isVisible;
            Debug.Log($"[InGameLogger] Console toggled. Visible: {_isVisible}");
            
            if (_isVisible)
            {
                // Auto-scroll to bottom when opening
                lock (_logLock)
                {
                    if (_stylesInitialized && _logStyle != null)
                    {
                        float contentWidth = Screen.width - 30f;
                        float totalHeight = 5f; // Start with padding
                        foreach (var entry in _logEntries)
                        {
                            string formatted = entry.ToFormattedString();
                            totalHeight += _logStyle.CalcHeight(new GUIContent(formatted), contentWidth - 10f) + 2f;
                        }
                        totalHeight += 5f; // Bottom padding
                        _scrollPosition = new Vector2(0, totalHeight);
                    }
                    else
                    {
                        // Fallback if styles not initialized yet
                        _scrollPosition = new Vector2(0, 10000f); // Large value to scroll to bottom
                    }
                }
            }
        }

        private void SetConsoleVisible(bool visible)
        {
            _isVisible = visible;
            Debug.Log($"[InGameLogger] Console visibility set to: {visible}");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[InGameLogger] Scene loaded: {scene.name}, mode: {mode}");
            // Ensure we stay active
            if (_instance == this)
            {
                gameObject.SetActive(true);
            }
        }

        private void OnDestroy()
        {
            Debug.Log("[InGameLogger] OnDestroy() called. Instance is this: " + (_instance == this));
            if (_instance == this)
            {
                // Save logs before destroying
                SaveLogsToFile();
                
                SceneManager.sceneLoaded -= OnSceneLoaded;
                Debug.Log("[InGameLogger] Unsubscribed from SceneManager.sceneLoaded event");
                _instance = null;
            }
        }

        /// <summary>
        /// Saves all log entries to a text file in the mod's directory.
        /// Called automatically on mod unload and OnDestroy.
        /// </summary>
        public static void SaveLogsToFile()
        {
            // Prevent saving multiple times
            if (_hasBeenSaved || _instance == null)
                return;

            try
            {
                lock (_instance._logLock)
                {
                    if (_instance._logEntries.Count == 0)
                    {
                        Debug.Log("[InGameLogger] No log entries to save");
                        return;
                    }

                    // Construct the mod directory path
                    string gameRoot = VTResources.gameRootDirectory;
                    string modDirectory = Path.Combine(gameRoot, "@Mod Loader", "Mods", "SharedMusicPlayer");

                    // Create directory if it doesn't exist
                    if (!Directory.Exists(modDirectory))
                    {
                        Directory.CreateDirectory(modDirectory);
                        Debug.Log($"[InGameLogger] Created mod directory: {modDirectory}");
                    }

                    // Generate timestamped filename
                    DateTime now = DateTime.Now;
                    string timestamp = now.ToString("yyyy-MM-dd_HH-mm-ss");
                    string filename = $"logs_{timestamp}.txt";
                    string filePath = Path.Combine(modDirectory, filename);

                    // Build log file content
                    StringBuilder sb = new StringBuilder();
                    
                    // Header
                    sb.AppendLine("========================================");
                    sb.AppendLine("SharedMusicPlayer Mod Log File");
                    sb.AppendLine("========================================");
                    sb.AppendLine($"Session Start: {_sessionStartTime:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Session End:   {now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"Duration:      {(now - _sessionStartTime).TotalSeconds:F2} seconds");
                    sb.AppendLine($"Total Entries: {_instance._logEntries.Count}");
                    sb.AppendLine("========================================");
                    sb.AppendLine();

                    // Log entries
                    foreach (var entry in _instance._logEntries)
                    {
                        // Use full timestamp format for file (YYYY-MM-DD HH:mm:ss.fff)
                        string timeStr = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        string levelStr = entry.Level switch
                        {
                            LogLevel.Debug => "DBG",
                            LogLevel.Info => "INF",
                            LogLevel.Warn => "WRN",
                            LogLevel.Error => "ERR",
                            _ => "UNK"
                        };

                        sb.AppendLine($"[{timeStr} {levelStr}] {entry.Category}: {entry.Message}");
                    }

                    // Write to file
                    File.WriteAllText(filePath, sb.ToString());
                    Debug.Log($"[InGameLogger] Successfully saved {_instance._logEntries.Count} log entries to: {filePath}");
                    
                    _hasBeenSaved = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[InGameLogger] Failed to save logs to file: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public static void AddLog(LogLevel level, string category, string message)
        {
            if (_instance == null)
            {
                Debug.LogWarning($"[InGameLogger] Attempted to log but instance is null: [{level}] {category}: {message}");
                return;
            }

            lock (_instance._logLock)
            {
                var entry = new LogEntry
                {
                    Timestamp = DateTime.Now,
                    Level = level,
                    Message = message,
                    Category = category
                };

                _instance._logEntries.Enqueue(entry);

                // Limit log entries to prevent memory issues
                while (_instance._logEntries.Count > MaxLogEntries)
                {
                    _instance._logEntries.Dequeue();
                }
            }
        }
    }
}
