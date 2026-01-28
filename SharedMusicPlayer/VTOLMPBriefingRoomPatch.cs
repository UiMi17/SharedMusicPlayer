using HarmonyLib;
using UnityEngine;
using VTOLVR.Multiplayer;
using System.Collections;
using System;
using System.IO;
using UnityEngine.UI;
using TMPro;

namespace SharedMusicPlayer
{
    [HarmonyPatch(typeof(VTOLMPBriefingRoom), "EnterVehicle")]
    public static class Patch_EnterVehicle_BlockCopilot
{
    private static bool _isEnteringVehicle = false;
    private static float _enterVehicleStartTime = -1f;
    private const float ENTER_VEHICLE_TIMEOUT = 60f; // 60 seconds timeout

    [HarmonyPrefix]
    public static bool Prefix(VTOLMPBriefingRoom __instance)
    {
		Logger.Log("EnterVehicle called", "VTOLMPBriefingRoomPatch");
		
		// Check if we're already in the process of entering vehicle
		if (_isEnteringVehicle)
		{
			// Check for timeout - reset if stuck
			if (Time.realtimeSinceStartup - _enterVehicleStartTime > ENTER_VEHICLE_TIMEOUT)
			{
				Logger.LogWarn("EnterVehicle timeout detected, resetting flag", "VTOLMPBriefingRoomPatch");
				_isEnteringVehicle = false;
			}
			else
			{
				Logger.LogDebug("EnterVehicle already in progress, ignoring duplicate call", "VTOLMPBriefingRoomPatch");
				return false; // Block duplicate call
			}
		}
		var manager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();
		if (manager == null)
		{
			Logger.Log("Creating MusicNetworkManager...", "VTOLMPBriefingRoomPatch");
			var go = new GameObject("MusicNetworkManager");
			manager = go.AddComponent<MusicNetworkManager>();
			UnityEngine.Object.DontDestroyOnLoad(go);
			Logger.Log("MusicNetworkManager created", "VTOLMPBriefingRoomPatch");
		}

		var sceneManager = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
		if (sceneManager == null)
		{
			Logger.LogWarn("VTOLMPSceneManager not found; allowing EnterVehicle.", "VTOLMPBriefingRoomPatch");
			Debug.LogWarning("[SharedMusicPlayer]: VTOLMPSceneManager not found; allowing EnterVehicle.");
			return true;
		}

		var mySlot = sceneManager.GetSlot(sceneManager.localPlayer);
		if (mySlot == null)
		{
			Logger.LogWarn("My slot not found; allowing EnterVehicle.", "VTOLMPBriefingRoomPatch");
			Debug.LogWarning("[SharedMusicPlayer]: My slot not found; allowing EnterVehicle.");
			return true;
		}

		var ownerSlot = sceneManager.GetMCOwnerSlot(mySlot);
		bool amOwner = ownerSlot == mySlot;

		SharedMusicState.LibraryOwnerSteamId = ownerSlot != null && ownerSlot.player != null ? (ulong?)ownerSlot.player.steamUser.Id : null;
		Logger.Log($"Ownership check: amOwner={amOwner}, ownerSteamId={SharedMusicState.LibraryOwnerSteamId}", "VTOLMPBriefingRoomPatch");

		ulong? otherCrewId = GetOtherCrewID(sceneManager, mySlot);

		if (amOwner)
		{
			try
			{
				string[] musicFiles = Array.Empty<string>();
				musicFiles = Directory.GetFiles(Path.GetFullPath(GameSettings.RADIO_MUSIC_PATH));
				Array.Sort(musicFiles, StringComparer.OrdinalIgnoreCase);
				Logger.Log($"Owner: Found {musicFiles.Length} music file(s) to send", "VTOLMPBriefingRoomPatch");

				if (otherCrewId != null)
				{
					Logger.Log($"I am owner — starting music file send to {otherCrewId}.", "VTOLMPBriefingRoomPatch");
					Debug.Log($"[SharedMusicPlayer]: I am owner — starting music file send to {otherCrewId}.");
					manager.StartSender((ulong)otherCrewId, musicFiles);
				}
				else
				{
					Logger.Log("I am owner — other crew not present. Will watch and send on join.", "VTOLMPBriefingRoomPatch");
					Debug.Log("[SharedMusicPlayer]: I am owner — other crew not present. Will watch and send on join.");
					manager.StartOwnerWatcher(musicFiles);
				}
			}
			catch (Exception ex)
			{
				Logger.LogError($"Failed to prepare music files for sending: {ex}", "VTOLMPBriefingRoomPatch");
				Debug.LogError("[SharedMusicPlayer]: Failed to prepare music files for sending: " + ex);
			}

			return true;
		}
		else
		{
			// Check if download was cancelled - block entry even if ready flag is set
			if (SharedMusicState.IsDownloadCancelled)
			{
				Logger.Log("Download was cancelled, blocking vehicle entry", "VTOLMPBriefingRoomPatch");
				Debug.Log("[SharedMusicPlayer]: Download was cancelled, blocking vehicle entry");
				return false;
			}

			if (!SharedMusicState.IsCopilotReadyToEnter)
			{
				ulong? ownerSteamId = SharedMusicState.LibraryOwnerSteamId;
				if (ownerSteamId == null && ownerSlot != null && ownerSlot.player != null)
				{
					ownerSteamId = ownerSlot.player.steamUser.Id;
				}

				if (ownerSteamId != null)
				{
					// Set flag to prevent multiple coroutines
					if (!_isEnteringVehicle)
					{
						_isEnteringVehicle = true;
						_enterVehicleStartTime = Time.realtimeSinceStartup;
						Logger.Log($"Blocking EnterVehicle() for non-owner — waiting for music files from owner {ownerSteamId}", "VTOLMPBriefingRoomPatch");
						Debug.Log("[SharedMusicPlayer]: Blocking EnterVehicle() for non-owner — waiting for music files from owner");
						_waitThenEnterCoroutine = __instance.StartCoroutine(WaitThenEnter(__instance, (ulong)ownerSteamId));
						return false;
					}
					else
					{
						Logger.LogDebug("EnterVehicle coroutine already running, ignoring duplicate call", "VTOLMPBriefingRoomPatch");
						return false; // Block duplicate call
					}
				}
				else
				{
					// No owner identified - allow entry (might be single-player or owner not yet available)
					Logger.LogWarn("Non-owner: No owner SteamID found, allowing entry without file transfer", "VTOLMPBriefingRoomPatch");
					Debug.LogWarning("[SharedMusicPlayer]: Non-owner: No owner SteamID found, allowing entry without file transfer");
					return true;
				}
			}

			Logger.Log($"Non-owner: IsCopilotReadyToEnter={SharedMusicState.IsCopilotReadyToEnter}", "VTOLMPBriefingRoomPatch");
			return SharedMusicState.IsCopilotReadyToEnter;
		}
    }

