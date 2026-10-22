using System.Globalization;
using System.Text;
using UnityEngine;
namespace FractureTogether;

public sealed partial class Together
{
 const float PanelWidth=900,PanelHeight=570,PageWidth=644,PageHeight=332;
 static readonly string[] PanelTabs={"Play together","Co-op rules","Display","About"};
 static readonly string[] TabHints={"Connection & party","Shared settings","Graphics & camera","Progress & help"};
 static readonly string[] TabNumbers={"1","2","3","4"};
 static readonly string[] GraphicsModes={"Original","Balanced","Fast"};
 static readonly string[] GraphicsDescriptions={"Game defaults","Less distant shadow","Less shadow, no SSR"};
 static readonly KeyCode[] KeyPresets={KeyCode.F11,KeyCode.F12,KeyCode.Home,KeyCode.End};
 static readonly Color Ink=new(.94f,.95f,.97f),Muted=new(.66f,.70f,.75f),Accent=new(.90f,.91f,.86f),Surface=new(.035f,.045f,.061f,.92f),Card=new(.73f,.78f,.84f,.085f);
 static readonly Rect ShortcutButton=new(116,527,104,31),HeaderDragBounds=new(0,0,830,92);
 int settingsTab,connectionMode;bool showCode,advancedGraphics,menuClick;Vector2 menuPointer;
 GUIStyle? textStyle,smallStyle,titleStyle,buttonStyle,primaryButtonStyle,pageTitleStyle,navStyle,captionStyle,connectionErrorTitleStyle,footerStyle,readOnlyFieldStyle,shortcutButtonStyle;
 string localAddresses="",cachedRoster="",cachedLobbyNote="",connectionUiError="",cachedKeyButton="",cachedHint="",cachedHintKey="";
 string issueCode="",issueTitle="",issueHint="",issueVersions="";GUIContent? issueCodeContent,issueTitleContent,issueHintContent,issueVersionsContent;
 KeyCode cachedKeyButtonCode=KeyCode.None;float rosterRefreshAt,addressRefreshAt;
 readonly List<int> rosterIds=new(4);readonly StringBuilder rosterBuilder=new(256);readonly string[] keyPresetLabels=new string[4];
 Vector2 panelOffset,dragStartPointer,dragStartOffset;bool panelDragging,menuMouseWasHeld,nativeUiReleaseGuard;int nativeUiResumeFrame=-1;
 int dragReleaseFrame=-1,dragEventFrame=-1;
 float panelOpenAmount,menuKeyPopupAmount,tabContentAmount=1;
 bool menuKeyPopupOpen,drawingMenuPopup;int popupGestureFrame=-1;
 string menuTooltip="",cachedTooltip="";Rect menuTooltipAnchor,cachedTooltipAnchor;float tooltipAmount;int tooltipFrame=-1;GUIContent? tooltipContent;
 readonly Dictionary<string,(GUIContent Content,float Width)> settingLabels=new(StringComparer.Ordinal);
 readonly Dictionary<string,(float Value,string Format,string Text)> stepDisplays=new(StringComparer.Ordinal);
 readonly Dictionary<int,float> switchAnimations=new();readonly Dictionary<int,int> switchAnimationFrames=new();
 struct ControlTween {internal float Hover,Selected,Press;internal int Frame;}
 readonly Dictionary<int,ControlTween> controlTweens=new(256);float shortcutKeyPulse;
 string rulesPreview="";int previewPlayers,previewLoot;float previewFloor=-1,previewEnemies=-1;
 static float hudHintUntil=-1,hudHintStarted=-1;
 float PanelScale=>Math.Min(1.15f,Math.Min(Screen.width/(PanelWidth+48),Screen.height/(PanelHeight+48)));
 bool PanelVisualVisible=>panel||panelOpenAmount>.001f;
 bool KeyPopupVisible=>menuKeyPopupOpen||menuKeyPopupAmount>.001f;
 static float Ease(float value)=>value*value*(3-2*value);
 Vector2 PanelOrigin(){var scale=PanelScale;var center=new Vector2((Screen.width-PanelWidth*scale)/2,(Screen.height-PanelHeight*scale)/2);var origin=center+panelOffset;origin.x=Mathf.Clamp(origin.x,8,Math.Max(8,Screen.width-PanelWidth*scale-8));origin.y=Mathf.Clamp(origin.y,8,Math.Max(8,Screen.height-PanelHeight*scale-8));panelOffset=origin-center;return origin;}
 Vector2 PanelVisualOrigin()=>PanelOrigin()+new Vector2(0,(1-Ease(panelOpenAmount))*8*PanelScale);
 internal static bool BlockNativeUi {get{var c=Current;if(c==null)return false;if(c.PanelVisualVisible)return true;if(c.nativeUiReleaseGuard){if(InputBridge.LeftHeld)return true;c.nativeUiReleaseGuard=false;c.nativeUiResumeFrame=Time.frameCount+2;}return Time.frameCount<=c.nativeUiResumeFrame;}}
 void OnPanelVisibilityChanged(bool value){menuKeyPopupOpen=false;menuKeyPopupAmount=0;CancelMenuKeyCapture();activeTextField=0;menuTyped="";menuTooltip="";cachedTooltip="";tooltipAmount=0;if(value)rosterRefreshAt=0;}
 void InitializeHudHint(){
  // Awake also calls this method. Start the one-shot clock only when the hint
  // can actually be painted in a playable scene, never during loading.
  if(hudHintUntil>=0||PanelVisualVisible||scene.Length==0||scene=="MainMenu"||!player||!player!.gameObject.activeInHierarchy||Event.current?.type!=EventType.Repaint)return;
  hudHintStarted=Time.realtimeSinceStartup;hudHintUntil=hudHintStarted+15;
 }
 void MenuInputTick(){
  var dt=Math.Min(Time.unscaledDeltaTime,.05f);panelOpenAmount=Mathf.MoveTowards(panelOpenAmount,panel?1:0,dt/.2f);menuKeyPopupAmount=Mathf.MoveTowards(menuKeyPopupAmount,menuKeyPopupOpen?1:0,dt/.14f);tabContentAmount=Mathf.MoveTowards(tabContentAmount,1,dt/.14f);shortcutKeyPulse=Mathf.MoveTowards(shortcutKeyPulse,0,dt/.28f);
  // Pointer polling is dormant before the first hint and after it expires.
  if(!PanelVisualVisible&&(hudHintUntil<0||Time.realtimeSinceStartup>=hudHintUntil)){menuTyped="";menuTextAction=0;menuMouseWasHeld=false;panelDragging=false;return;}
  menuClick=InputBridge.Active&&InputBridge.MouseReleased();var p=InputBridge.Pointer();menuPointer=new Vector2(p.x,Screen.height-p.y);var held=InputBridge.LeftHeld;var pressed=held&&!menuMouseWasHeld;menuMouseWasHeld=held;
  if(!panel||!InputBridge.Active)panelDragging=false;
  if(panel&&pressed&&!KeyPopupVisible&&panelOpenAmount>.98f){var local=(menuPointer-PanelVisualOrigin())/PanelScale;if(HeaderDragBounds.Contains(local)){panelDragging=true;dragEventFrame=Time.frameCount;dragStartPointer=menuPointer;dragStartOffset=panelOffset;activeTextField=0;}}
  if(panelDragging&&held){panelOffset=dragStartOffset+menuPointer-dragStartPointer;PanelOrigin();dragEventFrame=Time.frameCount;}
  if(panelDragging&&!held&&Time.frameCount>dragEventFrame+2){panelDragging=false;dragReleaseFrame=Time.frameCount+1;}
  if(panelDragging||Time.frameCount<=dragReleaseFrame)menuClick=false;
  menuTyped=panel&&!MenuCaptureActive?InputBridge.Typed():"";CollectTextInput();
 }
 void PanelDragEvent(){
  var e=Event.current;if(e==null||!panel||KeyPopupVisible||panelOpenAmount<.98f)return;var p=e.mousePosition;var local=(p-PanelVisualOrigin())/PanelScale;
  if(e.type==EventType.MouseDown&&e.button==0&&HeaderDragBounds.Contains(local)){panelDragging=true;dragEventFrame=Time.frameCount;dragStartPointer=p;dragStartOffset=panelOffset;activeTextField=0;menuClick=false;e.Use();}
  else if(panelDragging&&e.type==EventType.MouseDrag){dragEventFrame=Time.frameCount;panelOffset=dragStartOffset+p-dragStartPointer;PanelOrigin();menuClick=false;e.Use();}
  else if(panelDragging&&e.type==EventType.MouseUp){panelDragging=false;dragReleaseFrame=Time.frameCount+1;menuClick=false;e.Use();}
 }
 bool MenuHit(Rect rect){var enabled=GUI.enabled;GUI.enabled=enabled&&!panelDragging&&Time.frameCount>dragReleaseFrame&&Time.frameCount>popupGestureFrame&&(drawingMenuPopup||!KeyPopupVisible);bool hit;try{hit=GUI.Button(rect,GUIContent.none,GUIStyle.none);}finally{GUI.enabled=enabled;}if(hit)menuClick=false;return hit;}
 static void Fill(Rect rect,Color color){if(Event.current?.type!=EventType.Repaint)return;var old=GUI.color;color.a*=old.a;GUI.color=color;GUI.DrawTexture(rect,Texture2D.whiteTexture);GUI.color=old;}
 static void Rounded(Rect rect,Color color,float radius=5){
  if(Event.current?.type!=EventType.Repaint)return;var old=GUI.color;color.a*=old.a;GUI.color=Color.white;
  try{GUI.DrawTexture(rect,Texture2D.whiteTexture,ScaleMode.StretchToFill,true,0,color,0,Math.Min(radius,Math.Min(rect.width,rect.height)/2));}finally{GUI.color=old;}
 }
 void Styles(){if(textStyle!=null)return;textStyle=new GUIStyle(GUI.skin.label){fontSize=14,wordWrap=true,richText=false};textStyle.normal.textColor=Ink;smallStyle=new GUIStyle(textStyle){fontSize=11};smallStyle.normal.textColor=Muted;titleStyle=new GUIStyle(textStyle){fontSize=25,fontStyle=FontStyle.Normal};pageTitleStyle=new GUIStyle(titleStyle){fontSize=24};navStyle=new GUIStyle(textStyle){fontSize=16};captionStyle=new GUIStyle(smallStyle){fontSize=10};buttonStyle=new GUIStyle(textStyle){alignment=TextAnchor.MiddleCenter,wordWrap=false};primaryButtonStyle=new GUIStyle(buttonStyle);primaryButtonStyle.normal.textColor=new Color(.055f,.065f,.065f);connectionErrorTitleStyle=new GUIStyle(textStyle){fontSize=20};shortcutButtonStyle=new GUIStyle(buttonStyle){fontSize=12};footerStyle=new GUIStyle(smallStyle){alignment=TextAnchor.MiddleLeft,wordWrap=false,clipping=TextClipping.Clip};readOnlyFieldStyle=new GUIStyle(textStyle){alignment=TextAnchor.MiddleLeft,wordWrap=false,clipping=TextClipping.Clip};}
 void Text(float x,float y,float w,float h,string text,bool small=false){if(Event.current?.type==EventType.Repaint)GUI.Label(new Rect(x,y,w,h),text,small?smallStyle!:textStyle!);}
 void Label(Rect rect,string text,GUIStyle style){if(Event.current?.type==EventType.Repaint)GUI.Label(rect,text,style);}
 bool Hover(Rect rect)=>rect.Contains(GUI.matrix.inverse.MultiplyPoint3x4(menuPointer));
 Vector3 AnimateControl(Rect rect,string text,int kind,bool hover,bool selected,bool clicked){
  // Visual-only stable slots; cap storage even if text/control geometry changes.
  var key=unchecked(text.GetHashCode()*397^Mathf.RoundToInt(rect.y/16)*31^rect.width.GetHashCode()^kind*7919^settingsTab*104729);
  var known=controlTweens.TryGetValue(key,out var tween);if(!known&&controlTweens.Count>=256)return new Vector3(hover?1:0,selected?1:0,clicked?1:0);
  if(clicked)tween.Press=1;
  if(Event.current?.type==EventType.Repaint&&(!known||tween.Frame!=Time.frameCount)){
   tween.Frame=Time.frameCount;var dt=Math.Min(Time.unscaledDeltaTime,.05f);var held=hover&&GUI.enabled&&!panelDragging&&menuMouseWasHeld;
   tween.Hover=Mathf.MoveTowards(tween.Hover,hover&&GUI.enabled?1:0,dt/.11f);tween.Selected=Mathf.MoveTowards(tween.Selected,selected?1:0,dt/.14f);tween.Press=Mathf.MoveTowards(tween.Press,held?1:0,dt/(held?.07f:.17f));
  }
  controlTweens[key]=tween;return new Vector3(Ease(tween.Hover),Ease(tween.Selected),Ease(tween.Press));
 }
 bool MenuButton(Rect rect,string text,bool primary=false,bool selected=false){var clicked=MenuHit(rect);var a=AnimateControl(rect,text,1,Hover(rect),selected,clicked);var press=Math.Max(a.z,rect==ShortcutButton?shortcutKeyPulse:0);var tint=primary?Color.Lerp(Accent,Color.white,a.x):Color.Lerp(Card,new Color(.80f,.85f,.90f,.14f),a.x);if(!primary)tint=Color.Lerp(tint,new Color(.80f,.85f,.90f,.19f),a.y);tint=Color.Lerp(tint,primary?new Color(.72f,.75f,.72f):new Color(.80f,.85f,.90f,.29f),press*.75f);if(!GUI.enabled)tint.a*=.45f;Rounded(rect,tint);var old=GUI.color;if(!GUI.enabled)GUI.color=new Color(1,1,1,old.a*.4f);Label(rect,text,rect==ShortcutButton?shortcutButtonStyle!:primary?primaryButtonStyle!:buttonStyle!);GUI.color=old;if(!clicked)return false;activeTextField=0;return true;}
 bool QuietButton(Rect rect,string text,bool selected=false){var clicked=MenuHit(rect);var a=AnimateControl(rect,text,2,Hover(rect),selected,clicked);var alpha=Mathf.Lerp(.09f*a.x,.16f,a.y)+a.z*.10f;if(alpha>.001f&&GUI.enabled)Rounded(rect,new Color(.8f,.85f,.90f,alpha));var old=GUI.color;if(!GUI.enabled)GUI.color=new Color(1,1,1,old.a*.4f);Label(rect,text,buttonStyle!);GUI.color=old;if(!clicked)return false;activeTextField=0;return true;}
 bool Switch(Rect rect,bool value,int id){
  var clicked=MenuHit(rect);if(clicked){value=!value;activeTextField=0;}var a=AnimateControl(rect,"toggle",3,Hover(rect),value,clicked);var target=value?1f:0f;if(!switchAnimations.TryGetValue(id,out var amount))amount=target;
  if(Event.current?.type==EventType.Repaint&&switchAnimationFrames.GetValueOrDefault(id,-1)!=Time.frameCount){amount=Mathf.MoveTowards(amount,target,Math.Min(Time.unscaledDeltaTime,.05f)/.14f);switchAnimations[id]=amount;switchAnimationFrames[id]=Time.frameCount;}
  var track=new Rect(rect.x+rect.width-44+a.z,rect.y+(rect.height-22)/2,44-2*a.z,22);Rounded(track,Color.Lerp(Color.Lerp(new Color(.53f,.57f,.62f,.38f),Accent,Ease(amount)),Color.white,a.x*.06f),11);Rounded(new Rect(track.x+3+(track.width-22)*Ease(amount),track.y+3,16,16),Color.Lerp(new Color(.78f,.81f,.85f),new Color(.12f,.12f,.11f),Ease(amount)),8);Label(new Rect(rect.x-28,rect.y,30,rect.height),value?"On":"Off",footerStyle!);return value;
 }
 void Tooltip(Rect rect,string text){if(panel&&!KeyPopupVisible&&Hover(rect)){menuTooltip=text;menuTooltipAnchor=rect;}}
 void SettingLabel(float y,string label,string help,float width=476){
  if(!settingLabels.TryGetValue(label,out var cached)){var content=new GUIContent(label);cached=(content,textStyle!.CalcSize(content).x);settingLabels[label]=cached;}
  var rect=new Rect(0,y+3,width,24);if(Event.current?.type==EventType.Repaint)GUI.Label(rect,cached.Content,textStyle!);
  if(help.Length==0)return;var line=new Rect(4,y+24,Math.Min(width-8,Math.Max(1,cached.Width-8)),1);var hover=new Rect(0,y,Math.Min(width,cached.Width),28);
  Fill(line,new Color(.72f,.77f,.83f,Hover(hover)?.48f:.16f));Tooltip(hover,help);
 }
 float StepRow(float y,string label,float value,float step,float min,float max,string format="0.##",bool enabled=true,string help=""){
  SettingLabel(y,label,help);var previous=GUI.enabled;GUI.enabled=previous&&enabled;var result=Mathf.Clamp(value,min,max);
  if(MenuButton(new Rect(496,y,30,28),"-"))result=Mathf.Clamp(Mathf.Round((result-step)*10000)/10000,min,max);
  if(Event.current?.type==EventType.Repaint){if(!stepDisplays.TryGetValue(label,out var display)||display.Value!=result||display.Format!=format){display=(result,format,result.ToString(format,CultureInfo.InvariantCulture));stepDisplays[label]=display;}Label(new Rect(530,y,72,28),display.Text,buttonStyle!);}
  if(MenuButton(new Rect(610,y,30,28),"+"))result=Mathf.Clamp(Mathf.Round((result+step)*10000)/10000,min,max);GUI.enabled=previous;return enabled?result:value;
 }
 string ConnectionStatus=>Connected?(SameWorld?"Everyone is ready":"Synchronizing players..."):Network?.Hosting==true?"Lobby open - waiting for friends":Network!=null?"Connecting...":"LAN co-op for up to four players.";
 void RefreshMenuKeyLabels(){
  var key=Plugin.Instance.MenuKey.Value;if(cachedKeyButtonCode==key)return;if(cachedKeyButtonCode!=KeyCode.None)shortcutKeyPulse=1;cachedKeyButtonCode=key;cachedHintKey=MenuKeyLabel;cachedKeyButton=cachedHintKey+"   ˅";cachedHint="Press "+cachedHintKey+" to open settings.";
  for(var i=0;i<KeyPresets.Length;i++)keyPresetLabels[i]=KeyPresets[i]+(key==KeyPresets[i]?"  (current)":"");
 }
 void DrawModHud(){
  InitializeHudHint();var now=Time.realtimeSinceStartup;if(PanelVisualVisible||hudHintUntil<0||now>=hudHintUntil)return;Styles();RefreshMenuKeyLabels();
  var enter=Ease(Mathf.Clamp01((now-hudHintStarted)/.35f));var leave=Ease(Mathf.Clamp01((hudHintUntil-now)/.6f));var alpha=Math.Min(enter,leave);var width=Math.Min(312,Screen.width-28);var x=Screen.width-width-14+(1-enter)*24+(1-leave)*(width+24);var r=new Rect(x,14,width,60);
  var matrix=GUI.matrix;var color=GUI.color;GUI.matrix=Matrix4x4.identity;GUI.color=new Color(1,1,1,color.a*alpha);
  try{Rounded(r,new Color(.045f,.057f,.075f,.90f),8);Text(r.x+12,r.y+7,r.width-88,22,"Fracture Point Multiplayer");Text(r.x+12,r.y+32,r.width-24,20,cachedHint,true);if(MenuButton(new Rect(r.x+r.width-70,r.y+8,58,25),cachedHintKey))SetPanel(true);}
  finally{GUI.matrix=matrix;GUI.color=color;}
 }
 void DrawModPanel(){
  HandleMenuKeyCaptureEvent();PanelDragEvent();Styles();RefreshMenuKeyLabels();var eventPointer=Event.current?.mousePosition??menuPointer;var matrix=GUI.matrix;var color=GUI.color;var enabled=GUI.enabled;var scale=PanelScale;var origin=PanelVisualOrigin();
  GUI.matrix=Matrix4x4.identity;GUI.color=new Color(1,1,1,color.a*Ease(panelOpenAmount));Fill(new Rect(0,0,Screen.width,Screen.height),new Color(.014f,.022f,.032f,.32f));
  var panelMatrix=Matrix4x4.TRS(new Vector3(origin.x,origin.y,0),Quaternion.identity,new Vector3(scale,scale,1));GUI.matrix=panelMatrix;GUI.enabled=enabled&&panel;menuTooltip="";
  try{
   PreparePopupInput(eventPointer);Rounded(new Rect(0,0,PanelWidth,PanelHeight),Surface,9);Label(new Rect(27,22,650,34),"Fracture Point",titleStyle!);Text(29,61,600,22,"MULTIPLAYER  /  LAN CO-OP",true);
   Text(783,30,43,24,"ESC",true);if(QuietButton(new Rect(840,22,32,34),"×")){SetPanel(false);return;}
   Fill(new Rect(199,112,1,372),new Color(1,1,1,.075f));
   for(var i=0;i<PanelTabs.Length;i++){
    var y=122+i*58;var rect=new Rect(20,y-5,164,49);var selected=settingsTab==i;var a=AnimateControl(rect,PanelTabs[i],4,Hover(rect),selected,false);var tabAlpha=Mathf.Lerp(.055f*a.x,.11f,a.y);if(tabAlpha>.001f)Rounded(rect,new Color(.78f,.84f,.91f,tabAlpha));
    var old=GUI.color;GUI.color=new Color(1,1,1,old.a*Mathf.Lerp(Mathf.Lerp(.58f,.88f,a.x),1,a.y));Label(new Rect(28,y+2,22,22),TabNumbers[i],captionStyle!);Label(new Rect(51,y-1,130,25),PanelTabs[i],navStyle!);Text(52,y+23,128,19,TabHints[i],true);GUI.color=old;
    if(MenuHit(rect)&&!selected){settingsTab=i;tabContentAmount=0;activeTextField=0;CancelMenuKeyCapture();menuKeyPopupOpen=false;menuTooltip="";}
   }
   var contentColor=GUI.color;GUI.color=new Color(1,1,1,contentColor.a*Ease(tabContentAmount));
   var showConnectionIssue=settingsTab==0&&Network==null&&connectionIssue!=null;
   Label(new Rect(228,109,PageWidth,34),settingsTab==0?(showConnectionIssue?"Connection problem":Network==null?"Play together":"Your party"):settingsTab==1?"Co-op rules":settingsTab==2?(advancedGraphics?"Light & shadows":"Display"):"About",pageTitleStyle!);
   Text(228,148,PageWidth,29,settingsTab==0?(showConnectionIssue?"Review the reason below, then retry or edit your connection.":connectionUiError.Length>0&&Network==null?connectionUiError:ConnectionStatus):settingsTab==1?"The host controls shared rules. Layout changes apply at home.":settingsTab==2?"Local graphics and camera settings.":"Fracture Point Multiplayer  /  0.3.0",true);
   GUI.matrix=panelMatrix*Matrix4x4.TRS(new Vector3(228,178+(1-Ease(tabContentAmount))*5,0),Quaternion.identity,Vector3.one);
   if(settingsTab==0)ConnectionPage();else if(settingsTab==1)RulesPage();else if(settingsTab==2)GraphicsPage();else AboutPage();DrawMenuTooltip();
   GUI.color=contentColor;GUI.matrix=panelMatrix;
   Fill(new Rect(24,516,852,1),new Color(1,1,1,.10f));Label(new Rect(28,527,84,31),"Menu shortcut",footerStyle!);
   drawingMenuPopup=true;if(MenuButton(ShortcutButton,MenuCaptureActive?"Press key...":cachedKeyButton)){CancelMenuKeyCapture();menuKeyPopupOpen=!menuKeyPopupOpen;activeTextField=0;popupGestureFrame=Time.frameCount;}drawingMenuPopup=false;
   Label(new Rect(238,527,394,31),MenuCaptureMessage.Length>0?MenuCaptureMessage:"Esc  Back / Close    ·    Drag the title to move",footerStyle!);
   // Authorship is compiled into the menu, with no file/config toggle. URLs
   // open only on an intentional click, never on load, hover or a timer.
   var authorRect=new Rect(650,527,140,31);var siteRect=new Rect(805,527,71,31);
   var authorHover=AnimateControl(authorRect,ReleaseCredits.Author,14,Hover(authorRect),false,false);var siteHover=AnimateControl(siteRect,ReleaseCredits.Site,14,Hover(siteRect),false,false);
   Label(authorRect,ReleaseCredits.Author,footerStyle!);Label(siteRect,ReleaseCredits.Site,footerStyle!);
   Fill(new Rect(authorRect.x,authorRect.y+22,116,1),new Color(.66f,.70f,.75f,authorHover.x*.65f));Fill(new Rect(siteRect.x,siteRect.y+22,51,1),new Color(.66f,.70f,.75f,siteHover.x*.65f));
   if(MenuHit(authorRect))Application.OpenURL(ReleaseCredits.AuthorUrl);if(MenuHit(siteRect))Application.OpenURL(ReleaseCredits.SiteUrl);DrawMenuKeyPopup();
  }finally{GUI.matrix=matrix;GUI.color=color;GUI.enabled=enabled;}
 }
 Rect ShortcutPopupBounds()=>new(116,278+(1-Ease(menuKeyPopupAmount))*6,220,234);
 void PreparePopupInput(Vector2 eventPointer){
  var e=Event.current;if(e==null||!KeyPopupVisible)return;if(e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape){menuKeyPopupOpen=false;popupGestureFrame=Time.frameCount;e.Use();return;}var local=GUI.matrix.inverse.MultiplyPoint3x4(eventPointer);
  if(e.type==EventType.MouseDown&&e.button==0&&!ShortcutPopupBounds().Contains(local)&&!ShortcutButton.Contains(local)){menuKeyPopupOpen=false;popupGestureFrame=Time.frameCount;activeTextField=0;e.Use();}
 }
 void DrawMenuKeyPopup(){
  if(!KeyPopupVisible)return;var old=GUI.color;var enabled=GUI.enabled;GUI.enabled=enabled&&menuKeyPopupOpen;GUI.color=new Color(1,1,1,old.a*Ease(menuKeyPopupAmount));var r=ShortcutPopupBounds();drawingMenuPopup=true;
  try{Rounded(r,new Color(.075f,.090f,.112f,.99f),8);Text(r.x+14,r.y+10,r.width-28,24,"Menu shortcut");Text(r.x+14,r.y+37,r.width-28,33,"Choose a key to open or close this menu",true);
   for(var i=0;i<KeyPresets.Length;i++)if(QuietButton(new Rect(r.x+10,r.y+74+i*26,r.width-20,24),keyPresetLabels[i],Plugin.Instance.MenuKey.Value==KeyPresets[i])){SetMenuKey(KeyPresets[i]);menuKeyPopupOpen=false;popupGestureFrame=Time.frameCount;}
   Fill(new Rect(r.x+14,r.y+184,r.width-28,1),new Color(1,1,1,.10f));if(QuietButton(new Rect(r.x+10,r.y+196,r.width-20,28),"Press another key...")){menuKeyPopupOpen=false;popupGestureFrame=Time.frameCount;StartMenuKeyCapture();}
  }finally{drawingMenuPopup=false;GUI.color=old;GUI.enabled=enabled;}
 }
 void DrawMenuTooltip(){
  if(Event.current?.type!=EventType.Repaint)return;var wanted=panel&&!KeyPopupVisible&&menuTooltip.Length>0;
  if(wanted){if(cachedTooltip!=menuTooltip){cachedTooltip=menuTooltip;tooltipContent??=new GUIContent();tooltipContent.text=cachedTooltip;tooltipAmount=0;}cachedTooltipAnchor=menuTooltipAnchor;}
  if(tooltipFrame!=Time.frameCount){tooltipFrame=Time.frameCount;tooltipAmount=Mathf.MoveTowards(tooltipAmount,wanted?1:0,Math.Min(Time.unscaledDeltaTime,.05f)/(wanted?.12f:.18f));}
  if(tooltipAmount<=.001f||tooltipContent==null)return;var alpha=Ease(tooltipAmount);const float width=328,height=82;
  var x=Mathf.Clamp(cachedTooltipAnchor.x,0,PageWidth-width);var y=cachedTooltipAnchor.yMax+8;if(y+height>PageHeight)y=Math.Max(0,cachedTooltipAnchor.y-height-8);y+=(1-alpha)*5;
  var old=GUI.color;GUI.color=new Color(1,1,1,old.a*alpha);try{Rounded(new Rect(x,y,width,height),new Color(.065f,.081f,.105f,.98f),7);GUI.Label(new Rect(x+12,y+9,width-24,height-18),tooltipContent,smallStyle!);}finally{GUI.color=old;}
 }
 void ReadLocalAddresses(){if(localAddresses.Length>0&&Time.unscaledTime<addressRefreshAt)return;addressRefreshAt=Time.unscaledTime+30;try{localAddresses=string.Join(" / ",System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).OrderByDescending(n=>n.Name.Contains("Radmin",StringComparison.OrdinalIgnoreCase)||n.Description.Contains("Radmin",StringComparison.OrdinalIgnoreCase)).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!System.Net.IPAddress.IsLoopback(a.Address)).Select(a=>a.Address.ToString()).Take(3));if(localAddresses.Length==0)localAddresses="Unavailable";}catch{localAddresses="Unavailable";}}
 void RefreshRoster(){
  if(Time.unscaledTime<rosterRefreshAt||Network==null)return;rosterRefreshAt=Time.unscaledTime+.5f;rosterIds.Clear();rosterIds.Add(Network.LocalId);foreach(var id in remotes.Keys)if(id!=Network.LocalId)rosterIds.Add(id);rosterIds.Sort();rosterBuilder.Clear();
  for(var i=0;i<rosterIds.Count;i++){if(i>0)rosterBuilder.Append('\n');var id=rosterIds[i];rosterBuilder.Append(i+1).Append(". ");if(id==Network.LocalId)rosterBuilder.Append(Plugin.Instance.Nick.Value).Append("  (you)");else if(remotes.TryGetValue(id,out var peer))rosterBuilder.Append(string.IsNullOrWhiteSpace(peer.State?.Name)?"Loading player...":peer.State!.Name);}
  cachedRoster=rosterBuilder.ToString();cachedLobbyNote=usePassword?"Password protected":"Open lobby · no password";
 }
 void ConnectionErrorPage(){
  if(connectionIssue is not {} issue)return;
  var nextCode=issue.Code??"";var nextTitle=issue.Title??"";var nextHint=issue.Hint??"";var nextVersions=issue.Versions??"";
  issueCodeContent??=new GUIContent();issueTitleContent??=new GUIContent();issueHintContent??=new GUIContent();issueVersionsContent??=new GUIContent();
  if(issueCode!=nextCode||issueTitle!=nextTitle||issueHint!=nextHint||issueVersions!=nextVersions){
   issueCode=nextCode;issueTitle=nextTitle;issueHint=nextHint;issueVersions=nextVersions;
   issueCodeContent.text="Connection error · "+issueCode;issueTitleContent.text=issueTitle;issueHintContent.text=issueHint;issueVersionsContent.text=issueVersions;
  }
  activeTextField=0;menuTooltip="";cachedTooltip="";tooltipAmount=0;
  Rounded(new Rect(0,0,640,252),new Color(.66f,.35f,.29f,.10f),8);
  if(Event.current?.type==EventType.Repaint){
   var old=GUI.color;GUI.color=new Color(1,.77f,.66f,old.a);GUI.Label(new Rect(16,12,608,22),issueCodeContent,textStyle!);GUI.color=old;
   GUI.Label(new Rect(16,39,608,55),issueTitleContent,connectionErrorTitleStyle!);
   GUI.Label(new Rect(16,102,608,76),issueHintContent,textStyle!);
   if(issueVersions.Length>0){Fill(new Rect(16,188,608,1),new Color(1,1,1,.09f));GUI.Label(new Rect(16,198,608,44),issueVersionsContent,textStyle!);}
  }
  if(MenuButton(new Rect(0,286,200,36),"Try again",true)){connectionIssue=null;connectionUiError="";StartSession(connectionMode==0);return;}
  if(MenuButton(new Rect(214,286,200,36),"Edit connection")){connectionIssue=null;connectionUiError="";}
 }
 void ConnectionPage(){
  if(Network!=null){RefreshRoster();Text(0,0,PageWidth,25,Network.Hosting?"You are hosting":"Connected to a friend");Rounded(new Rect(0,34,640,112),Card);Text(12,44,616,100,cachedRoster);
   if(Network.Hosting){ReadLocalAddresses();Text(0,165,640,20,"Your IP",true);Text(0,188,640,25,localAddresses);Text(0,216,640,22,cachedLobbyNote,true);}
   Text(0,252,640,26,"Meet at home. The host controls travel between floors.",true);if(MenuButton(new Rect(392,286,248,36),"Leave lobby & return home"))StopSession();return;}
  if(connectionIssue!=null){ConnectionErrorPage();return;}
  if(MenuButton(new Rect(0,0,150,30),"Create lobby",selected:connectionMode==0))connectionMode=0;if(QuietButton(new Rect(162,0,150,30),"Join friend",selected:connectionMode==1))connectionMode=1;
  Text(0,47,300,21,"Nickname · max 20 characters",true);nick=PanelText(new Rect(0,70,300,34),nick,3,NicknameRules.MaxCharacters);
  if(connectionMode==0){ReadLocalAddresses();Text(330,47,310,21,"Your IP",true);Rounded(new Rect(330,70,310,34),new Color(.8f,.85f,.86f,.07f),3);Label(new Rect(341,72,288,30),localAddresses,readOnlyFieldStyle!);SettingLabel(126,"Password protection","Optional. Friends can join an open lobby using only your IP.",520);usePassword=Switch(new Rect(568,124,72,32),usePassword,1);
   if(usePassword){Text(0,176,544,21,"Lobby password",true);code=PanelText(new Rect(0,198,544,34),code,2,64,!showCode);if(QuietButton(new Rect(556,198,84,34),showCode?"Hide":"Show"))showCode=!showCode;Text(0,249,640,27,"Share your IP and password. Friends join while you are at home.",true);}
   else Text(0,188,640,48,"Share your LAN or Radmin VPN IP with friends.\nEveryone joins while the host is at home.",true);
  }else{Text(330,47,310,21,"Friend's IP",true);address=PanelText(new Rect(330,70,228,34),address,1,128);if(QuietButton(new Rect(568,70,72,34),"Paste"))address=LimitField(GUIUtility.systemCopyBuffer.Trim(),1,128);Text(0,126,544,21,"Lobby password · optional",true);code=PanelText(new Rect(0,148,544,34),code,2,64,!showCode);if(QuietButton(new Rect(556,148,84,34),showCode?"Hide":"Show"))showCode=!showCode;Text(0,209,640,48,"Use the host's LAN or VPN IP. Leave the password empty for an open lobby.\nEveryone needs the same mod version.",true);}
  if(MenuButton(new Rect(440,286,200,36),connectionMode==0?"Create lobby":"Join friend",true)){StartSession(connectionMode==0);connectionUiError=Network==null?(detail.Length>0?detail:status):"";}
  if(status!="Offline"&&Network==null)Tooltip(new Rect(440,286,200,36),status+" "+detail);if(detail.Length>0&&Network==null)Tooltip(new Rect(0,-35,640,29),detail);
 }
 void RulesPage(){
  var edit=Network==null||Network.Hosting;var layoutEdit=edit&&!SharedRun&&!PartyScaling.Building;var v=edit?ModSettings.Read():Rules;var fire=v.FriendlyFire;var revive=v.ReviveHealthPercent;var length=v.FloorStep;var enemies=v.EnemyStep;var two=v.LootTwo;var three=v.LootThree;var four=v.LootFour;
  v.FriendlyFire=StepRow(0,"Friendly fire (% damage)",v.FriendlyFire*100,1,7,100,"0",edit,"Your bullets deal this percentage of normal damage to teammates. Players can still walk through one another.")/100;
  v.ReviveHealthPercent=(int)StepRow(36,"Revive health (% of max HP)",v.ReviveHealthPercent,1,1,20,"0",edit,"A downed player spectates until the next floor, then returns with this percentage of their own maximum HP.");
  v.FloorStep=StepRow(72,"Extra floor length per player (×)",v.FloorStep,.05f,.5f,1,"0.00",layoutEdit,"Adds this multiple of base floor length for each extra player, before the 33% co-op room reduction. Native minimum layouts and fixed boss arenas are preserved.");
  v.EnemyStep=StepRow(108,"Extra enemies per player (×)",v.EnemyStep,.1f,.8f,2,"0.0",layoutEdit,"Adds this multiple of the native enemy density for each extra player. Default 0.8 gives ×1 / ×1.8 / ×2.6 / ×3.4. Spawn safety still applies.");
  const string lootHelp="Sale value of newly collected shared valuables, including guard loot, safes and cabinets. One item can be taken by one player. Weapons and ammo boxes remain personal.";
  v.LootTwo=(int)StepRow(144,"Loot value · 2 players (×)",v.LootTwo,1,1,2,"0",layoutEdit,lootHelp);v.LootThree=(int)StepRow(180,"Loot value · 3 players (×)",v.LootThree,1,1,4,"0",layoutEdit,lootHelp);v.LootFour=(int)StepRow(216,"Loot value · 4 players (×)",v.LootFour,1,1,6,"0",layoutEdit,lootHelp);
  var count=Connected?Math.Clamp(PartySize,2,4):4;var loot=v.Loot(count);
  if(previewPlayers!=count||previewFloor!=v.FloorStep||previewEnemies!=v.EnemyStep||previewLoot!=loot){previewPlayers=count;previewFloor=v.FloorStep;previewEnemies=v.EnemyStep;previewLoot=loot;rulesPreview=count+" players · base floor ×"+v.Length(count).ToString("0.##",CultureInfo.InvariantCulture)+" · enemies ×"+v.Enemies(count).ToString("0.##",CultureInfo.InvariantCulture)+" · loot ×"+loot;}
  Text(0,255,640,25,rulesPreview,true);
  if(v.EnemyStep>1.2f||v.FloorStep>.75f){var old=GUI.color;GUI.color=new Color(1,.79f,.48f,old.a);Text(0,288,640,38,"Warning: extreme settings may cause lag or crashes.",true);GUI.color=old;}
  if(edit&&(fire!=v.FriendlyFire||revive!=v.ReviveHealthPercent||length!=v.FloorStep||enemies!=v.EnemyStep||two!=v.LootTwo||three!=v.LootThree||four!=v.LootFour)){ModSettings.Friendly.Value=v.FriendlyFire*100;ModSettings.Revive.Value=v.ReviveHealthPercent;ModSettings.FloorStep.Value=v.FloorStep;ModSettings.EnemyStep.Value=v.EnemyStep;ModSettings.LootTwo.Value=v.LootTwo;ModSettings.LootThree.Value=v.LootThree;ModSettings.LootFour.Value=v.LootFour;ApplyRuleSettings();}
 }
 void GraphicsPage(){
  if(advancedGraphics){if(QuietButton(new Rect(0,0,190,28),"←  Back to display"))advancedGraphics=false;Performance.ShadowDistance.Value=StepRow(52,"Shadow distance (m)",Performance.ShadowDistance.Value,5,10,100,help:"Maximum distance for shadows in Balanced and Fast presets.");Performance.LocalShadowDistance.Value=StepRow(102,"Nearby shadows (m)",Performance.LocalShadowDistance.Value,2,4,30,help:"Distance for nearby local-light shadows. Lighting stays enabled.");Performance.PointShadows.Value=(int)StepRow(152,"Point lights with shadows",Performance.PointShadows.Value,1,0,8,"0",help:"Maximum nearby point lights that cast shadows. This changes shadows, not the lights themselves.");Performance.SpotShadows.Value=(int)StepRow(202,"Spotlights with shadows",Performance.SpotShadows.Value,1,0,16,"0",help:"Maximum nearby spotlights that cast shadows. Your flashlight stays enabled.");Text(0,273,640,46,"Balanced and Fast presets only.\nLighting and your flashlight stay enabled.",true);return;}
  for(var i=0;i<3;i++){var x=i*218;if(MenuButton(new Rect(x,0,204,32),GraphicsModes[i],selected:Performance.Active==i))Performance.Mode.Value=i;Text(x,39,204,25,GraphicsDescriptions[i],true);}
  SettingLabel(92,"Sharper image","Disables FSR blur. GPU cost may increase.",520);Performance.NativeResolution.Value=Switch(new Rect(568,90,72,32),Performance.NativeResolution.Value,2);
  SettingLabel(140,"Menu background blur","Softens the scene only while this menu is open. It uses the existing local blur effect.",520);Plugin.Instance.MenuBlur.Value=Switch(new Rect(568,138,72,32),Plugin.Instance.MenuBlur.Value,3);
  SettingLabel(194,"First-person spectator view","Use the teammate's camera position and view angle. Space switches between first and third person; left/right mouse switches teammates.",520);SpectatorFirstPerson.Value=Switch(new Rect(568,192,72,32),SpectatorFirstPerson.Value,4);
  SpectatorDistance.Value=StepRow(237,"Third-person distance (m)",SpectatorDistance.Value,.5f,.5f,5,enabled:!SpectatorFirstPerson.Value,help:"Distance behind the teammate in third person. Default: 2.5 m. Left mouse: next teammate. Right mouse: previous teammate.");
  if(QuietButton(new Rect(0,284,220,32),"Light & shadow settings  →"))advancedGraphics=true;Text(428,290,212,24,"F10  Cycle graphics presets",true);
 }
 void AboutPage(){Text(0,0,640,27,"LAN co-op for up to four players.");Text(0,44,640,74,"Your money, stash, upgrades and quests remain personal.\nLeaving a lobby saves your progress and returns you home.\nItems left in the shared world stay there.",true);Text(0,137,640,74,"Downed players spectate until the next floor.\nRevive health uses each player's own maximum HP.\nJoin at home using the host's LAN or Radmin VPN IP.",true);Text(0,252,640,62,"After playing, run Collect-Multiplayer-Logs.cmd in the game folder.\nIt creates a local diagnostic ZIP.",true);}
}
