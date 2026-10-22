using GameLocalization;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

// Passive discovery only. Native explicit regeneration, scene/language refresh,
// text preprocessing and text-change/fitting callbacks retain their own paths.
public static class IncrementalLocalization
{
 const int MaxTexts=4096,MaxFonts=1024,MaxFontCache=2048,MaxDepth=4096;
 const float FallbackInterval=30;
 struct Cursor {internal Transform Node;internal int Child;internal bool Inspected;internal Cursor(Transform node){Node=node;Child=0;Inspected=false;}}
 static readonly Stack<Cursor> walk=new(128);
 static readonly Queue<TMP_Text> texts=new();static readonly HashSet<IntPtr> queuedTexts=new();
 static readonly Queue<TMP_FontAsset> fonts=new();static readonly HashSet<IntPtr> queuedFonts=new();
 static readonly Dictionary<IntPtr,float> checkedFonts=new();
 static readonly TMP_FontAsset?[] fontSnapshot=new TMP_FontAsset?[128];
 static bool checkingFont;static IntPtr inFlightFont;static int generation;static float fontRetryAt;static string fontStage="font";
 static Il2CppArrayBase<GameObject>? roots;static int rootIndex,sceneHandle=int.MinValue;
 static bool requested,wasEnabled;static float textAt,fontAt;
 public static long Passes,Nodes,Texts,FontChecks,FontsPatched,Dropped,Failures;
 public static int LastNodes,LastSteps,LastTexts,LastFonts;public static double LastMilliseconds;
 public static string LastError="";

