using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VTOLVR.Multiplayer;

namespace SharedMusicPlayer
{
    /// <summary>
    /// Manages the playlist and current song index.
    /// Simple playlist building and index tracking only.
    /// </summary>
    public class SharedRadioController : MonoBehaviour
    {
        public static SharedRadioController Instance { get; private set; }

        private readonly List<string> _playlistPaths = new List<string>();
        private CockpitRadio _cockpitRadio;
        private int _currentIndex = 0;
        private bool _playlistBuilt = false;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Logger.LogWarn("SharedRadioController instance already exists, destroying duplicate", "SharedRadioController");
                Destroy(gameObject);
                return;
            }
            Logger.Log("SharedRadioController initializing...", "SharedRadioController");
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Logger.Log("SharedRadioController initialized successfully", "SharedRadioController");
        }

        public IReadOnlyList<string> GetPlaylistPaths()
        {
            EnsurePlaylist();
            return _playlistPaths;
        }

        public void EnsurePlaylist()
        {
            if (_playlistBuilt)
                return;

            Logger.Log("Building playlist...", "SharedRadioController");
            try
            {
                // Determine path by ownership (owner: local RadioMusic; non-owner: SharedRadioMusic)
                string basePath = GameSettings.RADIO_MUSIC_PATH;
                var scene = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
                if (scene != null)
                {
                    var mySlot = scene.GetSlot(scene.localPlayer);
                    var ownerSlot = mySlot != null ? scene.GetMCOwnerSlot(mySlot) : null;
                    bool amOwner = (ownerSlot != null && ownerSlot == mySlot);
                    if (!amOwner)
                    {
                        basePath = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
                        Directory.CreateDirectory(basePath);
                        Logger.Log($"Non-owner detected, using shared music path: {basePath}", "SharedRadioController");
                    }
                    else
                    {
                        Logger.Log($"Owner detected, using local music path: {basePath}", "SharedRadioController");
                    }
                }

                if (!Directory.Exists(basePath))
                {
                    Logger.LogWarn($"Path {basePath} does not exist, using default path", "SharedRadioController");
                    basePath = GameSettings.defaultRadioMusicPath;
                    Directory.CreateDirectory(basePath);
                }

                var files = Directory.GetFiles(Path.GetFullPath(basePath), "*.mp3");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                _playlistPaths.Clear();
                _playlistPaths.AddRange(files);

                Logger.Log($"Playlist built with {_playlistPaths.Count} file(s) from {basePath}", "SharedRadioController");

                // Mirror playlist into CockpitRadio for decoding/streaming
                _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (_cockpitRadio != null)
                {
                    _cockpitRadio.origSongs.Clear();
                    _cockpitRadio.shuffledSongs.Clear();
                    foreach (var f in _playlistPaths)
                    {
                        _cockpitRadio.origSongs.Add(f);
                        _cockpitRadio.shuffledSongs.Add(f);
                    }
                    _cockpitRadio.songIdx = 0;
                    Logger.Log("Playlist mirrored to CockpitRadio", "SharedRadioController");
                }

                _playlistBuilt = true;
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to build playlist: {e}", "SharedRadioController");
                Debug.LogError("[SharedRadioController] Failed to build playlist: " + e);
            }
        }

        public void RebuildPlaylist()
        {
            Logger.Log("Rebuilding playlist...", "SharedRadioController");
            _playlistBuilt = false;
            EnsurePlaylist();
            Logger.Log("Playlist rebuild complete", "SharedRadioController");
        }

        /// <summary>
        /// Gets the current song index
        /// </summary>
        public int GetCurrentIndex()
        {
            EnsurePlaylist();
            if (_cockpitRadio == null)
                _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
            if (_cockpitRadio != null && _cockpitRadio.songIdx != _currentIndex)
            {
                _currentIndex = _cockpitRadio.songIdx;
            }
            return _currentIndex;
        }

        /// <summary>
        /// Sets the current song index and syncs to CockpitRadio
        /// </summary>
        public void SetCurrentIndex(int index)
        {
            EnsurePlaylist();
            if (index < 0 || index >= _playlistPaths.Count)
            {
                Logger.LogWarn($"Invalid index {index}, clamping to valid range", "SharedRadioController");
                index = Mathf.Clamp(index, 0, _playlistPaths.Count - 1);
            }
            
            if (_currentIndex != index)
            {
                Logger.Log($"Setting index: {_currentIndex} -> {index}", "SharedRadioController");
                _currentIndex = index;
                
                if (_cockpitRadio == null)
                    _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (_cockpitRadio != null)
                {
                    _cockpitRadio.songIdx = index;
                }
            }
        }

        public string GetPathForIndex(int index)
        {
            EnsurePlaylist();
            if (index < 0 || index >= _playlistPaths.Count) return null;
            return _playlistPaths[index];
        }

        /// <summary>
        /// Gets the filename (without path) for a given index
        /// </summary>
        public string GetFileNameForIndex(int index)
        {
            EnsurePlaylist();
            if (index < 0 || index >= _playlistPaths.Count) return null;
            return Path.GetFileName(_playlistPaths[index]);
        }

        /// <summary>
        /// Finds the index of a file by its filename (case-insensitive)
        /// Returns -1 if not found
        /// </summary>
        public int FindIndexByFileName(string fileName)
        {
            EnsurePlaylist();
            if (string.IsNullOrEmpty(fileName))
                return -1;
            
            for (int i = 0; i < _playlistPaths.Count; i++)
            {
                string playlistFileName = Path.GetFileName(_playlistPaths[i]);
                if (string.Equals(playlistFileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int ComputeStableNameHash(string name)
        {
            unchecked
            {
                const int fnvPrime = 16777619;
                int hash = unchecked((int)2166136261);
                for (int i = 0; i < name.Length; i++)
                {
                    char c = char.ToLowerInvariant(name[i]);
                    hash ^= c;
                    hash *= fnvPrime;
                }
                return hash;
            }
        }

        public static int ComputeCrc32Head(string path, int maxBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int toRead = (int)Mathf.Min(maxBytes, (int)fs.Length);
                    byte[] buffer = new byte[toRead];
                    int read = fs.Read(buffer, 0, toRead);
                    return Crc32(buffer, 0, read);
                }
            }
            catch { return 0; }
        }

        private static int Crc32(byte[] data, int offset, int count)
        {
            unchecked
            {
                uint crc = 0xFFFFFFFFu;
                for (int i = 0; i < count; i++)
                {
                    crc ^= data[offset + i];
                    for (int b = 0; b < 8; b++)
                    {
                        uint mask = (crc & 1) != 0 ? 0xEDB88320u : 0u;
                        crc = (crc >> 1) ^ mask;
                    }
                }
                return (int)~crc;
            }
        }
    }
}
