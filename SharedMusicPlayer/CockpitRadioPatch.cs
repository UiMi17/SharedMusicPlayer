using System.IO;
using System;
using HarmonyLib;
using UnityEngine;
using VTOLVR.Multiplayer;
using VTNetworking;
using static VTNetworking.VTNetworkManager;
using System.Collections;

namespace VtolVRMod
{
    [HarmonyPatch]
    public class CockpitRadioPatch
    {
        public static RadioNetSync radioNetSync;
        public static bool isRemoteCall = false;

        [HarmonyPatch(typeof(CockpitRadio), "Start")]
        [HarmonyPrefix]
        public static bool Start_Prefix(CockpitRadio __instance)
        {
            var muvs = UnityEngine.Object.FindObjectOfType<MultiUserVehicleSync>();
            bool isCopilot = muvs.UserSeatIdx(BDSteamClient.mySteamID) > 0;

            string text = GameSettings.RADIO_MUSIC_PATH;

            if (isCopilot)
            {
                Debug.Log("[HarmonyPatch] I am in copilot seat");
                text = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
                Directory.CreateDirectory(text);
            }

            if (!Directory.Exists(text))
            {
                Debug.LogError("Cockpit radio song folder path not found: " + text + ". Using default path.");
                text = GameSettings.defaultRadioMusicPath;
                if (!Directory.Exists(text))
                {
                    Debug.LogError("Cockpit radio default song folder path not found: " + text + ". Disabling cockpit radio.");
                    try
                    {
                        Directory.CreateDirectory(text);
                        Debug.Log("Cockpit radio created the default song folder for future use.");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("Exception when trying to create the default cockpit radio folder: \n" + ex);
                        return false;
                    }
                }
            }

            string[] files = Directory.GetFiles(Path.GetFullPath(text));
            foreach (string text2 in files)
            {
                if (text2.EndsWith(".mp3"))
                {
                    __instance.shuffledSongs.Add(text2);
                    __instance.origSongs.Add(text2);
                }
            }

            return false;
        }

        [HarmonyPatch(typeof(CockpitRadio), "Awake")]
        [HarmonyPostfix]
        public static void Awake_Postfix(CockpitRadio __instance)
        {
            __instance.StartCoroutine(WaitForRadioNetSync(__instance));
        }

        private static IEnumerator WaitForRadioNetSync(CockpitRadio __instance)
        {
            Debug.Log("[CockpitRadioPatch] Waiting for RadioNetSync to be ready...");

            NetInstantiateRequest request = VTNetworkManager.NetInstantiate(
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
                radioNetSync = request.obj.GetComponent<RadioNetSync>();
                Debug.Log("[CockpitRadioPatch] RadioNetSync initialized via NetInstantiate.");
            }
            else
            {
                radioNetSync = UnityEngine.Object.FindAnyObjectByType<RadioNetSync>();
                if (radioNetSync != null)
                {
                    Debug.Log("[CockpitRadioPatch] Fallback: RadioNetSync found in scene.");
                }
                else
                {
                    Debug.LogWarning("[CockpitRadioPatch] Failed to initialize RadioNetSync — will remain null.");
                }
            }
        }

        [HarmonyPatch(typeof(CockpitRadio), "PlayButton")]
        [HarmonyPrefix]
        public static bool PlayButton_Prefix(CockpitRadio __instance)
        {
            if (isRemoteCall)
            {
                Debug.Log("[HarmonyPatch] PlayButton was called remotely. Skipping RPC.");
                return true;
            }

            Log("[HarmonyPatch] SharedRadioController.PlayButton called");

            MultiUserVehicleSync muvs = UnityEngine.Object.FindObjectOfType<MultiUserVehicleSync>();
            ulong? copilotID = null;
            for (int i = 0; i < muvs.seatCount; i++)
            {
                ulong occupantID = muvs.GetOccupantID(i);
                if (occupantID != 0UL && occupantID != BDSteamClient.mySteamID)
                {
                    copilotID = occupantID;
                }
            }

            Debug.Log($"[HarmonyPatch] CopilotID = {copilotID}");

            if (copilotID != null && radioNetSync != null)
            {
                Debug.Log("[CockpitRadioPatch]: OnLocalPlay");
                radioNetSync.OnLocalPlay((ulong)copilotID, __instance.songIdx, !__instance.paused);
            }

            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "NextSong")]
        [HarmonyPrefix]
        public static bool NextSong_Prefix(CockpitRadio __instance)
        {
            if (isRemoteCall)
            {
                Debug.Log("[HarmonyPatch] PlayButton was called remotely. Skipping RPC.");
                return true;
            }

            Debug.Log("[HarmonyPatch] NextSong called");

            MultiUserVehicleSync muvs = UnityEngine.Object.FindObjectOfType<MultiUserVehicleSync>();
            ulong? copilotID = null;
            for (int i = 0; i < muvs.seatCount; i++)
            {
                ulong occupantID = muvs.GetOccupantID(i);
                if (occupantID != 0UL && occupantID != BDSteamClient.mySteamID)
                {
                    copilotID = occupantID;
                }
            }

            Debug.Log($"[HarmonyPatch] CopilotID = {copilotID}");

            if (copilotID != null && radioNetSync != null)
            {
                radioNetSync.OnLocalNextSong((ulong)copilotID);
            }
            return true;
        }

        [HarmonyPatch(typeof(CockpitRadio), "PrevSong")]
        [HarmonyPrefix]
        public static bool PrevSong_Prefix(CockpitRadio __instance)
        {
            if (isRemoteCall)
            {
                Debug.Log("[HarmonyPatch] PlayButton was called remotely. Skipping RPC.");
                return true;
            }

            Debug.Log("[HarmonyPatch] PrevSong called");

            MultiUserVehicleSync muvs = UnityEngine.Object.FindObjectOfType<MultiUserVehicleSync>();
            ulong? copilotID = null;
            for (int i = 0; i < muvs.seatCount; i++)
            {
                ulong occupantID = muvs.GetOccupantID(i);
                if (occupantID != 0UL && occupantID != BDSteamClient.mySteamID)
                {
                    copilotID = occupantID;
                }
            }

            Debug.Log($"[HarmonyPatch] CopilotID = {copilotID}");

            if (copilotID != null && radioNetSync != null)
            {
                radioNetSync.OnLocalPrevSong((ulong)copilotID);
            }
            return true;
        }
    }
}