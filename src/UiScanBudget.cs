using GameLocalization;
using HarmonyLib;
using UnityEngine;
namespace FractureTogether;

// Text changes already have native localization callbacks. Full discovery is a fallback.
// Preserve forced refreshes and invalidate immediately on scene/language changes.
public static class UiScanBudget
{
 internal static float FontAt,TextAt,LegacyAt,LegacyScanAt;
 static int discoveryFrame=-1,nativeRefreshDepth;
 internal static bool Enabled=>Performance.LocalizationScans?.Value==true;
 internal static bool NativeRefresh=>nativeRefreshDepth>0;
 public static int Discoveries,Deferred;
 public static void Reset(){FontAt=TextAt=LegacyAt=LegacyScanAt=0;discoveryFrame=-1;IncrementalLocalization.Invalidate();}
 internal static void BeginNativeRefresh(){nativeRefreshDepth++;Reset();}
 internal static void EndNativeRefresh(){if(nativeRefreshDepth>0)nativeRefreshDepth--;}
 internal static bool Due(ref float deadline,float interval,bool discovery=false){
  if(Performance.LocalizationScans?.Value!=true)return true;
  var now=Time.unscaledTime;if(now<deadline||discovery&&discoveryFrame==Time.frameCount){Deferred++;return false;}
  deadline=now+interval;if(discovery){discoveryFrame=Time.frameCount;Discoveries++;}return true;
 }
}
[HarmonyPatch(typeof(LocFonts),nameof(LocFonts.PatchLoadedFontAssets))]
static class CachedFontDiscovery {
 static bool Prefix(){if(!UiScanBudget.Enabled){IncrementalLocalization.Cancel();return true;}if(UiScanBudget.NativeRefresh)return true;IncrementalLocalization.RequestFonts();return false;}
}
[HarmonyPatch(typeof(LocTextHook),nameof(LocTextHook.ScanAll))]
static class CachedTextDiscovery {
 static bool Prefix(bool regenerate,ref int __result){if(!UiScanBudget.Enabled){IncrementalLocalization.Cancel();return true;}if(regenerate||UiScanBudget.NativeRefresh)return true;IncrementalLocalization.RequestTexts();__result=0;return false;}
}
[HarmonyPatch(typeof(LocLegacyText),nameof(LocLegacyText.Scan))]
static class CachedLegacyDiscovery {static bool Prefix(bool force){if(!UiScanBudget.Enabled||force||UiScanBudget.NativeRefresh)return true;IncrementalLocalization.RequestTexts();return false;}}
[HarmonyPatch(typeof(LocLegacyText),nameof(LocLegacyText.Tick))]
static class BudgetLegacyLocalization {static bool Prefix()=>UiScanBudget.Due(ref UiScanBudget.LegacyAt,.1f);}
[HarmonyPatch(typeof(LocFonts),nameof(LocFonts.Apply))]
static class RefreshLanguageBudget {
 static void Prefix()=>UiScanBudget.BeginNativeRefresh();
 static Exception? Finalizer(Exception? __exception){UiScanBudget.EndNativeRefresh();return __exception;}
}
[HarmonyPatch(typeof(LocBootstrap),"OnSceneLoaded")]
static class RefreshSceneBudget {
 // Native OnSceneLoaded calls ScanAll(false), so the bool alone cannot
 // distinguish its mandatory refresh from the periodic passive fallback.
 static void Prefix()=>UiScanBudget.BeginNativeRefresh();
 static Exception? Finalizer(Exception? __exception){UiScanBudget.EndNativeRefresh();return __exception;}
}
[HarmonyPatch(typeof(LocFonts),nameof(LocFonts.Release))]
static class ReleasedLocalizationFonts {static void Prefix()=>UiScanBudget.Reset();}
