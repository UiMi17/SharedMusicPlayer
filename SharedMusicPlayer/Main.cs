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
            Log($"Awake at {ModFolder}");

            // Just to be sure... Otherwise, sockets are fucked. 
            if (!SteamClient.IsValid)
            {
                SteamClient.Init(480, true);
            }

            SteamNetworkingUtils.InitRelayNetworkAccess();

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


            harmony.PatchAll();
        }

        public override void UnLoad()
        {
            var musicManager = UnityEngine.Object.FindObjectOfType<MusicNetworkManager>();
            if (musicManager != null)
            {
                musicManager.StopAll();
            }
            var radioManager = UnityEngine.Object.FindObjectOfType<RadioNetworkManager>();
            if (radioManager != null)
            {
                radioManager.StopAll();
            }

            string sharedMusicDir = Path.Combine(VTResources.gameRootDirectory, "SharedRadioMusic");
            if (Directory.Exists(sharedMusicDir))
            {
                Directory.Delete(sharedMusicDir, true);
            }

            harmony.UnpatchSelf();
        }
    }
}