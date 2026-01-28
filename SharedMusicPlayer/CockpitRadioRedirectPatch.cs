using HarmonyLib;
using UnityEngine;
using SharedMusicPlayer;
using VTOLVR.Multiplayer;

namespace VtolVRMod
{
    /// <summary>
    /// Simple Harmony patches that redirect all CockpitRadio method calls to SharedCockpitRadioManager.
    /// </summary>
    [HarmonyPatch]
    public class CockpitRadioRedirectPatch
    {
        // Flag to allow original methods to run for remote calls
        public static bool isRemoteCall = false;

        /// <summary>
        /// Checks if we're in a multi-crew vehicle with another crew member present.
        /// Returns false for single-crew vehicles or when no other crew is present.
        /// </summary>
        private static bool IsMultiCrewMode()
        {
            try
            {
                var scene = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
                if (scene == null)
                {
                    // No multiplayer scene manager = single player or single-crew vehicle
                    return false;
                }

                var mySlot = scene.GetSlot(scene.localPlayer);
                if (mySlot == null)
                {
                    return false;
                }

                // Check if vehicle is multi-crew capable
                var baseSlot = scene.GetMCBaseSlot(mySlot);
                if (baseSlot == null)
                {
                    return false;
                }

                // If mcSlotCount is 1, it's a single-crew vehicle
                if (baseSlot.mcSlotCount <= 1)
                {
                    return false;
                }

                // Check if there's actually another crew member
                ulong? otherCrewId = RadioNetSync.GetOtherCrewId(scene, mySlot);
                if (otherCrewId == null)
                {
                    // No other crew member present = single-crew mode
                    return false;
                }

                // We have a multi-crew vehicle with another crew member
                return true;
            }
            catch
            {
                // On error, assume single-crew to avoid breaking things
                return false;
            }
        }

        // Manage CockpitRadio's Start method - use SharedRadioController for multi-crew, original for single-crew
        [HarmonyPatch(typeof(CockpitRadio), "Start")]
        [HarmonyPrefix]
        public static bool Start_Prefix(CockpitRadio __instance)
        {
            // If in multi-crew mode, use SharedRadioController to manage playlist
            if (IsMultiCrewMode())
            {
                SharedRadioController.Instance?.EnsurePlaylist();
                return false; // Prevent original Start from running
            }
            
            // Single-crew mode: let original Start run to initialize playlist normally
            return true;
        }

