using HarmonyLib;
namespace FractureTogether;
// The overlay is modal: game uGUI must not receive its pointer/keyboard events.
[HarmonyPatch(typeof(UnityEngine.EventSystems.EventSystem),"Update")]
static class ModPanelUiCapture
{
 static bool Prefix()=>!Together.BlockNativeUi;
}
