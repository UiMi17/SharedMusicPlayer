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

                ulong? otherCrewId = RadioNetSync.GetOtherCrewId(scene, mySlot);
                if (otherCrewId == null)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                // On error, assume single-crew to avoid breaking things
                return false;
            }
        }

        [HarmonyPatch(typeof(CockpitRadio), "Start")]
        [HarmonyPrefix]
        public static bool Start_Prefix(CockpitRadio __instance)
        {
            if (IsMultiCrewMode())
            {
                SharedRadioController.Instance?.EnsurePlaylist();
                return false;
            }
            
            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(CockpitRadio __instance)
        {
            _justStopped = false;
            _isAutoAdvanceNext = false;
            
            if (SharedCockpitRadioManager.Instance == null)
            {
                GameObject managerObj = new GameObject("SharedCockpitRadioManager");
                managerObj.AddComponent<SharedCockpitRadioManager>();
                SharedMusicPlayer.Logger.Log("Created SharedCockpitRadioManager", "CockpitRadioRedirectPatch");
            }
            else
            {
                SharedCockpitRadioManager.Instance.ReinitializeForNewVehicle();
            }
        }

        [HarmonyPatch(typeof(CockpitRadio), "PlayButton")]
        [HarmonyPrefix]
        public static bool PlayButton_Prefix(CockpitRadio __instance)
        {
            if (!IsMultiCrewMode())
            {
                return true;
            }

            if (isRemoteCall)
            {
                _justStopped = false;
                return true;
            }

            if (_justStopped)
            {
                SharedMusicPlayer.Logger.Log(
                    "PlayButton_Prefix: Just stopped, ignoring PlayButton call to prevent restart",
                    "CockpitRadioRedirectPatch");
                _justStopped = false;
                return false;
            }

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
                        isCurrentlyPlaying = !isPaused && audioSource.isPlaying;
                    }
                }
            }
            catch { }

            if (SharedCockpitRadioManager.Instance != null)
            {
                if (isCurrentlyPlaying)
                {
                    SharedMusicPlayer.Logger.Log(
                        "PlayButton_Prefix: Music is currently playing, treating as STOP command",
                        "CockpitRadioRedirectPatch");
                    _justStopped = true;
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Stop);
                }
                else
                {
                    SharedMusicPlayer.Logger.Log(
                        $"PlayButton_Prefix: Music is paused or not playing (isPaused={isPaused}), treating as PlayToggle command",
                        "CockpitRadioRedirectPatch");
                    SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.PlayToggle);
                }
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "PrevSong")]
        [HarmonyPrefix]
        public static bool PrevSong_Prefix(CockpitRadio __instance)
        {
            if (!IsMultiCrewMode())
            {
                return true;
            }

            if (isRemoteCall)
            {
                return true;
            }

            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Prev);
                return false;
            }

            return true;
        }

        private static bool _isAutoAdvanceNext = false;
        
        public static bool IsAutoAdvanceStop => _isAutoAdvanceNext;

        private static bool _justStopped = false;

        [HarmonyPatch(typeof(CockpitRadio), "StopPlayingSong")]
        [HarmonyPrefix]
        public static bool StopPlayingSong_Prefix(CockpitRadio __instance)
        {
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
                SharedMusicPlayer.Logger.Log(
                    "StopPlayingSong_Prefix: isRemoteCall=true, allowing original to run",
                    "CockpitRadioRedirectPatch");
                return true;
            }

            if (streamFinished)
            {
                _isAutoAdvanceNext = true;
                SharedMusicPlayer.Logger.Log(
                    "StopPlayingSong_Prefix: Auto-advance stop detected (streamFinished=true), set _isAutoAdvanceNext=true, allowing direct execution",
                    "CockpitRadioRedirectPatch");
                return true;
            }

            SharedMusicPlayer.Logger.Log(
                "StopPlayingSong_Prefix: Manual stop detected, redirecting to manager",
                "CockpitRadioRedirectPatch");

            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Stop);
                return false;
            }

            SharedMusicPlayer.Logger.Log("StopPlayingSong_Prefix: Manager not ready, allowing original", "CockpitRadioRedirectPatch");
            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "NextSong")]
        [HarmonyPrefix]
        public static bool NextSong_Prefix(CockpitRadio __instance)
        {
            if (!IsMultiCrewMode())
            {
                _isAutoAdvanceNext = false;
                return true;
            }

            SharedMusicPlayer.Logger.Log(
                $"NextSong_Prefix: isRemoteCall={isRemoteCall}, _isAutoAdvanceNext={_isAutoAdvanceNext}",
                "CockpitRadioRedirectPatch");

            if (isRemoteCall)
            {
                bool wasAutoAdvance = _isAutoAdvanceNext;
                _isAutoAdvanceNext = false;
                SharedMusicPlayer.Logger.Log(
                    $"NextSong_Prefix: isRemoteCall=true, wasAutoAdvance={wasAutoAdvance}, cleared flag, allowing original to run",
                    "CockpitRadioRedirectPatch");
                return true;
            }

            bool isAutoAdvance = _isAutoAdvanceNext;
            _isAutoAdvanceNext = false;

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

            if (isAutoAdvance && !isOwner)
            {
                SharedMusicPlayer.Logger.Log("NextSong_Prefix: Suppressing non-owner auto-advance", "CockpitRadioRedirectPatch");
                return false;
            }

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
                return false;
            }

            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "StreamSongRoutine")]
        [HarmonyPostfix]
        public static void StreamSongRoutine_Postfix(CockpitRadio __instance)
        {
            SharedMusicPlayer.Logger.Log("StreamSongRoutine_Postfix: Coroutine completed", "CockpitRadioRedirectPatch");
        }
    }
}
