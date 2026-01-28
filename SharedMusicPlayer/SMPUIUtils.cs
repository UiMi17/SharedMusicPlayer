using UnityEngine;
using UnityEngine.UI;
using VTOLVR.Multiplayer;

namespace SharedMusicPlayer
{
    public static class SMPUIUtils
{
    public static void SetUpdatingText(VTOLMPBriefingRoomUI briefingUI, string newText)
    {
        if (briefingUI == null || briefingUI.updatingContentObj == null)
            return;

        var texts = briefingUI.updatingContentObj.GetComponentsInChildren<Text>(includeInactive: true);
        foreach (var text in texts)
        {
            if (text.gameObject.name == "CancelInstructionText")
                continue;
            if (HasAncestorNamed(text.transform, "CancelDownloadButton"))
                continue;
            text.text = newText;
            return;
        }
        Debug.LogWarning("[SharedMusicPlayer] Text wasn't found inside the updatingContentObj");
    }

    private static bool HasAncestorNamed(Transform t, string name)
    {
        for (var p = t.parent; p != null; p = p.parent)
            if (p.name == name)
                return true;
        return false;
    }
    }
}
