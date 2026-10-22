using GameLocalization;
using HarmonyLib;
using UnityEngine.UI;

namespace FractureTogether;

// Native recipe: LocLegacyText.Scan at RVA 0x683130.
// 0x683354..0x6833A5: Unity-live Text and a valid scene (inactive is allowed).
// 0x6833AB..0x683400: skip an existing key in the native Tracked dictionary.
// 0x683406..0x683507: exclude roots beginning "[Graphy]" or "Quantum Console"
// using the native string.StartsWith(string) semantics.
// 0x683509..0x683571: allocate an empty Entry and assign Tracked[originalText].
// Source/Output are deliberately left null; native Tick reads and updates them.
// The original scan has no active/enabled/language/input-field admission filter.
static class IncrementalLegacyLocalization
{
 internal static void Discover(Text text){
  if(!UiScanBudget.Enabled||UiScanBudget.NativeRefresh||!text||!text.gameObject.scene.IsValid())return;
  var tracked=LocLegacyText.Tracked;
  if(tracked==null||tracked.ContainsKey(text))return;
  var root=text.transform.root;
  if(root){var name=root.name;if(name.StartsWith("[Graphy]")||name.StartsWith("Quantum Console"))return;}
  // Keep Unity Text as the key and the game's own Entry object as the value;
  // native Tick retains its equality, source-change and dead-key cleanup logic.
  tracked[text]=new LocLegacyText.Entry();
 }
}

// Text's own nontrivial override is unique at RVA 0x384F280: it enables the
// graphic, invalidates its text generator and calls FontUpdateTracker.TrackText.
// Do not target UIBehaviour.OnEnable (shared no-op RVA 0x531840).
[HarmonyPatch(typeof(Text),"OnEnable")]
static class DiscoverEnabledLegacyText
{
 static void Postfix(Text __instance)=>IncrementalLegacyLocalization.Discover(__instance);
}
