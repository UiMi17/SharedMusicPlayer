using System;
using UnityEngine;
using VTOLVR.Multiplayer;

namespace SharedMusicPlayer
{
    /// <summary>
    /// Simple multiplayer-aware wrapper for CockpitRadio that handles synchronization.
    /// Both players can control playback - any button press executes locally and broadcasts to the other player.
    /// </summary>
    public class SharedCockpitRadioManager : MonoBehaviour
    {
        public static SharedCockpitRadioManager Instance { get; private set; }

        private CockpitRadio _cockpitRadio;
        private RadioNetSync _radioNetSync;
        private bool _isInitialized = false;
        private UnityEngine.Coroutine _initCoroutine = null;
        private bool _isReinitializing = false;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Logger.LogWarn("SharedCockpitRadioManager instance already exists, destroying duplicate", "SharedCockpitRadioManager");
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Logger.Log("SharedCockpitRadioManager initializing...", "SharedCockpitRadioManager");
        }

        private void Start()
        {
            _initCoroutine = StartCoroutine(InitializeCoroutine());
        }

        /// <summary>
        /// Re-initializes the manager for a new vehicle (e.g., when switching from single-crew to multi-crew)
        /// </summary>
        public void ReinitializeForNewVehicle()
        {
            // Prevent multiple simultaneous reinitializations
            if (_isReinitializing)
            {
                Logger.Log("Reinitialization already in progress, skipping duplicate call", "SharedCockpitRadioManager");
                return;
            }

            Logger.Log("Reinitializing SharedCockpitRadioManager for new vehicle", "SharedCockpitRadioManager");
            _isReinitializing = true;
            
            // Stop any running initialization coroutine
            if (_initCoroutine != null)
            {
                StopCoroutine(_initCoroutine);
                _initCoroutine = null;
            }
            
            // Reset state
            _isInitialized = false;
            _cockpitRadio = null;
            _radioNetSync = null;
            
            // Start new initialization
            _initCoroutine = StartCoroutine(InitializeCoroutine());
        }

        private System.Collections.IEnumerator InitializeCoroutine()
        {
            // Wait for CockpitRadio to exist
            while (_cockpitRadio == null)
            {
                _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (_cockpitRadio == null)
                    yield return new WaitForSeconds(0.1f);
            }

            Logger.Log("CockpitRadio found, waiting for RadioNetSync...", "SharedCockpitRadioManager");

            // Wait for RadioNetSync
            VTNetworking.VTNetworkManager.NetInstantiateRequest request = VTNetworking.VTNetworkManager.NetInstantiate(
                "RadioSyncNet/Prefab",
                Vector3.zero,
                Quaternion.identity,
                true
            );

            float timeout = 5f;
            float elapsed = 0f;

            while (!request.isReady && elapsed < timeout)
            {
                yield return new WaitForSeconds(0.2f);
                elapsed += 0.2f;
            }

            if (request.isReady && request.obj != null)
            {
                _radioNetSync = request.obj.GetComponent<RadioNetSync>();
                Logger.Log("RadioNetSync initialized", "SharedCockpitRadioManager");
            }
            else
            {
                _radioNetSync = UnityEngine.Object.FindAnyObjectByType<RadioNetSync>();
                if (_radioNetSync != null)
                {
                    Logger.Log("RadioNetSync found via fallback", "SharedCockpitRadioManager");
                }
            }

            // Ensure playlist is built
            SharedRadioController.Instance?.EnsurePlaylist();

            // Verify CockpitRadio is still valid before marking as initialized
            if (_cockpitRadio == null)
            {
                Logger.LogWarn("InitializeCoroutine: CockpitRadio became null before initialization complete, re-finding...", "SharedCockpitRadioManager");
                _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (_cockpitRadio == null)
                {
                    Logger.LogError("InitializeCoroutine: Could not find CockpitRadio, initialization failed", "SharedCockpitRadioManager");
                    yield break;
                }
            }

            _isInitialized = true;
            _isReinitializing = false; // Clear reinitialization flag
            _initCoroutine = null; // Clear coroutine reference when done
            Logger.Log($"SharedCockpitRadioManager initialized successfully (_cockpitRadio={_cockpitRadio != null}, _radioNetSync={_radioNetSync != null})", "SharedCockpitRadioManager");

            // If non-owner, request state from owner to sync
            if (!IsOwner())
            {
                ulong? ownerId = GetOtherCrewId();
                if (ownerId != null && _radioNetSync != null)
                {
                    Logger.Log($"Requesting music state from owner {ownerId}", "SharedCockpitRadioManager");
                    // Wait a bit for everything to settle, then request state
                    yield return new WaitForSeconds(0.5f);
                    _radioNetSync.RequestState((ulong)ownerId);
                }
            }
        }

