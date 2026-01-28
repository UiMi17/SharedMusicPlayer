global using static SharedMusicPlayer.Logger;
using System.IO;
using System.Reflection;
using ModLoader.Framework;
using ModLoader.Framework.Attributes;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using VTNetworking;
using System.Collections.Generic;

namespace SharedMusicPlayer
{
    [ItemId("net.uimi17.sharedmusicplayermod")] 
    public class Main : VtolMod
    {
        public string ModFolder;
        public Harmony harmony = new Harmony("net.uimi17.sharedmusicplayermod");

        private void Awake()
        {
            ModFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            
            GameObject loggerGO = new GameObject("InGameLogger");
            loggerGO.AddComponent<InGameLogger>();
            GameObject.DontDestroyOnLoad(loggerGO);
            
            Log($"Mod initialization started. Mod folder: {ModFolder}");

            // Just to be sure... Otherwise, sockets are fucked. 
            if (!SteamClient.IsValid)
            {
                Log("Steam client not valid, initializing...", "Main");
                SteamClient.Init(480, true);
                Log("Steam client initialized", "Main");
            }
            else
            {
                Log("Steam client already valid", "Main");
            }

            Log("Initializing Steam relay network access...", "Main");
            SteamNetworkingUtils.InitRelayNetworkAccess();
            Log("Steam relay network access initialized", "Main");

            Log("Creating RadioNetSync prefab...", "Main");
            GameObject radioSyncObj = new GameObject("RadioNetSyncPrefab");
            radioSyncObj.SetActive(false); // important before adding components

            VTNetEntity vtEntity = radioSyncObj.AddComponent<VTNetEntity>();
            RadioNetSync radioSync = radioSyncObj.AddComponent<RadioNetSync>();
            vtEntity.netSyncs = new List<VTNetSync> { radioSync };

            // Creating as a prefab
            GameObject prefab = GameObject.Instantiate(radioSyncObj);
            GameObject.DontDestroyOnLoad(prefab);
            prefab.hideFlags = HideFlags.HideAndDontSave;

            // Registering as a prefab
            VTNetworkManager.RegisterOverrideResource("RadioSyncNet/Prefab", prefab);
            Log("RadioNetSync prefab registered successfully", "Main");

            try
            {
                if (SharedRadioController.Instance == null)
                {
                    Log("Creating SharedRadioController...", "Main");
                    GameObject controllerGO = new GameObject("SharedRadioController");
                    controllerGO.AddComponent<SharedRadioController>();
                    GameObject.DontDestroyOnLoad(controllerGO);
                    Log("SharedRadioController created and initialized", "Main");
                }
                else
                {
                    Log("SharedRadioController already exists", "Main");
                }
            }
            catch (System.Exception ex)
            {
                LogError($"Failed to initialize SharedRadioController: {ex}", "Main");
                Debug.LogError($"[SharedMusicPlayer] Failed to initialize SharedRadioController: {ex}");
            }

            Log("Applying Harmony patches...", "Main");
            harmony.PatchAll();
            Log("Mod initialization complete", "Main");
        }

        public override void UnLoad()
        {
            Log("Mod unload started", "Main");
            
            InGameLogger.SaveLogsToFile();
            
            var musicManager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();
            if (musicManager != null)
            {
                Log("Stopping MusicNetworkManager...", "Main");
                musicManager.StopAll();
            }

            string sharedMusicDir = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
            if (Directory.Exists(sharedMusicDir))
            {
                Log($"Cleaning up shared music directory: {sharedMusicDir}", "Main");
                Directory.Delete(sharedMusicDir, true);
            }

            Log("Removing Harmony patches...", "Main");
            harmony.UnpatchSelf();
            Log("Mod unload complete", "Main");
        }
    }
}