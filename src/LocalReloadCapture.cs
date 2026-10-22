using Demo.Scripts.Runtime.Character;
using HarmonyLib;
using UnityEngine;
using Firearm=Demo.Scripts.Runtime.Item.Weapon;
namespace FractureTogether;

// Accepted local native action only. No input polling, inventory/health writes,
// proxy Weapon, remote OnReload, synthetic bullets, sounds or gameplay callback.
static class LocalReloadCapture
{
 static Firearm? owner;static FPSController? controller;static object? session;
 static string world="",scene="";static int floor;static float started;static int kind,reports;
 internal static long Sequence{get;private set;}
 internal static int Kind=>kind;
 internal static float Elapsed=>kind==0?0:Mathf.Clamp(Time.unscaledTime-started,0,ReloadPresentationPolicy.MaxElapsed);
 internal static bool Owns(Firearm weapon){
  var t=Together.Current;var p=t?.LocalController;if(t==null||!t.Connected||!t.SameWorld||t.Down||t.PartyWiped||t.Recovering||!p||!p!.enabled||!p.Health||p.Health.Health<=0||p.Health.died||!weapon||!weapon.gameObject.activeInHierarchy)return false;
  var active=p.GetActiveItem();return active&&active!.Pointer==weapon.Pointer;
 }
 internal static void Begin(Firearm weapon,int acceptedKind){
  if(!Owns(weapon)||acceptedKind is <1 or >2||Sequence==long.MaxValue)return;
  var t=Together.Current!;owner=weapon;controller=t.LocalController;session=t.Network;world=t.world;scene=t.scene;floor=t.floor;started=Time.unscaledTime;kind=acceptedKind;Sequence++;
  if(reports++<32)GameplayLog.Record("avatar.reload_local",new{phase="start",sequence=Sequence,kind,weaponId=weapon.WeaponItem?.Id??-1});
 }
 internal static void End(Firearm weapon,string reason){if(kind!=0&&owner&&weapon&&owner!.Pointer==weapon.Pointer)Stop(reason);}
 internal static bool OwnsController(FPSController player)=>kind!=0&&controller&&player&&controller!.Pointer==player.Pointer;
 internal static void Throw(FPSController player){if(OwnsController(player))Stop("native_accepted_throw");}
 static void Stop(string reason){if(kind==0)return;var prior=kind;var elapsed=Elapsed;kind=0;owner=null;controller=null;session=null;if(reports++<32)GameplayLog.Record("avatar.reload_local",new{phase="stop",sequence=Sequence,prior,elapsed,reason});}
 internal static void Tick(FPSController? player){
  if(kind==0)return;var t=Together.Current;
  if(t==null||!ReferenceEquals(t.Network,session)||t.world!=world||t.scene!=scene||t.floor!=floor||!owner||!controller||!player||controller!.Pointer!=player!.Pointer||!Owns(owner!)){Stop("owner_world_or_lifecycle");return;}
  // This only ends a start already accepted by native OnReload. It never
  // infers a new reload from PlayingAnimation (which also covers grenades).
  if(controller._actionState!=FPSActionState.PlayingAnimation){Stop("native_action_ended");return;}
  if(Time.unscaledTime-started>ReloadPresentationPolicy.MaxElapsed)Stop("stale_action_timeout");
 }
 internal static void Reset(){owner=null;controller=null;session=null;world=scene="";floor=-1;started=0;kind=0;Sequence=0;reports=0;}
}
[HarmonyPatch(typeof(Firearm),nameof(Firearm.OnReload))]
static class CaptureAcceptedReload
{
 static void Prefix(Firearm __instance,out int __state){__state=LocalReloadCapture.Owns(__instance)&&__instance.SelectedMag!=null?(__instance.SelectedMag.AmmoCount==0?2:1):0;}
 static void Postfix(Firearm __instance,int __state,bool __result,bool __runOriginal){if(__runOriginal&&__result&&__state!=0)LocalReloadCapture.Begin(__instance,__state);}
}
[HarmonyPatch(typeof(Firearm),nameof(Firearm.OnActionEnded))]
static class CaptureNativeReloadEnd {static void Postfix(Firearm __instance,bool __runOriginal){if(__runOriginal)LocalReloadCapture.End(__instance,"native_OnActionEnded");}}
[HarmonyPatch(typeof(Firearm),nameof(Firearm.OnUnEquip))]
static class CaptureReloadUnequip {static void Postfix(Firearm __instance,bool __runOriginal){if(__runOriginal)LocalReloadCapture.End(__instance,"native_unequip");}}
[HarmonyPatch(typeof(FPSController),nameof(FPSController.OnThrowGrenade))]
static class CaptureReloadInterruptedByThrow {
 static void Prefix(FPSController __instance,out bool __state)=>__state=LocalReloadCapture.OwnsController(__instance)&&__instance._actionState==FPSActionState.None;
 static void Postfix(FPSController __instance,bool __state,bool __runOriginal){if(__runOriginal&&__state&&__instance._actionState==FPSActionState.PlayingAnimation)LocalReloadCapture.Throw(__instance);}
}