        // Initialize SharedCockpitRadioManager when CockpitRadio awakens
        [HarmonyPatch(typeof(CockpitRadio), "Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(CockpitRadio __instance)
        {
            // Reset state flags when entering a new vehicle
            _justStopped = false;
            _isAutoAdvanceNext = false;
            
            // Ensure SharedCockpitRadioManager exists (only needed for multi-crew)
            if (SharedCockpitRadioManager.Instance == null)
            {
                GameObject managerObj = new GameObject("SharedCockpitRadioManager");
                managerObj.AddComponent<SharedCockpitRadioManager>();
                SharedMusicPlayer.Logger.Log("Created SharedCockpitRadioManager", "CockpitRadioRedirectPatch");
            }
            else
            {
                // Manager already exists - re-initialize it for the new vehicle
                // This is important when switching from single-crew to multi-crew
                SharedCockpitRadioManager.Instance.ReinitializeForNewVehicle();
            }
        }

        // Redirect PlayButton to manager
        [HarmonyPatch(typeof(CockpitRadio), "PlayButton")]
        [HarmonyPrefix]
        public static bool PlayButton_Prefix(CockpitRadio __instance)
        {
            // If not in multi-crew mode, let original logic run (single-crew vehicle)
            if (!IsMultiCrewMode())
            {
                return true;
            }

            if (isRemoteCall)
            {
                // Allow original to run for remote calls (RPCs)
                // Clear the just-stopped flag for remote calls since they're intentional
                _justStopped = false;
                return true;
            }

            // If we just stopped, ignore this PlayButton call to prevent restarting
            if (_justStopped)
            {
                SharedMusicPlayer.Logger.Log(
                    "PlayButton_Prefix: Just stopped, ignoring PlayButton call to prevent restart",
                    "CockpitRadioRedirectPatch");
                _justStopped = false; // Clear the flag
                return false; // Prevent original from running
            }

            // Check if music is currently playing (has clip and not paused)
            // If so, treat this as a STOP command instead of just pause
            // This ensures both players stop when one presses the stop/play button
            bool isCurrentlyPlaying = false;
            bool isPaused = false;
            try
            {
                var audioSourceField = typeof(CockpitRadio).GetField("audioSource", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var pausedField = typeof(CockpitRadio).GetField("paused", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (audioSourceField != null && pausedField != null)
                {
                    var audioSource = audioSourceField.GetValue(__instance) as UnityEngine.AudioSource;
                    isPaused = (bool)pausedField.GetValue(__instance);
                    
                    if (audioSource != null && audioSource.clip != null)
                    {
                        // Music is currently playing if there's a clip and it's not paused
                        isCurrentlyPlaying = !isPaused && audioSource.isPlaying;
                    }
                }
            }
            catch { }

            // Redirect to manager using command system
            if (SharedCockpitRadioManager.Instance != null)
            {
                // If music is currently playing (not paused), pressing the button should STOP it
                // This ensures both players stop when one presses the stop/play button
                if (isCurrentlyPlaying)
                {
                    SharedMusicPlayer.Logger.Log(
                        "PlayButton_Prefix: Music is currently playing, treating as STOP command",
                        "CockpitRadioRedirectPatch");
                    _justStopped = true; // Set flag to prevent immediate restart
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Stop);
                }
                else
                {
                    // Music is paused or not playing, so this is a play/unpause command
                    SharedMusicPlayer.Logger.Log(
                        $"PlayButton_Prefix: Music is paused or not playing (isPaused={isPaused}), treating as PlayToggle command",
                        "CockpitRadioRedirectPatch");
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.PlayToggle);
                }
                return false; // Prevent original from running
            }

            // Fallback: allow original if manager not ready
            return true;
        }

        // Redirect PrevSong to manager
        [HarmonyPatch(typeof(CockpitRadio), "PrevSong")]
        [HarmonyPrefix]
        public static bool PrevSong_Prefix(CockpitRadio __instance)
        {
            // If not in multi-crew mode, let original logic run (single-crew vehicle)
            if (!IsMultiCrewMode())
            {
                return true;
            }

            if (isRemoteCall)
            {
                // Allow original to run for remote calls (RPCs)
                return true;
            }

            // Redirect to manager using command system
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Prev);
                return false; // Prevent original from running
            }

            // Fallback: allow original if manager not ready
            return true;
        }

        // Flag to track if NextSong is being called as part of auto-advance
        // Set by StreamSongRoutine patch before calling NextSong()
        private static bool _isAutoAdvanceNext = false;
        
        // Public getter for logging (backward compatibility)
        public static bool IsAutoAdvanceStop => _isAutoAdvanceNext;

        // Flag to prevent PlayButton from restarting immediately after Stop
        private static bool _justStopped = false;

        // Redirect StopPlayingSong to manager
        [HarmonyPatch(typeof(CockpitRadio), "StopPlayingSong")]
        [HarmonyPrefix]
        public static bool StopPlayingSong_Prefix(CockpitRadio __instance)
        {
            // If not in multi-crew mode, let original logic run (single-crew vehicle)
            if (!IsMultiCrewMode())
            {
                return true;
            }

            bool streamFinished = false;
            try
            {
                var streamFinishedField = typeof(CockpitRadio).GetField("streamFinished", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (streamFinishedField != null)
                {
                    streamFinished = (bool)streamFinishedField.GetValue(__instance);
                }
            }
            catch { }

            SharedMusicPlayer.Logger.Log(
                $"StopPlayingSong_Prefix: isRemoteCall={isRemoteCall}, streamFinished={streamFinished}",
                "CockpitRadioRedirectPatch");

            if (isRemoteCall)
            {
                // Allow original to run for remote calls (RPCs and internal calls from manager)
                SharedMusicPlayer.Logger.Log(
                    "StopPlayingSong_Prefix: isRemoteCall=true, allowing original to run",
                    "CockpitRadioRedirectPatch");
                return true;
            }

            // If this is an auto-advance stop (streamFinished=true), set the flag for NextSong
            // The StreamSongRoutine will then call NextSong(), which will check this flag
            if (streamFinished)
            {
                _isAutoAdvanceNext = true;
                SharedMusicPlayer.Logger.Log(
                    "StopPlayingSong_Prefix: Auto-advance stop detected (streamFinished=true), set _isAutoAdvanceNext=true, allowing direct execution",
                    "CockpitRadioRedirectPatch");
                return true; // Let it execute directly, NextSong_Prefix will handle the auto-advance
            }

            // Manual stop - redirect to manager
            SharedMusicPlayer.Logger.Log(
                "StopPlayingSong_Prefix: Manual stop detected, redirecting to manager",
                "CockpitRadioRedirectPatch");

            if (SharedCockpitRadioManager.Instance != null)
            {
                // Execute the stop command - this will handle local execution and broadcasting
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Stop);
                return false; // Prevent original from running (manager will execute it with isRemoteCall=true)
            }

            // Fallback: allow original if manager not ready
            SharedMusicPlayer.Logger.Log("StopPlayingSong_Prefix: Manager not ready, allowing original", "CockpitRadioRedirectPatch");
            return true;
        }