        private bool IsOwner()
        {
            try
            {
                var scene = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
                if (scene == null) return true;
                var mySlot = scene.GetSlot(scene.localPlayer);
                if (mySlot == null) return true;
                var ownerSlot = scene.GetMCOwnerSlot(mySlot);
                return ownerSlot == mySlot;
            }
            catch { return true; }
        }

        private ulong? GetOtherCrewId()
        {
            try
            {
                var scene = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
                if (scene == null) return null;
                var mySlot = scene.GetSlot(scene.localPlayer);
                if (mySlot == null) return null;

                // Check if vehicle is multi-crew capable
                var baseSlot = scene.GetMCBaseSlot(mySlot);
                if (baseSlot == null || baseSlot.mcSlotCount <= 1)
                {
                    // Single-crew vehicle
                    return null;
                }

                return RadioNetSync.GetOtherCrewId(scene, mySlot);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Unified command handler for all radio operations.
        /// Separates user actions from internal operations and handles broadcasting.
        /// </summary>
        public void ExecuteCommand(RadioCommand command, bool isAutoAdvance = false, bool isRemoteCall = false)
        {
            // Re-find CockpitRadio if it became null (e.g., scene transition)
            if (_cockpitRadio == null && _isInitialized)
            {
                Logger.Log("ExecuteCommand: CockpitRadio became null, attempting to re-find it", "SharedCockpitRadioManager");
                _cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
                if (_cockpitRadio == null)
                {
                    Logger.LogWarn("ExecuteCommand: Could not re-find CockpitRadio", "SharedCockpitRadioManager");
                }
                else
                {
                    Logger.Log("ExecuteCommand: CockpitRadio re-found successfully", "SharedCockpitRadioManager");
                }
            }

            if (!_isInitialized || _cockpitRadio == null)
            {
                Logger.Log($"ExecuteCommand: Not initialized or CockpitRadio is null (command={command}, _isInitialized={_isInitialized}, _cockpitRadio={_cockpitRadio != null})", "SharedCockpitRadioManager");
                return;
            }

            ulong? otherId = GetOtherCrewId();
            bool isOwner = IsOwner();

            Logger.Log(
                $"ExecuteCommand: command={command}, isAutoAdvance={isAutoAdvance}, isRemoteCall={isRemoteCall}, isOwner={isOwner}, otherId={otherId}",
                "SharedCockpitRadioManager");

            // Handle auto-advance: only owner executes and broadcasts
            if (isAutoAdvance && !isOwner)
            {
                Logger.Log("ExecuteCommand: Suppressing non-owner auto-advance", "SharedCockpitRadioManager");
                return; // Non-owner suppresses auto-advance, waits for owner's broadcast
            }

            // Execute the command locally
            VtolVRMod.CockpitRadioRedirectPatch.isRemoteCall = true;

            // Stop streaming coroutine for Stop command
            if (command == RadioCommand.Stop)
            {
                var streamRoutineField = typeof(CockpitRadio).GetField("streamRoutine", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (streamRoutineField != null)
                {
                    var streamRoutine = streamRoutineField.GetValue(_cockpitRadio) as UnityEngine.Coroutine;
                    if (streamRoutine != null)
                    {
                        Logger.Log("ExecuteCommand: Stopping streamRoutine coroutine", "SharedCockpitRadioManager");
                        _cockpitRadio.StopCoroutine(streamRoutine);
                        streamRoutineField.SetValue(_cockpitRadio, null);
                    }
                }
            }

            // Execute the appropriate CockpitRadio method
            switch (command)
            {
                case RadioCommand.PlayToggle:
                    _cockpitRadio.PlayButton();
                    break;
                case RadioCommand.Next:
                    _cockpitRadio.NextSong();
                    break;
                case RadioCommand.Prev:
                    _cockpitRadio.PrevSong();
                    break;
                case RadioCommand.Stop:
                    _cockpitRadio.StopPlayingSong();
                    break;
            }

            VtolVRMod.CockpitRadioRedirectPatch.isRemoteCall = false;

            // Broadcast to other player (only for local user actions, not remote calls)
            if (!isRemoteCall && otherId != null && _radioNetSync != null)
            {
                switch (command)
                {
                    case RadioCommand.PlayToggle:
                        Logger.Log($"ExecuteCommand: Broadcasting PlayToggle to {otherId}", "SharedCockpitRadioManager");
                        _radioNetSync.SendPlayToggle((ulong)otherId);
                        break;
                    case RadioCommand.Next:
                        Logger.Log($"ExecuteCommand: Broadcasting Next to {otherId}", "SharedCockpitRadioManager");
                        _radioNetSync.SendNext((ulong)otherId);
                        break;
                    case RadioCommand.Prev:
                        Logger.Log($"ExecuteCommand: Broadcasting Prev to {otherId}", "SharedCockpitRadioManager");
                        _radioNetSync.SendPrev((ulong)otherId);
                        break;
                    case RadioCommand.Stop:
                        Logger.Log($"ExecuteCommand: Broadcasting Stop to {otherId}", "SharedCockpitRadioManager");
                        _radioNetSync.SendStop((ulong)otherId);
                        break;
                }
            }
            else if (isRemoteCall)
            {
                Logger.Log("ExecuteCommand: Remote call, not broadcasting", "SharedCockpitRadioManager");
            }
            else
            {
                Logger.Log($"ExecuteCommand: Not broadcasting (otherId={otherId}, _radioNetSync={_radioNetSync != null})", "SharedCockpitRadioManager");
            }
        }

        /// <summary>
        /// Handles PlayButton - both players execute locally and broadcast to each other
        /// </summary>
        public void PlayButton()
        {
            ExecuteCommand(RadioCommand.PlayToggle);
        }

        /// <summary>
        /// Handles NextSong - both players execute locally and broadcast to each other
        /// </summary>
        public void NextSong()
        {
            ExecuteCommand(RadioCommand.Next);
        }

        /// <summary>
        /// Handles NextSong with auto-advance detection
        /// </summary>
        public void NextSong(bool isAutoAdvance)
        {
            ExecuteCommand(RadioCommand.Next, isAutoAdvance: isAutoAdvance);
        }

        /// <summary>
        /// Handles PrevSong - both players execute locally and broadcast to each other
        /// </summary>
        public void PrevSong()
        {
            ExecuteCommand(RadioCommand.Prev);
        }

        /// <summary>
        /// Handles StopPlayingSong - both players execute locally and broadcast to each other
        /// This is called by the Harmony patch when StopPlayingSong() is called on CockpitRadio
        /// </summary>
        public void StopPlayingSong()
        {
            ExecuteCommand(RadioCommand.Stop);
        }

        /// <summary>
        /// Public method that can be called directly by UI or other code to stop playback
        /// This ensures stops are always synchronized between players
        /// </summary>
        public void Stop()
        {
            ExecuteCommand(RadioCommand.Stop);
        }

        /// <summary>
        /// Handles state request from copilot - stops music if playing and sends current state
        /// This is called when copilot actually enters the cabin (not when they click to join)
        /// </summary>
        public void HandleStateRequest()
        {
            if (!_isInitialized || _cockpitRadio == null || _radioNetSync == null)
            {
                Logger.LogWarn("HandleStateRequest: Not initialized, cannot send state", "SharedCockpitRadioManager");
                return;
            }

            // Only owner (pilot) should handle state requests
            if (!IsOwner())
            {
                Logger.LogWarn("HandleStateRequest: Non-owner received state request, ignoring", "SharedCockpitRadioManager");
                return;
            }

            Logger.Log("HandleStateRequest: Copilot entered cabin, checking current state and stopping if playing", "SharedCockpitRadioManager");

            // Get current state
            int currentSongIndex = 0;
            bool isCurrentlyPlaying = false;
            bool isPaused = false;

            try
            {
                var songIdxField = typeof(CockpitRadio).GetField("songIdx", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var pausedField = typeof(CockpitRadio).GetField("paused", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var audioSourceField = typeof(CockpitRadio).GetField("audioSource", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                if (songIdxField != null)
                {
                    currentSongIndex = (int)songIdxField.GetValue(_cockpitRadio);
                }

                if (pausedField != null)
                {
                    isPaused = (bool)pausedField.GetValue(_cockpitRadio);
                }

                if (audioSourceField != null)
                {
                    var audioSource = audioSourceField.GetValue(_cockpitRadio) as UnityEngine.AudioSource;
                    if (audioSource != null && audioSource.clip != null)
                    {
                        isCurrentlyPlaying = !isPaused && audioSource.isPlaying;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"HandleStateRequest: Error reading state: {ex}", "SharedCockpitRadioManager");
                return; // Don't proceed if we can't read state
            }

            // Stop music if playing (as requested - stop pilot's music when copilot actually enters)
            if (isCurrentlyPlaying)
            {
                Logger.Log("HandleStateRequest: Music is playing, stopping it", "SharedCockpitRadioManager");
                ExecuteCommand(RadioCommand.Stop, isRemoteCall: true);
            }

            // Send state to copilot (always stopped state after stopping)
            // Use coroutine to ensure RPC is sent after a small delay to avoid timing issues
            StartCoroutine(SendStateToCopilotCoroutine(currentSongIndex));
        }

        private System.Collections.IEnumerator SendStateToCopilotCoroutine(int songIndex)
        {
            // Small delay to ensure copilot's RadioNetSync is fully ready
            yield return new WaitForSeconds(0.1f);

            ulong? copilotId = GetOtherCrewId();
            if (copilotId != null && _radioNetSync != null)
            {
                try
                {
                    // Get filename for the current song index to help with matching
                    string currentFileName = null;
                    if (SharedRadioController.Instance != null)
                    {
                        currentFileName = SharedRadioController.Instance.GetFileNameForIndex(songIndex);
                    }
                    
                    Logger.Log($"HandleStateRequest: Sending state to copilot {copilotId}: songIndex={songIndex}, fileName={currentFileName ?? "unknown"}, isPlaying=false, isPaused=false", "SharedCockpitRadioManager");
                    _radioNetSync.SendState((ulong)copilotId, songIndex, isPlaying: false, isPaused: false);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"HandleStateRequest: Error sending state to copilot: {ex}", "SharedCockpitRadioManager");
                }
            }
            else
            {
                Logger.LogWarn($"HandleStateRequest: Cannot send state (copilotId={copilotId}, _radioNetSync={_radioNetSync != null})", "SharedCockpitRadioManager");
            }
        }

        /// <summary>
        /// Handles state sync from pilot - syncs copilot's radio to match pilot's state
        /// </summary>
        public void HandleStateSync(int songIndex, bool isPlaying, bool isPaused)
        {
            if (!_isInitialized || _cockpitRadio == null)
            {
                Logger.LogWarn("HandleStateSync: Not initialized, cannot sync state", "SharedCockpitRadioManager");
                return;
            }

            Logger.Log($"HandleStateSync: Syncing to state: songIndex={songIndex}, isPlaying={isPlaying}, isPaused={isPaused}", "SharedCockpitRadioManager");

            try
            {
                // Ensure playlist is up-to-date before syncing (important after download/cancellation)
                if (SharedRadioController.Instance != null)
                {
                    SharedRadioController.Instance.RebuildPlaylist();
                    Logger.Log("HandleStateSync: Rebuilt playlist before syncing state", "SharedCockpitRadioManager");
                    
                    // Validate index is within bounds - if not, clamp it and log warning
                    int playlistCount = SharedRadioController.Instance.GetPlaylistPaths().Count;
                    if (songIndex < 0 || songIndex >= playlistCount)
                    {
                        Logger.LogWarn($"HandleStateSync: Received songIndex={songIndex} is out of bounds (playlist has {playlistCount} files), clamping to valid range", "SharedCockpitRadioManager");
                        songIndex = Mathf.Clamp(songIndex, 0, Math.Max(0, playlistCount - 1));
                    }
                }

                // Set song index
                var songIdxField = typeof(CockpitRadio).GetField("songIdx", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (songIdxField != null)
                {
                    songIdxField.SetValue(_cockpitRadio, songIndex);
                    Logger.Log($"HandleStateSync: Set song index to {songIndex}", "SharedCockpitRadioManager");
                }

                // Sync to SharedRadioController as well (this will clamp index to valid range)
                if (SharedRadioController.Instance != null)
                {
                    SharedRadioController.Instance.SetCurrentIndex(songIndex);
                }

                // Music should always be stopped when copilot joins (per user requirement)
                // So we don't start it even if isPlaying is true
                Logger.Log("HandleStateSync: State synced, music remains stopped (per requirement when copilot joins)", "SharedCockpitRadioManager");
            }
            catch (Exception ex)
            {
                Logger.LogError($"HandleStateSync: Error syncing state: {ex}", "SharedCockpitRadioManager");
            }
        }

    }
}
