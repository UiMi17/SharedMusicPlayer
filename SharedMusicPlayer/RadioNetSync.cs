using UnityEngine;
using VTNetworking;
using VtolVRMod;
using VTOLVR.Multiplayer;

namespace SharedMusicPlayer
{
    public class RadioNetSync : VTNetSync
    {
        public override void OnNetInitialized()
        {
            base.OnNetInitialized();
        }

        /// <summary>
        /// Send play/pause toggle command to other crew member
        /// </summary>
        public void SendPlayToggle(ulong otherCrewId)
        {
            Logger.Log($"Sending PlayToggle to {otherCrewId}", "RadioNetSync");
            SendDirectedRPC(otherCrewId, "RPC_PlayToggle");
        }

        /// <summary>
        /// Send next song command to other crew member
        /// </summary>
        public void SendNext(ulong otherCrewId)
        {
            Logger.Log($"Sending Next to {otherCrewId}", "RadioNetSync");
            SendDirectedRPC(otherCrewId, "RPC_Next");
        }

        /// <summary>
        /// Send previous song command to other crew member
        /// </summary>
        public void SendPrev(ulong otherCrewId)
        {
            Logger.Log($"Sending Prev to {otherCrewId}", "RadioNetSync");
            SendDirectedRPC(otherCrewId, "RPC_Prev");
        }

        /// <summary>
        /// Send stop command to other crew member
        /// </summary>
        public void SendStop(ulong otherCrewId)
        {
            Logger.Log($"Sending Stop to {otherCrewId}", "RadioNetSync");
            SendDirectedRPC(otherCrewId, "RPC_Stop");
        }

        [VTRPC]
        private void RPC_PlayToggle()
        {
            Logger.Log("RPC_PlayToggle received", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.PlayToggle, isRemoteCall: true);
            }
            else
            {
                Logger.LogWarn("PlayToggle received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        [VTRPC]
        private void RPC_Next()
        {
            Logger.Log("RPC_Next received", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Next, isRemoteCall: true);
            }
            else
            {
                Logger.LogWarn("Next received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        [VTRPC]
        private void RPC_Prev()
        {
            Logger.Log("RPC_Prev received", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Prev, isRemoteCall: true);
            }
            else
            {
                Logger.LogWarn("Prev received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        [VTRPC]
        private void RPC_Stop()
        {
            Logger.Log("RPC_Stop received", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.ExecuteCommand(RadioCommand.Stop, isRemoteCall: true);
            }
            else
            {
                Logger.LogWarn("Stop received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        /// <summary>
        /// Request current music state from the other crew member (used when copilot joins)
        /// </summary>
        public void RequestState(ulong otherCrewId)
        {
            Logger.Log($"Requesting music state from {otherCrewId}", "RadioNetSync");
            SendDirectedRPC(otherCrewId, "RPC_RequestState");
        }

        /// <summary>
        /// Send current music state to the other crew member
        /// Note: VTNetworking doesn't support bool parameters, so we use int (0/1) instead
        /// </summary>
        public void SendState(ulong otherCrewId, int songIndex, bool isPlaying, bool isPaused)
        {
            Logger.Log($"Sending music state to {otherCrewId}: songIndex={songIndex}, isPlaying={isPlaying}, isPaused={isPaused}", "RadioNetSync");
            // Convert bool to int (0/1) because VTNetworking doesn't support bool parameters
            int isPlayingInt = isPlaying ? 1 : 0;
            int isPausedInt = isPaused ? 1 : 0;
            SendDirectedRPC(otherCrewId, "RPC_State", songIndex, isPlayingInt, isPausedInt);
        }

        [VTRPC]
        private void RPC_RequestState()
        {
            Logger.Log("RPC_RequestState received", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.HandleStateRequest();
            }
            else
            {
                Logger.LogWarn("RequestState received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        [VTRPC]
        private void RPC_State(int songIndex, int isPlayingInt, int isPausedInt)
        {
            // Convert int back to bool (VTNetworking doesn't support bool parameters)
            bool isPlaying = isPlayingInt != 0;
            bool isPaused = isPausedInt != 0;
            Logger.Log($"RPC_State received: songIndex={songIndex}, isPlaying={isPlaying}, isPaused={isPaused}", "RadioNetSync");
            if (SharedCockpitRadioManager.Instance != null)
            {
                SharedCockpitRadioManager.Instance.HandleStateSync(songIndex, isPlaying, isPaused);
            }
            else
            {
                Logger.LogWarn("State received but SharedCockpitRadioManager not found", "RadioNetSync");
            }
        }

        /// <summary>
        /// Helper to get the other crew member's Steam ID
        /// </summary>
        public static ulong? GetOtherCrewId(VTOLMPSceneManager scene, VTOLMPSceneManager.VehicleSlot mySlot)
        {
            var baseSlot = scene.GetMCBaseSlot(mySlot);
            for (int i = 0; i < baseSlot.mcSlotCount; i++)
            {
                var s = scene.GetSlot(baseSlot.slotID + i);
                if (s != null && s.player != null && s.player != mySlot.player)
                    return s.player.steamUser.Id;
            }
            return null;
        }
    }
}
