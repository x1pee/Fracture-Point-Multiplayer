using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
namespace FractureTogether;
static class InputBridge
{
 [DllImport("user32.dll")]static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
 internal static bool Legacy=true;static bool mouseHeld,mouseReleased,mouseActive;static int mouseFrame=-1;
 static readonly Dictionary<KeyCode,(Key Modern,int VirtualKey)> mappings=CreateMappings();
 static readonly KeyCode[] captureKeys=mappings.Keys.ToArray();
 static readonly Dictionary<KeyCode,bool> held=new();static readonly Dictionary<KeyCode,(int Frame,bool Down,bool Held)> frames=new();
 static readonly Dictionary<int,(int Frame,short State)> virtualFrames=new();
 static int typedFrame=-1,activeFrame=-1,focusGainedFrame=-1;static string typedBatch="";static bool active;
 internal static bool Active {get{var frame=Time.frameCount;if(activeFrame!=frame){activeFrame=frame;GetWindowThreadProcessId(GetForegroundWindow(),out var pid);var next=pid==(uint)Environment.ProcessId;if(next&&!active)focusGainedFrame=frame;active=next;}return active;}}
 internal static bool FocusGained=>Active&&focusGainedFrame==Time.frameCount;
 // Unity's enum values are not Win32 VKs, and InputSystem has different names
 // for digits, punctuation, Enter and Ctrl. Never guess or fall back to F11.
 static Dictionary<KeyCode,(Key Modern,int VirtualKey)> CreateMappings(){
  var map=new Dictionary<KeyCode,(Key,int)>();void Add(KeyCode old,Key current,int vk)=>map.Add(old,(current,vk));
  for(var i=0;i<26;i++)Add((KeyCode)((int)KeyCode.A+i),(Key)((int)Key.A+i),0x41+i);
  for(var i=0;i<10;i++){Add((KeyCode)((int)KeyCode.Alpha0+i),i==0?Key.Digit0:(Key)((int)Key.Digit1+i-1),0x30+i);Add((KeyCode)((int)KeyCode.Keypad0+i),(Key)((int)Key.Numpad0+i),0x60+i);}
  for(var i=0;i<15;i++)Add((KeyCode)((int)KeyCode.F1+i),i<12?(Key)((int)Key.F1+i):Key.None,0x70+i);
  Add(KeyCode.Backspace,Key.Backspace,0x08);Add(KeyCode.Tab,Key.Tab,0x09);Add(KeyCode.Clear,Key.None,0x0C);Add(KeyCode.Return,Key.Enter,0x0D);
  Add(KeyCode.Pause,Key.Pause,0x13);Add(KeyCode.Escape,Key.Escape,0x1B);Add(KeyCode.Space,Key.Space,0x20);
  Add(KeyCode.PageUp,Key.PageUp,0x21);Add(KeyCode.PageDown,Key.PageDown,0x22);Add(KeyCode.End,Key.End,0x23);Add(KeyCode.Home,Key.Home,0x24);
  Add(KeyCode.LeftArrow,Key.LeftArrow,0x25);Add(KeyCode.UpArrow,Key.UpArrow,0x26);Add(KeyCode.RightArrow,Key.RightArrow,0x27);Add(KeyCode.DownArrow,Key.DownArrow,0x28);
  Add(KeyCode.Print,Key.PrintScreen,0x2C);Add(KeyCode.Insert,Key.Insert,0x2D);Add(KeyCode.Delete,Key.Delete,0x2E);Add(KeyCode.Help,Key.None,0x2F);
  Add(KeyCode.KeypadMultiply,Key.NumpadMultiply,0x6A);Add(KeyCode.KeypadPlus,Key.NumpadPlus,0x6B);Add(KeyCode.KeypadMinus,Key.NumpadMinus,0x6D);Add(KeyCode.KeypadPeriod,Key.NumpadPeriod,0x6E);Add(KeyCode.KeypadDivide,Key.NumpadDivide,0x6F);
  // VK_RETURN cannot distinguish the numpad Enter. Use its precise native inputs.
  Add(KeyCode.KeypadEnter,Key.NumpadEnter,0);Add(KeyCode.KeypadEquals,Key.NumpadEquals,0);
  Add(KeyCode.Numlock,Key.NumLock,0x90);Add(KeyCode.CapsLock,Key.CapsLock,0x14);Add(KeyCode.ScrollLock,Key.ScrollLock,0x91);
  Add(KeyCode.Semicolon,Key.Semicolon,0xBA);Add(KeyCode.Equals,Key.Equals,0xBB);Add(KeyCode.Comma,Key.Comma,0xBC);Add(KeyCode.Minus,Key.Minus,0xBD);Add(KeyCode.Period,Key.Period,0xBE);Add(KeyCode.Slash,Key.Slash,0xBF);Add(KeyCode.BackQuote,Key.Backquote,0xC0);
  Add(KeyCode.LeftBracket,Key.LeftBracket,0xDB);Add(KeyCode.Backslash,Key.Backslash,0xDC);Add(KeyCode.RightBracket,Key.RightBracket,0xDD);Add(KeyCode.Quote,Key.Quote,0xDE);
  Add(KeyCode.LeftShift,Key.LeftShift,0xA0);Add(KeyCode.RightShift,Key.RightShift,0xA1);Add(KeyCode.LeftControl,Key.LeftCtrl,0xA2);Add(KeyCode.RightControl,Key.RightCtrl,0xA3);Add(KeyCode.LeftAlt,Key.LeftAlt,0xA4);Add(KeyCode.RightAlt,Key.RightAlt,0xA5);
  Add(KeyCode.LeftMeta,Key.LeftMeta,0x5B);Add(KeyCode.RightMeta,Key.RightMeta,0x5C);Add(KeyCode.LeftWindows,Key.LeftMeta,0x5B);Add(KeyCode.RightWindows,Key.RightMeta,0x5C);Add(KeyCode.AltGr,Key.RightAlt,0xA5);Add(KeyCode.Menu,Key.ContextMenu,0x5D);
  return map;
 }
 internal static bool Mapped(KeyCode key)=>mappings.ContainsKey(key);
 internal static bool TryMap(KeyCode key,out Key modern,out int virtualKey){if(mappings.TryGetValue(key,out var map)){modern=map.Modern;virtualKey=map.VirtualKey;return true;}modern=Key.None;virtualKey=0;return false;}
 static (int Frame,bool Down,bool Held) Sample(KeyCode key){
  if(!Active||!mappings.TryGetValue(key,out var map))return(Time.frameCount,false,false);
  if(frames.TryGetValue(key,out var cached)&&cached.Frame==Time.frameCount)return cached;
  short state=0;if(map.VirtualKey!=0){if(virtualFrames.TryGetValue(map.VirtualKey,out var prior)&&prior.Frame==Time.frameCount)state=prior.State;else{state=GetAsyncKeyState(map.VirtualKey);virtualFrames[map.VirtualKey]=(Time.frameCount,state);}}
  var now=(state&0x8000)!=0;var down=(state&1)!=0||(now&&!held.GetValueOrDefault(key));
  if(Legacy)try{down|=Input.GetKeyDown(key);now|=Input.GetKey(key);}catch(InvalidOperationException){Legacy=false;}
  if(map.Modern!=Key.None&&Keyboard.current is {} keyboard){var control=keyboard[map.Modern];down|=control.wasPressedThisFrame;now|=control.isPressed;}
  held[key]=now;return frames[key]=(Time.frameCount,down,now);
 }
 internal static bool Down(KeyCode key)=>Sample(key).Down;
 internal static bool Down(KeyCode legacy,Key modern)=>Down(legacy);
 internal static bool Held(KeyCode key)=>Sample(key).Held;
 internal static bool ModifiersHeld=>Held(KeyCode.LeftShift)||Held(KeyCode.RightShift)||Held(KeyCode.LeftControl)||Held(KeyCode.RightControl)||Held(KeyCode.LeftAlt)||Held(KeyCode.RightAlt)||Held(KeyCode.LeftWindows)||Held(KeyCode.RightWindows);
 internal static bool TryCaptureKey(out KeyCode key){
  key=KeyCode.None;if(!Active)return false;var keyboard=Keyboard.current;var nativePressed=keyboard?.anyKey.wasPressedThisFrame==true;
  if(Legacy)try{nativePressed|=Input.anyKeyDown;}catch(InvalidOperationException){Legacy=false;}
  if(!nativePressed)return false;
  // Only current native edges count here. Old Win32 low bits from text typed
  // before capture must never select an unrelated key.
  foreach(var candidate in captureKeys){var pressed=false;if(Legacy)try{pressed=Input.GetKeyDown(candidate);}catch(InvalidOperationException){Legacy=false;}
   var modern=mappings[candidate].Modern;if(modern!=Key.None&&keyboard!=null)pressed|=keyboard[modern].wasPressedThisFrame;
   if(pressed){key=candidate;return true;}
  }return false;
 }
 static void PollMouse(){var active=Active;var frame=Time.frameCount;if(mouseFrame==frame&&mouseActive==active)return;mouseFrame=frame;mouseActive=active;if(!active){mouseHeld=mouseReleased=false;return;}var state=GetAsyncKeyState(1);var now=(state&0x8000)!=0;mouseReleased=!now&&(mouseHeld||(state&1)!=0);mouseHeld=now;if(Legacy)try{mouseReleased|=Input.GetMouseButtonUp(0);}catch(InvalidOperationException){Legacy=false;}mouseReleased|=Mouse.current?.leftButton.wasReleasedThisFrame==true;}
 // UI release guards and the panel share one sample. Reading LeftHeld must not
 // consume Win32's short-click bit before MouseReleased can observe it.
 internal static bool LeftHeld {get{PollMouse();return mouseActive&&mouseHeld;}}
 internal static bool MouseReleased(){PollMouse();return mouseActive&&mouseReleased;}
 internal static Vector2 Pointer(){if(Legacy)try{return Input.mousePosition;}catch(InvalidOperationException){Legacy=false;}return Mouse.current?.position.ReadValue()??Vector2.zero;}
 // Keep the original batch for the whole frame. PanelText consumes its own
 // queue on the first GUI pass; later GUI key events must still know a legacy
 // batch already supplied these characters. GUI may also run before Update.
 internal static string Typed(){if(typedFrame==Time.frameCount)return typedBatch;typedFrame=Time.frameCount;typedBatch="";if(Legacy)try{typedBatch=Input.inputString;}catch(InvalidOperationException){Legacy=false;}return typedBatch;}
 internal static bool HasLegacyTypedBatchThisFrame=>Typed().Length>0;
}
