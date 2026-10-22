using HarmonyLib;
using UnityEngine;
namespace FractureTogether;

// Observability only: do not replace, clamp, repeat or suppress native damage.
// Values are bounded scalar snapshots; no names, addresses or packet payloads.
static class DamageDiagnostics
{
 const int PerSecond=32;
 static float windowUntil;static int windowCount;static long suppressed;
 [ThreadStatic] static int networkDepth;
 internal readonly struct NetworkScope:IDisposable {
  readonly int before;
  internal NetworkScope(int prior){before=prior;networkDepth=prior+1;}
  public void Dispose()=>networkDepth=before;
 }
 internal static NetworkScope NetworkReplay()=>new(networkDepth);
 static bool Admit(bool severe,out long skipped){
  skipped=0;if(!GameplayLog.Operational)return false;
  var now=Time.realtimeSinceStartup;if(now>=windowUntil){windowUntil=now+1;windowCount=0;}
  if(!severe&&windowCount>=PerSecond){suppressed++;return false;}
  windowCount++;skipped=suppressed;suppressed=0;return true;
 }
 internal struct NativeState {
  internal bool Active,EnemyBullet,PlayerBullet,NetworkRoute;
  internal int Before,Maximum,Requested,Frame,ActorId;internal int? ProjectileOwner;
 }
 internal static NativeState Before(PlayerHealthManager health,int amount){
  if(!GameplayLog.Operational||!health)return default;
  return new NativeState{Active=true,Before=health.Health,Maximum=health.MaxHealth,Requested=amount,Frame=Time.frameCount,ActorId=health.GetInstanceID(),EnemyBullet=Together.EnemyBullet,PlayerBullet=Together.PlayerBullet,ProjectileOwner=Together.ProjectileOwner,NetworkRoute=networkDepth>0};
 }
 internal static void After(PlayerHealthManager health,NativeState state,Exception? error){
  if(!state.Active||!health)return;var after=health.Health;var loss=state.Before-after;
  var severe=state.Before>0&&after<=0||loss>=Math.Max(20,state.Maximum/2);
  if(!Admit(severe,out var skipped))return;var current=Together.Current;
  GameplayLog.Record("damage_native",new{frame=state.Frame,actorId=state.ActorId,requested=state.Requested,before=state.Before,after,maxHp=state.Maximum,maxHpAfter=health.MaxHealth,loss,enemyBullet=state.EnemyBullet,playerBullet=state.PlayerBullet,projectileOwner=state.ProjectileOwner,networkRoute=state.NetworkRoute,down=current?.Down??false,partyWiped=current?.PartyWiped??false,floor=current?.floor??-1,world=current?.world??"",exceptionType=error?.GetType().Name,suppressed=skipped});
 }
 internal static void Deliver(int target,float amount,int localId){
  try{if(!Admit(false,out var skipped))return;var current=Together.Current;
   GameplayLog.Record("damage_delivery",new{frame=Time.frameCount,target,amount,localId,enemyBullet=Together.EnemyBullet,playerBullet=Together.PlayerBullet,projectileOwner=Together.ProjectileOwner,networkRoute=networkDepth>0,friendlyRule=current?.Rules.FriendlyFire??0,floor=current?.floor??-1,world=current?.world??"",suppressed=skipped});
  }catch(Exception error){GameplayLog.Fault("damage_diagnostics",error);}
 }
 internal static void Friendly(int source,int target,float raw,float rule,long sequence){
  try{if(!Admit(false,out var skipped))return;
   GameplayLog.Record("damage_friendly",new{frame=Time.frameCount,source,target,raw,rule,scaled=raw*rule,sequence,suppressed=skipped});
  }catch(Exception error){GameplayLog.Fault("damage_diagnostics",error);}
 }
}

[HarmonyPatch(typeof(PlayerHealthManager),nameof(PlayerHealthManager.Damage))]
static class NativePlayerDamageDiagnostics
{
 [HarmonyPriority(Priority.First)]
 static void Prefix(PlayerHealthManager __instance,int __0,out DamageDiagnostics.NativeState __state){
  __state=default;try{__state=DamageDiagnostics.Before(__instance,__0);}catch(Exception error){GameplayLog.Fault("damage_diagnostics",error);}
 }
 [HarmonyPriority(Priority.Last)]
 static Exception? Finalizer(PlayerHealthManager __instance,DamageDiagnostics.NativeState __state,Exception? __exception){
  try{DamageDiagnostics.After(__instance,__state,__exception);}catch(Exception error){GameplayLog.Fault("damage_diagnostics",error);}return __exception;
 }
}
