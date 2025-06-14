using HarmonyLib;
using SharedMusicPlayer;
using UnityEngine;
using VTOLVR.Multiplayer;
using System.Collections;
using System;
using System.IO;

[HarmonyPatch(typeof(VTOLMPBriefingRoom), "EnterVehicle")]
public static class Patch_EnterVehicle_BlockCopilot
{
    [HarmonyPrefix]
    public static bool Prefix(VTOLMPBriefingRoom __instance)
    {
        var manager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();
        if (manager == null)
        {
            var go = new GameObject("MusicNetworkManager");
            manager = go.AddComponent<MusicNetworkManager>();
            UnityEngine.Object.DontDestroyOnLoad(go);
        }

        var sceneManager = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
        var mySlot = sceneManager.GetSlot(sceneManager.localPlayer);
        ulong? copilotSteamId = GetSecondPilotID(mySlot);

        if (!__instance.IsInCopilotSlot() && copilotSteamId != null)
        {
            Debug.Log("[SharedMusicPlayer]: I am pilot - starting music file send.");
            string[] musicFiles = Array.Empty<string>();
            try
            {
                musicFiles = Directory.GetFiles(Path.GetFullPath(GameSettings.RADIO_MUSIC_PATH));
            }
            catch (Exception ex)
            {
                Debug.LogError("[SharedMusicPlayer]: Failed to read music files: " + ex);
            }

            manager.StartSender((ulong)copilotSteamId, musicFiles);
        }
        if (__instance.IsInCopilotSlot() && !SharedMusicState.IsCopilotReadyToEnter)
        {
            Debug.Log("[SharedMusicPlayer]: Blocking EnterVehicle() for copilot — waiting for music files");

            if (copilotSteamId != null)
            {
                __instance.StartCoroutine(WaitThenEnter(__instance, (ulong)copilotSteamId));
            }

            return false;
        }

        return true;
    }

    private static ulong? GetSecondPilotID(VTOLMPSceneManager.VehicleSlot mySlot)
    {
        if (mySlot == null) return null;

        var sceneManager = UnityEngine.Object.FindAnyObjectByType<VTOLMPSceneManager>();
        var mcBaseSlot = sceneManager.GetMCBaseSlot(mySlot);

        for (int i = 0; i < mcBaseSlot.mcSlotCount; i++)
        {
            var slot = sceneManager.GetSlot(mcBaseSlot.slotID + i);
            if (slot.player != null && slot.player != mySlot.player)
                return slot.player.steamUser.Id;
        }

        return null;
    }

    private static IEnumerator WaitThenEnter(VTOLMPBriefingRoom instance, ulong steamId)
    {
        var manager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();

        Debug.Log("[SharedMusicPlayer]: Coroutine started - waiting for file reception...");
        yield return manager.BeginReceiveAndWaitCoroutine(steamId);

        Debug.Log("[SharedMusicPlayer]: Files received - entering vehicle");
        SharedMusicState.IsCopilotReadyToEnter = true;

        instance.EnterVehicle();
    }

    public static class SharedMusicState
    {
        public static bool IsCopilotReadyToEnter = false;
    }

}