using UnityEngine;
using VTNetworking;
using VtolVRMod;

public class RadioNetSync : VTNetSync
{
    public override void OnNetInitialized()
    {

        base.OnNetInitialized();
    }

    public void OnLocalPlay(ulong copilotID, int songIdx, bool paused)
    {
        Debug.Log("[RadioNetSync]: OnLocalPlay");
        int pausedInt = paused ? 1 : 0;
        SendDirectedRPC(copilotID, "RPC_PlaySong", songIdx, pausedInt);
    }

    public void OnLocalNextSong(ulong copilotID)
    {
        Debug.Log("[RadioNetSync]: OnLocalNextSong");
        SendDirectedRPC(copilotID, "RPC_NextSong");
    }

    public void OnLocalPrevSong(ulong copilotID)
    {
        Debug.Log("[RadioNetSync]: OnLocalPrevSong");
        SendDirectedRPC(copilotID, "RPC_PrevSong");
    }

    public void OnLocalStopSong(ulong copilotID)
    {
        Debug.Log("[RadioNetSync]: OnLocalStopSong");
        SendDirectedRPC(copilotID, "RPC_StopSong");
    }

    [VTRPC]
    private void RPC_PlaySong(int songIdx, int pausedInt)
    {
        bool paused = pausedInt != 0;
        Debug.Log($"[RadioNetSync] PlaySong request received - {songIdx}, paused={paused}");
        var cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
        cockpitRadio.songIdx = songIdx;

        CockpitRadioPatch.isRemoteCall = true;

        if ((bool)cockpitRadio.audioSource.clip)
        {
            if (paused)
            {
                cockpitRadio.audioSource.Pause();
                cockpitRadio.paused = true;
            }
            else
            {
                cockpitRadio.audioSource.UnPause();
                cockpitRadio.paused = false;
            }
        }
        else
        {
            cockpitRadio.PlayButton();
        }

        CockpitRadioPatch.isRemoteCall = false;
    }

    [VTRPC]
    private void RPC_NextSong()
    {
        Debug.Log("[RadioNetSync] NextSong request received");
        var cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
        CockpitRadioPatch.isRemoteCall = true;
        cockpitRadio.NextSong();
        CockpitRadioPatch.isRemoteCall = false;
    }

    [VTRPC]
    private void RPC_PrevSong()
    {
        Debug.Log("[RadioNetSync] PrevSong request received");
        var cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
        CockpitRadioPatch.isRemoteCall = true;
        cockpitRadio.PrevSong();
        CockpitRadioPatch.isRemoteCall = false;
    }

    [VTRPC]
    private void RPC_StopSong()
    {
        Debug.Log("[RadioNetSync] StopSong request received");
        var cockpitRadio = UnityEngine.Object.FindObjectOfType<CockpitRadio>();
        CockpitRadioPatch.isRemoteCall = true;
        cockpitRadio.StopPlayingSong();
        CockpitRadioPatch.isRemoteCall = false;
    }
}