        // Redirect NextSong to manager
        [HarmonyPatch(typeof(CockpitRadio), "NextSong")]
        [HarmonyPrefix]
        public static bool NextSong_Prefix(CockpitRadio __instance)
        {
            // If not in multi-crew mode, let original logic run (single-crew vehicle)
            // This includes auto-advance functionality
            if (!IsMultiCrewMode())
            {
                // Clear auto-advance flag to avoid state issues
                _isAutoAdvanceNext = false;
                return true;
            }

            SharedMusicPlayer.Logger.Log(
                $"NextSong_Prefix: isRemoteCall={isRemoteCall}, _isAutoAdvanceNext={_isAutoAdvanceNext}",
                "CockpitRadioRedirectPatch");

            if (isRemoteCall)
            {
                // Allow original to run for remote calls (RPCs and internal calls from manager)
                // Clear the auto-advance flag since this is actual execution
                bool wasAutoAdvance = _isAutoAdvanceNext;
                _isAutoAdvanceNext = false;
                SharedMusicPlayer.Logger.Log(
                    $"NextSong_Prefix: isRemoteCall=true, wasAutoAdvance={wasAutoAdvance}, cleared flag, allowing original to run",
                    "CockpitRadioRedirectPatch");
                return true;
            }

            // Check if this is auto-advance (set by StreamSongRoutine patch)
            bool isAutoAdvance = _isAutoAdvanceNext;
            _isAutoAdvanceNext = false; // Clear immediately after checking

            // Check ownership
            bool isOwner = true;
            try
            {
                var scene = UnityEngine.Object.FindAnyObjectByType<VTOLVR.Multiplayer.VTOLMPSceneManager>();
                if (scene != null)
                {
                    var mySlot = scene.GetSlot(scene.localPlayer);
                    if (mySlot != null)
                    {
                        var ownerSlot = scene.GetMCOwnerSlot(mySlot);
                        isOwner = (ownerSlot == mySlot);
                    }
                }
            }
            catch { }

            SharedMusicPlayer.Logger.Log(
                $"NextSong_Prefix: isAutoAdvance={isAutoAdvance}, isOwner={isOwner}",
                "CockpitRadioRedirectPatch");

            // If auto-advance and non-owner, suppress it (owner will broadcast)
            if (isAutoAdvance && !isOwner)
            {
                SharedMusicPlayer.Logger.Log("NextSong_Prefix: Suppressing non-owner auto-advance", "CockpitRadioRedirectPatch");
                return false; // Prevent auto-advance, wait for owner's broadcast
            }

            // Redirect to manager (handles both manual and auto-advance)
            if (SharedCockpitRadioManager.Instance != null)
            {
                if (isAutoAdvance)
                {
                    SharedMusicPlayer.Logger.Log("NextSong_Prefix: Owner auto-advance detected, redirecting to manager", "CockpitRadioRedirectPatch");
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Next, isAutoAdvance: true);
                }
                else
                {
                    SharedMusicPlayer.Logger.Log("NextSong_Prefix: Manual NextSong call, redirecting to manager", "CockpitRadioRedirectPatch");
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Next);
                }
                return false; // Prevent original from running
            }

            // Fallback: allow original if manager not ready
            return true;
        }

        // Patch StreamSongRoutine to track when it's running
        // This helps us detect auto-advance context
        [HarmonyPatch(typeof(CockpitRadio), "StreamSongRoutine")]
        [HarmonyPostfix]
        public static void StreamSongRoutine_Postfix(CockpitRadio __instance)
        {
            // This postfix runs after the coroutine completes
            // The flag should already be set by StopPlayingSong_Prefix when streamFinished=true
            // But we can use this to verify the coroutine context if needed
            SharedMusicPlayer.Logger.Log("StreamSongRoutine_Postfix: Coroutine completed", "CockpitRadioRedirectPatch");
        }
    }
}