	private static ulong? GetOtherCrewID(VTOLMPSceneManager sceneManager, VTOLMPSceneManager.VehicleSlot mySlot)
	{
		if (mySlot == null) return null;
		var mcBaseSlot = sceneManager.GetMCBaseSlot(mySlot);
		for (int i = 0; i < mcBaseSlot.mcSlotCount; i++)
		{
			var slot = sceneManager.GetSlot(mcBaseSlot.slotID + i);
			if (slot.player != null && slot.player != mySlot.player)
				return slot.player.steamUser.Id;
		}
		return null;
	}

    private static Coroutine _waitThenEnterCoroutine = null;

    public static void StopWaitThenEnterCoroutine()
    {
        if (_waitThenEnterCoroutine != null)
        {
            var briefingRoom = UnityEngine.Object.FindObjectOfType<VTOLMPBriefingRoom>();
            if (briefingRoom != null)
            {
                briefingRoom.StopCoroutine(_waitThenEnterCoroutine);
                _waitThenEnterCoroutine = null;
                Logger.Log("Stopped WaitThenEnter coroutine", "VTOLMPBriefingRoomPatch");
            }
        }
    }

    public static void ResetEnteringVehicleState()
    {
        _isEnteringVehicle = false;
        _enterVehicleStartTime = -1f;
        Logger.Log("Reset entering vehicle state", "VTOLMPBriefingRoomPatch");
    }

    private static IEnumerator WaitThenEnter(VTOLMPBriefingRoom instance, ulong steamId)
    {
        var manager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();
        if (manager == null)
        {
            Logger.LogError("MusicNetworkManager not found in WaitThenEnter", "VTOLMPBriefingRoomPatch");
            _isEnteringVehicle = false;
            _enterVehicleStartTime = -1f;
            yield break;
        }

        Logger.Log($"Coroutine started - waiting for file reception from {steamId}...", "VTOLMPBriefingRoomPatch");
        Debug.Log("[SharedMusicPlayer]: Coroutine started - waiting for file reception...");
        
        // Yield cannot be inside try-catch, so we handle errors after yield completes
        // Note: BeginReceiveAndWaitCoroutine will set _receiveCoroutine internally
        yield return manager.BeginReceiveAndWaitCoroutine(steamId);

        // Check if coroutine completed successfully (manager might have been destroyed)
        if (manager == null || instance == null)
        {
            Logger.LogWarn("Manager or instance destroyed during wait, aborting EnterVehicle", "VTOLMPBriefingRoomPatch");
            _isEnteringVehicle = false;
            _enterVehicleStartTime = -1f;
            _waitThenEnterCoroutine = null;
            yield break;
        }

        // Check if download was cancelled
        if (SharedMusicState.IsDownloadCancelled)
        {
            Logger.Log("Download was cancelled, not entering vehicle", "VTOLMPBriefingRoomPatch");
            Debug.Log("[SharedMusicPlayer]: Download was cancelled, not entering vehicle");
            _isEnteringVehicle = false;
            _enterVehicleStartTime = -1f;
            _waitThenEnterCoroutine = null;
            yield break;
        }

        try
        {
            Logger.Log("Files received - entering vehicle", "VTOLMPBriefingRoomPatch");
            Debug.Log("[SharedMusicPlayer]: Files received - entering vehicle");
            SharedMusicState.IsCopilotReadyToEnter = true;

            // Reset flag before calling EnterVehicle (which may trigger the patch again)
            _isEnteringVehicle = false;
            _enterVehicleStartTime = -1f;
            _waitThenEnterCoroutine = null;

            instance.EnterVehicle();
        }
        catch (Exception ex)
        {
            Logger.LogError($"WaitThenEnter failed after file reception: {ex}", "VTOLMPBriefingRoomPatch");
            _isEnteringVehicle = false;
            _enterVehicleStartTime = -1f;
            _waitThenEnterCoroutine = null;
        }
    }
}

// Button creation removed from Awake - now created dynamically in BeginReceiveAndWaitCoroutine when modal is shown

    public static class SharedMusicState
    {
        public static bool IsCopilotReadyToEnter = false;
        public static ulong? LibraryOwnerSteamId = null;
        public static bool IsDownloadCancelled = false;
    }
}