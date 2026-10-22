using Demo.Scripts.Runtime.Character;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FractureTogether;

public sealed partial class Together
{
 internal static bool PanelOpen=>Current?.panel==true;
 FPSController? panelInputController;
 bool panelClosing,panelCursorRestorePending,panelNativePauseAtOpen,panelInputOwned;
 int panelCloseReleaseFrame=-1;
 // BlockNativeUi owns its pointer/frame guard. Keep this dependency one-way.
 bool PanelReleaseBlocked=>PanelVisualVisible||BlockNativeUi||menuCaptureRelease||Time.frameCount<=panelCloseReleaseFrame||InputBridge.LeftHeld||InputBridge.Held(Plugin.Instance.MenuKey.Value)||InputBridge.Held(KeyCode.Escape);
 internal static bool PanelInputBlocked=>Current is {} c&&(c.PanelVisualVisible||c.panelClosing&&c.PanelReleaseBlocked);
 internal bool IsPanelController(FPSController controller){
  if(!controller||!controller.gameObject.activeInHierarchy)return false;
  var local=LocalController;
  return local&&local.gameObject.activeInHierarchy?local.Pointer==controller.Pointer:controller.gameObject.CompareTag("Player");
 }
 void ForgetPanelInput(){pausedInput=null;panelInputController=null;panelInputOwned=false;}
 void BeginPanelInputTransition(bool open){
  if(open){
   // Reopening during the close animation keeps the original cursor snapshot.
   if(!panelCursorRestorePending){previousLock=Cursor.lockState;previousCursor=Cursor.visible;var local=LocalController;panelNativePauseAtOpen=local&&local.gameObject.activeInHierarchy&&local.IsPauseMenuOpened;}
   panelCursorRestorePending=true;panelClosing=false;
   var controller=LocalController;if(controller)PausePanelController(controller!);
   Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
  }else{panelClosing=true;panelCloseReleaseFrame=Time.frameCount+1;}
 }
 internal void PausePanelController(FPSController controller){
  if(!PanelInputBlocked||!IsPanelController(controller))return;
  var same=panelInputController&&panelInputController.Pointer==controller.Pointer;
  var input=same&&pausedInput?pausedInput:controller.GetComponent<PlayerInput>();
  if(!same||pausedInput&&input&&pausedInput.Pointer!=input.Pointer)ForgetPanelInput();
  // Disabled input owned by native pause/death is never claimed as ours.
  if(input&&input.enabled&&input.inputIsActive&&!controller.IsPauseMenuOpened){pausedInput=input;panelInputController=controller;panelInputOwned=true;input.DeactivateInput();}
  controller._playerInput=Vector2.zero;
 }
 void UpdatePanelInputLifecycle(){
  if(!panel&&!panelClosing&&!panelCursorRestorePending&&pausedInput is null)return;
  if(PanelInputBlocked){var local=LocalController;if(local)PausePanelController(local!);return;}
  panelClosing=false;
  var known=LocalController;var controller=known&&known.gameObject.activeInHierarchy?known:panelInputController;
  var nativePaused=controller&&controller!.IsPauseMenuOpened;
  var health=controller?controller!.Health:null;var nativeDead=health&&(health.died||health.Health<=0);
  if(panelCursorRestorePending&&InputBridge.Active){
   // Native pause and death own their cursor; a later unpause must not inherit
   // the mod menu's old unlocked snapshot.
   if(!panelNativePauseAtOpen&&!nativePaused&&!nativeDead&&!Down&&!PartyWiped&&!Spectating){Cursor.lockState=previousLock;Cursor.visible=previousCursor;}
   panelCursorRestorePending=false;
  }
  if(!panelInputOwned||!pausedInput||!panelInputController){ForgetPanelInput();return;}
  var localController=LocalController;
  if(localController&&localController.gameObject.activeInHierarchy&&localController.Pointer!=panelInputController!.Pointer){ForgetPanelInput();return;}
  var actual=panelInputController!.GetComponent<PlayerInput>();
  if(!actual||actual.Pointer!=pausedInput!.Pointer){ForgetPanelInput();return;}
  // Keep the pending ownership through down/revive and native pause. Their
  // input may already be disabled when death observes it, so dropping ours
  // here would leave a revived player unable to move.
  if(!InputBridge.Active||nativeDead||Down||PartyWiped||Spectating||!IsPanelController(panelInputController!)||panelInputController.IsPauseMenuOpened||!actual.enabled)return;
  if(!actual.inputIsActive)actual.ActivateInput();ForgetPanelInput();
 }
}

// This menu has a separately enabled InputAction; PlayerInput.DeactivateInput
// does not stop it. Its Update only polls the pause hotkey and toggles the menu.
[HarmonyPatch(typeof(Michsky.UI.Heat.PauseMenuManager),"Update")]
static class ModPanelNativePauseHotkey
{
 static bool Prefix()=>!Together.PanelInputBlocked;
}

[HarmonyPatch(typeof(PauseControlProxy),nameof(PauseControlProxy.OnPause))]
static class ModPanelNativePauseAction
{
 static bool Prefix()=>!Together.PanelInputBlocked;
}

[HarmonyPatch(typeof(PauseControlProxy),nameof(PauseControlProxy.OnInventory))]
static class ModPanelNativeInventoryAction
{
 static bool Prefix()=>!Together.PanelInputBlocked;
}

// FPSController.Update writes Locked/hidden directly through cached engine
// icalls before updating look input. A postfix-only unlock still recenters the
// pointer every frame. Use its existing cursor-unlocked pause branch for this
// single native call, without changing the player's persistent pause state.
[HarmonyPatch(typeof(FPSController),"Update")]
static class ModPanelCursorCapture
{
 static void Unlock(){Cursor.lockState=CursorLockMode.None;Cursor.visible=true;}
 static void Prefix(FPSController __instance,out bool __state){
  __state=false;var current=Together.Current;
  if(!Together.PanelInputBlocked||current==null||!current.IsPanelController(__instance))return;
  // A panel may be opened before the home player has been created, or survive
  // a scene change. Capture the new controller as soon as its native Update runs.
  current.PausePanelController(__instance);
  Unlock();
  if(__instance.IsPauseMenuOpened)return;
  __instance.IsPauseMenuOpened=true;__state=true;
 }
 static Exception? Finalizer(FPSController __instance,bool __state,Exception? __exception){
  // Restore our scoped flag even if the panel closed during this call. The
  // same nullable-local identity check admits a newly created Player body.
  var current=Together.Current;
  if(__state&&current!=null&&current.IsPanelController(__instance)&&__instance.IsPauseMenuOpened){
   __instance.IsPauseMenuOpened=false;if(Together.PanelInputBlocked)Unlock();
  }
  return __exception;
 }
}
