using UnityEngine;
namespace FractureTogether;

static class ModMenuHotkeys
{
 internal static bool Reserved(KeyCode key)=>key is KeyCode.F6 or KeyCode.F7 or KeyCode.F9 or KeyCode.F10;
 internal static bool Modifier(KeyCode key)=>key is KeyCode.LeftShift or KeyCode.RightShift or KeyCode.LeftControl or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt or KeyCode.LeftMeta or KeyCode.RightMeta or KeyCode.LeftWindows or KeyCode.RightWindows or KeyCode.AltGr;
 internal static bool IsValid(KeyCode key)=>InputBridge.Mapped(key)&&!Reserved(key)&&!Modifier(key)&&key!=KeyCode.Escape;
 internal static string Label(KeyCode key)=>key switch{
  >=KeyCode.Alpha0 and <=KeyCode.Alpha9=>((int)key-(int)KeyCode.Alpha0).ToString(),KeyCode.BackQuote=>"`",KeyCode.Quote=>"'",KeyCode.Semicolon=>";",KeyCode.Comma=>",",KeyCode.Period=>".",KeyCode.Slash=>"/",KeyCode.Backslash=>"\\",KeyCode.LeftBracket=>"[",KeyCode.RightBracket=>"]",KeyCode.Minus=>"-",KeyCode.Equals=>"=",KeyCode.Return=>"Enter",KeyCode.KeypadEnter=>"Numpad Enter",KeyCode.Print=>"Print Screen",_=>key.ToString()};
}

public sealed partial class Together
{
 bool menuCapture,menuCaptureRelease;KeyCode menuCaptureReleaseKey;int menuCaptureStartedFrame=-1,menuCaptureReleaseFrame=-1;
 string menuCaptureMessage="";
 internal bool MenuCaptureActive=>menuCapture;
 internal string MenuCaptureMessage=>menuCaptureMessage;
 internal static string MenuKeyLabel=>ModMenuHotkeys.Label(Plugin.Instance.MenuKey.Value);
 internal static bool MenuHotkeysBlocked=>InputBridge.FocusGained||Current is {} c&&(c.menuCapture||c.menuCaptureRelease||!c.panel&&(c.PanelVisualVisible||c.panelClosing&&c.PanelReleaseBlocked)||c.panel&&(c.activeTextField!=0||c.KeyPopupVisible));
 void GuardMenuKeyRelease(KeyCode key){menuCaptureRelease=true;menuCaptureReleaseKey=key;menuCaptureReleaseFrame=Time.frameCount+1;InputBridge.Down(key);}
 internal void StartMenuKeyCapture(){menuCapture=true;menuCaptureStartedFrame=Time.frameCount;activeTextField=0;menuTyped="";menuTextAction=0;menuCaptureMessage="Press one key. Esc to cancel.";}
 internal void CancelMenuKeyCapture(){if(!menuCapture)return;menuCapture=false;GuardMenuKeyRelease(KeyCode.Escape);menuCaptureMessage="Cancelled";}
 internal bool SetMenuKey(KeyCode key){
  if(!ModMenuHotkeys.IsValid(key)){menuCaptureMessage=ModMenuHotkeys.Reserved(key)?ModMenuHotkeys.Label(key)+" is already used by the mod.":"Use one keyboard key without modifiers. Esc cancels.";return false;}
  Plugin.Instance.MenuKey.Value=key;Plugin.Instance.Config.Save();activeTextField=0;menuTyped="";menuTextAction=0;menuCapture=false;GuardMenuKeyRelease(key);menuCaptureMessage="Menu key: "+ModMenuHotkeys.Label(key);return true;
 }
 void CaptureMenuKey(KeyCode key){if(key==KeyCode.Escape)CancelMenuKeyCapture();else SetMenuKey(key);}
 void CloseMenuFromEscape(){
  if(menuCapture){CancelMenuKeyCapture();return;}
  GuardMenuKeyRelease(KeyCode.Escape);
  if(KeyPopupVisible){menuKeyPopupOpen=false;popupGestureFrame=Time.frameCount;return;}
  SetPanel(false);
 }
 internal void HandleMenuKeyCaptureEvent(){
  var e=Event.current;if(e==null||!e.isKey)return;
  if(e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape&&panel){if(!InputBridge.FocusGained&&!menuCaptureRelease)CloseMenuFromEscape();e.Use();return;}
  if(menuCapture){if(e.type==EventType.KeyDown){if(e.control||e.alt||e.shift||e.command)menuCaptureMessage="Use one key without modifiers.";else CaptureMenuKey(e.keyCode);}e.Use();}
  else if(menuCaptureRelease||panelClosing)e.Use();
 }
 void UpdateMenuHotkeys(){
  UpdatePanelInputLifecycle();
  if(!InputBridge.Active){CancelMenuKeyCapture();return;}
  // Drain/cache every command edge even while blocked. A rejected F6/F10 or
  // text-field key must not be replayed later from GetAsyncKeyState's low bit.
  var key=Plugin.Instance.MenuKey.Value;var menuDown=InputBridge.Down(key);
  var host=InputBridge.Down(KeyCode.F6);var join=InputBridge.Down(KeyCode.F7);var leave=InputBridge.Down(KeyCode.F9);var escape=InputBridge.Down(KeyCode.Escape);InputBridge.Down(KeyCode.F10);
  // Keys pressed in another application may leave Win32's low bit set. Drain
  // this first foreground frame, then require command keys to be released.
  if(InputBridge.FocusGained){GuardMenuKeyRelease(key);return;}
  if(panel&&escape&&!menuCaptureRelease){CloseMenuFromEscape();return;}
  if(menuCaptureRelease){
   var held=InputBridge.Held(menuCaptureReleaseKey)||InputBridge.Held(key)||InputBridge.Held(KeyCode.F6)||InputBridge.Held(KeyCode.F7)||InputBridge.Held(KeyCode.F9)||InputBridge.Held(KeyCode.F10)||InputBridge.Held(KeyCode.Escape);
   if(Time.frameCount>menuCaptureReleaseFrame&&!held)menuCaptureRelease=false;
   return;
  }
  if(menuCapture){
   if(!panel){CancelMenuKeyCapture();return;}
   if(Time.frameCount>menuCaptureStartedFrame){if(InputBridge.ModifiersHeld)menuCaptureMessage="Use one key without modifiers.";else if(InputBridge.TryCaptureKey(out var captured))CaptureMenuKey(captured);}
   return;
  }
  if(panel&&activeTextField!=0&&!KeyPopupVisible&&!InputBridge.ModifiersHeld&&key>=KeyCode.F1&&key<=KeyCode.F15&&menuDown){SetPanel(false);return;}
  if(MenuHotkeysBlocked||InputBridge.ModifiersHeld)return;
  if(menuDown){SetPanel(!panel);return;}
  if(leave)StopSession();else if(host)StartSession(true);else if(join)StartSession(false);
 }
}
