using UnityEngine;
using UnityEngine.UI;
using VTOLVR.Multiplayer;

public static class SMPUIUtils
{
    public static void SetUpdatingText(VTOLMPBriefingRoomUI briefingUI, string newText)
    {
        if (briefingUI == null || briefingUI.updatingContentObj == null)
            return;

        // Looking for text in briefingUI
        var text = briefingUI.updatingContentObj.GetComponentInChildren<Text>(includeInactive: true);
        if (text != null)
        {
            text.text = newText;
        }
        else
        {
            Debug.LogWarning("[SharedMusicPlayer] Text wasn't found inside the updatingContentObj");
        }
    }
}