 static void Clear(){generation++;fontRetryAt=0;walk.Clear();roots=null;rootIndex=0;texts.Clear();queuedTexts.Clear();fonts.Clear();queuedFonts.Clear();checkedFonts.Clear();}
 internal static void Invalidate(){Clear();textAt=fontAt=0;requested=true;}
 internal static void Cancel(){Clear();requested=false;wasEnabled=false;sceneHandle=int.MinValue;textAt=fontAt=0;}
 internal static void RequestTexts(){if(!UiScanBudget.Enabled)return;var now=Time.unscaledTime;if(now<textAt)return;textAt=now+FallbackInterval;requested=true;}
 internal static void RequestFonts(){if(!UiScanBudget.Enabled)return;var now=Time.unscaledTime;if(now<fontAt)return;fontAt=now+FallbackInterval;requested=true;}
 internal static void EnqueueText(TMP_Text text){
  if(!UiScanBudget.Enabled||UiScanBudget.NativeRefresh||!text||!text.gameObject.scene.IsValid()||queuedTexts.Contains(text.Pointer))return;
  if(texts.Count>=MaxTexts){Dropped++;return;}queuedTexts.Add(text.Pointer);texts.Enqueue(text);
 }
 internal static void EnqueueFont(TMP_FontAsset font){
  if(!UiScanBudget.Enabled||UiScanBudget.NativeRefresh||!font||font.Pointer==inFlightFont||queuedFonts.Contains(font.Pointer))return;
  if(checkedFonts.TryGetValue(font.Pointer,out var until)&&Time.unscaledTime<until)return;
  if(fonts.Count>=MaxFonts){Dropped++;return;}queuedFonts.Add(font.Pointer);fonts.Enqueue(font);
 }
 static bool FontBaseReady()=>LocFonts._baseReady&&(LocFonts._condRegular||LocFonts._sansRegular);
 static bool CheckFont(TMP_FontAsset font){
  // Exact per-font body from native PatchLoadedFontAssets. Preserve the native
  // distinction: IsOurs includes the prefix; HasOurs checks live Created fonts.
  if(checkingFont)return false;checkingFont=true;inFlightFont=font.Pointer;var version=generation;var copied=0;
  try{
   fontStage="font-IsOurs";if(!font||LocFonts.IsOurs(font))return true;
   fontStage="font-HasOurs";var table=font.fallbackFontAssetTable;if(LocFonts.HasOurs(table))return true;
   // Pinned FallbackOrder returns a freshly populated native List, never a lazy
   // iterator. Snapshot by index before destination mutation; the generic
   // interface enumerator bridge is unnecessary and failed in native guest QA.
   fontStage="font-order";var order=LocFonts.FallbackOrder(font).Cast<Il2CppSystem.Collections.Generic.List<TMP_FontAsset>>();
   var count=order.Count;if(count>fontSnapshot.Length)throw new InvalidOperationException("Native font fallback order exceeded snapshot capacity");
   fontStage="font-snapshot";
   for(var i=0;i<count;i++){if(generation!=version||i>=order.Count)return false;fontSnapshot[copied++]=order[i];}
   if(generation!=version||order.Count!=count||!font)return false;
   fontStage="font-append";table=font.fallbackFontAssetTable;
   if(table==null){table=new Il2CppSystem.Collections.Generic.List<TMP_FontAsset>();font.fallbackFontAssetTable=table;}
   for(var i=0;i<copied;i++){if(generation!=version)return false;var fallback=fontSnapshot[i];if(fallback&&!table.Contains(fallback)){table.Add(fallback);FontsPatched++;}}
   return generation==version;
  }finally{Array.Clear(fontSnapshot,0,copied);inFlightFont=IntPtr.Zero;checkingFont=false;}
 }
 static void Failed(Exception error,string stage="discovery"){Failures++;LastError=stage+": "+error.GetType().Name+": "+error.Message;}
 static bool WithinBudget(long deadline)=>Stopwatch.GetTimestamp()<deadline;
 public static void Tick(){
  if(!UiScanBudget.Enabled){if(wasEnabled||requested||texts.Count>0||fonts.Count>0)Cancel();return;}
  wasEnabled=true;LastNodes=LastSteps=LastTexts=LastFonts=0;LastMilliseconds=0;
  var started=Stopwatch.GetTimestamp();var deadline=started+Math.Max(1,Stopwatch.Frequency/500);
  var scene=SceneManager.GetActiveScene();
  if(scene.handle!=sceneHandle){Invalidate();sceneHandle=scene.handle;}
  if(!scene.IsValid()||!scene.isLoaded)return;
  RequestTexts();RequestFonts();
  var fontBaseReady=false;if(fonts.Count>0&&Time.unscaledTime>=fontRetryAt){try{fontBaseReady=FontBaseReady();}catch(Exception e){Failed(e,"font-base");fontRetryAt=Time.unscaledTime+.5f;}}
  while(fontBaseReady&&fonts.Count>0&&LastFonts<4&&WithinBudget(deadline)){
   var font=fonts.Dequeue();queuedFonts.Remove(font.Pointer);LastFonts++;FontChecks++;
   if(!font)continue;
   var version=generation;var complete=false;
   try{complete=CheckFont(font);}catch(Exception e){Failed(e,fontStage);fontRetryAt=Time.unscaledTime+.5f;}
   if(!complete||version!=generation){if(version==generation)EnqueueFont(font);break;}
   if(checkedFonts.Count>=MaxFontCache)checkedFonts.Clear();checkedFonts[font.Pointer]=Time.unscaledTime+5;
  }
  while(texts.Count>0&&LastTexts<8&&WithinBudget(deadline)){
   var text=texts.Dequeue();queuedTexts.Remove(text.Pointer);LastTexts++;Texts++;
   if(!text)continue;
   var stage="text-install";try{
    LocTextHook.Install(text);
    stage="text-diag";
    if(text.textPreprocessor?.TryCast<LocTextHook>()!=null&&text.IsActive()&&LocManager.Active)LocDiag.Check(text);
   }catch(Exception e){Failed(e,stage);}
  }
  if(roots==null&&requested&&WithinBudget(deadline)){
   // Root inventory is small; no full-scene/global component query is used.
   roots=scene.GetRootGameObjects();rootIndex=0;requested=false;Passes++;
  }
  while(roots!=null&&LastNodes<32&&LastSteps<64&&WithinBudget(deadline)){
   LastSteps++;
   if(walk.Count==0){
    if(rootIndex>=roots.Length){roots=null;break;}
    var root=roots[rootIndex++];if(root)walk.Push(new Cursor(root.transform));continue;
   }
   var cursor=walk.Pop();if(!cursor.Node)continue;
   if(!cursor.Inspected){
    cursor.Inspected=true;LastNodes++;Nodes++;
    // Graphic disallows multiple components; Unity's native attribute helper
    // walks base types, covering TMP_Text and legacy Text as well. Single
    // local queries avoid two empty native arrays on every ordinary 3D node.
    try{EnqueueText(cursor.Node.GetComponent<TMP_Text>());IncrementalLegacyLocalization.Discover(cursor.Node.GetComponent<UnityEngine.UI.Text>());}catch(Exception e){Failed(e);}
    walk.Push(cursor);continue;
   }
   if(cursor.Child>=cursor.Node.childCount)continue;
   var child=cursor.Node.GetChild(cursor.Child++);walk.Push(cursor);
   // Retain the parent's child index: a parent with 10,000 children cannot
   // enqueue them all in one frame. A depth cap also bounds retained cursors.
   if(child){if(walk.Count<MaxDepth)walk.Push(new Cursor(child));else Dropped++;}
  }
  LastMilliseconds=(Stopwatch.GetTimestamp()-started)*1000d/Stopwatch.Frequency;
 }
}

[HarmonyPatch(typeof(TextMeshPro),"OnEnable")]
static class DiscoverEnabledWorldText {static void Postfix(TextMeshPro __instance)=>IncrementalLocalization.EnqueueText(__instance);}
[HarmonyPatch(typeof(TextMeshProUGUI),"OnEnable")]
static class DiscoverEnabledCanvasText {static void Postfix(TextMeshProUGUI __instance)=>IncrementalLocalization.EnqueueText(__instance);}
[HarmonyPatch(typeof(LocTextHook),nameof(LocTextHook.Install))]
static class DiscoverTextFont {
 // Never enqueue text from Install: native text-change callbacks already call
 // Install, and doing so would create a recursive passive work stream.
 static void Postfix(TMP_Text t){if(UiScanBudget.Enabled&&!UiScanBudget.NativeRefresh&&t)IncrementalLocalization.EnqueueFont(t.font);}
}
