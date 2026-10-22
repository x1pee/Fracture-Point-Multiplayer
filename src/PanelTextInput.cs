using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
namespace FractureTogether;
public sealed partial class Together
{
 int activeTextField,textCaret,menuTextAction;bool textSelectAll;string menuTyped="";char pendingTextSurrogate;GUIStyle? panelInputStyle;
 readonly Dictionary<int,string> validatedFieldValues=new(),maskedFieldDisplays=new();
 int textCommandFrame=-1;readonly int[] polledTextCommands=new int[10],guiTextCommands=new int[10];
 // Match only duplicate deliveries from the OTHER input source. Multiple
 // genuine identical GUI commands in one frame remain separate commands.
 bool TakeTextCommand(int action,bool fromGui){
  if(textCommandFrame!=Time.frameCount){textCommandFrame=Time.frameCount;Array.Clear(polledTextCommands,0,polledTextCommands.Length);Array.Clear(guiTextCommands,0,guiTextCommands.Length);}
  var index=action+2;var other=fromGui?polledTextCommands:guiTextCommands;var own=fromGui?guiTextCommands:polledTextCommands;
  if(other[index]>0){other[index]--;return false;}own[index]++;return true;
 }
 void CollectTextInput(){menuTextAction=0;if(!panel||activeTextField==0)return;var k=Keyboard.current;if(k?.ctrlKey.isPressed==true){if(InputBridge.Down(KeyCode.A,Key.A))menuTextAction=4;else if(InputBridge.Down(KeyCode.V,Key.V))menuTextAction=5;}else if(InputBridge.Down(KeyCode.LeftArrow,Key.LeftArrow))menuTextAction=-1;else if(InputBridge.Down(KeyCode.RightArrow,Key.RightArrow))menuTextAction=1;else if(InputBridge.Down(KeyCode.Home,Key.Home))menuTextAction=-2;else if(InputBridge.Down(KeyCode.End,Key.End))menuTextAction=2;else if(InputBridge.Down(KeyCode.Delete,Key.Delete))menuTextAction=3;}
 static int PreviousTextElement(string text,int caret){var previous=0;foreach(var start in StringInfo.ParseCombiningCharacters(text)){if(start>=caret)break;previous=start;}return previous;}
 static int NextTextElement(string text,int caret){foreach(var start in StringInfo.ParseCombiningCharacters(text))if(start>caret)return start;return text.Length;}
 static int NextOrSameBoundary(string text,int caret){foreach(var start in StringInfo.ParseCombiningCharacters(text))if(start>=caret)return start;return text.Length;}
 static string LimitField(string text,int id,int maxLength){
  if(id==3)return NicknameRules.Limit(text);
  var valid=new StringBuilder();
  foreach(var rune in text.EnumerateRunes())if(!Rune.IsControl(rune)&&rune.Value!=0xFFFD)valid.Append(rune.ToString());
  var result=valid.ToString();if(result.Length<=maxLength)return result;
  var end=0;var elements=StringInfo.GetTextElementEnumerator(result);while(elements.MoveNext()){var next=elements.ElementIndex+elements.GetTextElement().Length;if(next>maxLength)break;end=next;}return result[..end];
 }
 void DeleteText(ref string value,bool backward){
  if(textSelectAll){value="";textCaret=0;}
  else if(backward&&textCaret>0){var start=PreviousTextElement(value,textCaret);value=value.Remove(start,textCaret-start);textCaret=start;}
  else if(!backward&&textCaret<value.Length)value=value.Remove(textCaret,NextTextElement(value,textCaret)-textCaret);
  textSelectAll=false;
 }
 void InsertText(ref string value,string inserted,int id,int maxLength){
  if(textSelectAll){value="";textCaret=0;}
  var candidate=value[..textCaret]+inserted+value[textCaret..];
  // Keep the existing suffix when insertion would exceed the field limit.
  if(LimitField(candidate,id,maxLength)==candidate){value=candidate;textCaret=NextOrSameBoundary(value,textCaret+inserted.Length);}
  textSelectAll=false;
 }
 void TypedText(ref string value,string typed,int id,int maxLength){
  if(pendingTextSurrogate!='\0'){typed=pendingTextSurrogate+typed;pendingTextSurrogate='\0';}
  if(typed.Length>0&&char.IsHighSurrogate(typed[^1])){pendingTextSurrogate=typed[^1];typed=typed[..^1];}
  var elements=StringInfo.GetTextElementEnumerator(typed);
  while(elements.MoveNext()){
   var element=elements.GetTextElement();
   if(element=="\b")DeleteText(ref value,true);
   else if(element.Contains('\n')||element.Contains('\r'))activeTextField=0;
   else{var cleaned=LimitField(element,id,maxLength);if(cleaned.Length>0)InsertText(ref value,cleaned,id,maxLength);}
  }
 }
 void ApplyTextCommand(ref string value,int action,int id,int maxLength){
  if(action==4)textSelectAll=true;
  else if(action==5){var paste=LimitField(GUIUtility.systemCopyBuffer,id,maxLength);if(textSelectAll){value="";textCaret=0;}foreach(var element in TextElements(paste))InsertText(ref value,element,id,maxLength);textSelectAll=false;}
  else if(action==-1){textCaret=PreviousTextElement(value,textCaret);textSelectAll=false;}
  else if(action==1){textCaret=NextTextElement(value,textCaret);textSelectAll=false;}
  else if(action==-2){textCaret=0;textSelectAll=false;}
  else if(action==2){textCaret=value.Length;textSelectAll=false;}
  else if(action==3)DeleteText(ref value,false);
  else if(action==6)DeleteText(ref value,true);
  else if(action==7)activeTextField=0;
 }
 string PanelText(Rect rect,string value,int id,int maxLength,bool secret=false){
  if(!validatedFieldValues.TryGetValue(id,out var validated)||validated!=value){value=LimitField(value,id,maxLength);validatedFieldValues[id]=value;}
  if(MenuHit(rect)){activeTextField=id;textCaret=value.Length;textSelectAll=false;pendingTextSurrogate='\0';}
  if(activeTextField==id){
   textCaret=Math.Clamp(textCaret,0,value.Length);
   if(menuTextAction!=0&&TakeTextCommand(menuTextAction,false))ApplyTextCommand(ref value,menuTextAction,id,maxLength);
   if(menuTextAction!=4&&menuTextAction!=5)TypedText(ref value,menuTyped,id,maxLength);
   menuTyped="";menuTextAction=0;
   var e=Event.current;
   if(e!=null&&e.type==EventType.KeyDown){
    var modifier=e.control||e.command;
    var action=modifier?(e.keyCode==KeyCode.A?4:e.keyCode==KeyCode.V?5:0):e.keyCode switch{KeyCode.LeftArrow=>-1,KeyCode.RightArrow=>1,KeyCode.Home=>-2,KeyCode.End=>2,KeyCode.Delete=>3,KeyCode.Backspace=>6,KeyCode.Return=>7,KeyCode.KeypadEnter=>7,_=>0};
    // Legacy text includes backspace/return. Keep those events from deleting
    // or submitting twice, even after menuTyped was cleared by a Layout pass.
    var legacyBatch=InputBridge.HasLegacyTypedBatchThisFrame;
    if(action!=0&&(!(action is 6 or 7)||!legacyBatch)){if(TakeTextCommand(action,true))ApplyTextCommand(ref value,action,id,maxLength);}
    else if(action==0&&!modifier&&!legacyBatch&&e.character!='\0')TypedText(ref value,e.character.ToString(),id,maxLength);
    // Modifier commands never insert their printable 'a'/'v' character.
    e.Use();
   }
  }
  Rounded(rect,new Color(.8f,.85f,.86f,activeTextField==id?.13f:.07f),3);Fill(new Rect(rect.x,rect.yMax-1,rect.width,1),activeTextField==id?Accent:new Color(1,1,1,.17f));
  validatedFieldValues[id]=value;
  if(textSelectAll&&activeTextField==id)Rounded(new Rect(rect.x+5,rect.y+4,rect.width-10,rect.height-8),new Color(.55f,.62f,.60f,.35f),3);
  panelInputStyle??=new GUIStyle(textStyle!){wordWrap=false,alignment=TextAnchor.MiddleLeft,clipping=TextClipping.Clip};
  if(Event.current?.type==EventType.Repaint){var shown=value;if(secret){if(!maskedFieldDisplays.TryGetValue(id,out shown)||shown.Length!=value.Length)maskedFieldDisplays[id]=shown=new string('*',value.Length);}if(activeTextField==id&&((int)(Time.unscaledTime*2)&1)==0)shown=shown.Insert(Math.Clamp(textCaret,0,shown.Length),"|");GUI.Label(new Rect(rect.x+11,rect.y+2,rect.width-22,rect.height-4),shown,panelInputStyle);}return value;
 }
 static IEnumerable<string> TextElements(string text){var elements=StringInfo.GetTextElementEnumerator(text);while(elements.MoveNext())yield return elements.GetTextElement();}
}
