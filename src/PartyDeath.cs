using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 public bool Down;public bool PartyWiped;int downFloor=-1,downEpochFloor=-1;string downWorld="",downEpochWorld="";float wipeCloseAt,downSendAt;bool downAcknowledged,downHadLayout,downEpochHadLayout;
 readonly HashSet<int> downPlayers=new();PlayerInput? deathInput;bool restoreDeathInput;
 internal static bool PlayingNativeDeath;
 readonly HashSet<IntPtr> allowedNativeDeathSteps=new();
 CardsManager? pendingReviveCards;
 Vector3? reviveAnchor;
 void RememberArrival(Vector3 anchor){reviveAnchor=anchor;}
 void ResetPartyDeath(){Down=false;PartyWiped=false;downPlayers.Clear();allowedNativeDeathSteps.Clear();pendingReviveCards=null;downFloor=downEpochFloor=-1;downWorld=downEpochWorld="";downHadLayout=downEpochHadLayout=false;wipeCloseAt=0;deathInput=null;restoreDeathInput=false;reviveAnchor=null;}
 [HideFromIl2Cpp]
 internal bool NativeDeathStep(PlayerHealthManager health,IntPtr iterator){
  // Native Damage inlines Die's async startup, bypassing the public wrapper.
  // The wipe's own state machine must remain allowed across asynchronous awaits.
  if(allowedNativeDeathSteps.Contains(iterator))return true;
  if(PlayingNativeDeath||PartyWiped){if(allowedNativeDeathSteps.Count<8)allowedNativeDeathSteps.Add(iterator);return true;}
  return PlayerDeath(health);
 }
 [HideFromIl2Cpp]
 public bool PlayerDeath(PlayerHealthManager health){
  if(PlayingNativeDeath)return true;
  if(PartyWiped||Down)return false;
  if(!SharedRun||Network==null)return true;
  Down=true;downFloor=floor;downWorld=world;downHadLayout=floorLayoutReady;downAcknowledged=false;downSendAt=0;health.Health=0;health.died=true;
  deathInput=player?player.GetComponent<PlayerInput>():null;restoreDeathInput=deathInput&&deathInput.inputIsActive;if(deathInput)deathInput.DeactivateInput();
  detail="You are down. Next floor restores "+Rules.ReviveHealthPercent+"% of your own maximum HP.";
  GameplayLog.Record("player_downed",new{floor,world,localId=Network.LocalId,partySize=PartySize});
  if(Network.Hosting)MarkDown(Network.LocalId);else SendDown();
  return false;
 }
 // Native Damage starts its death state machine only below zero. An exact
 // lethal hit can therefore leave a local guest at Health0 without a death.
 // Reconcile only the completed native damage call for the canonical local actor;
 // never poll health, add damage, bypass armor/dodge or alter native HP.
 [HideFromIl2Cpp]
 bool OwnCoopDamageActor(PlayerHealthManager health)=>SharedRun&&Network!=null&&Connected&&!Down&&!PartyWiped&&!PlayingNativeDeath&&!Recovering&&player&&player.Health&&health&&health.Pointer==player.Health.Pointer&&!health.died;
 [HideFromIl2Cpp]
 internal int ExactZeroDamageBefore(PlayerHealthManager health)=>OwnCoopDamageActor(health)?health.Health:-1;
 [HideFromIl2Cpp]
 internal void CompleteCoopLethalDamage(PlayerHealthManager health,int before){
  if(before<=0||!OwnCoopDamageActor(health)||health.Health>0)return;
  // The usual negative-HP native path already calls PlayerDeath and is excluded
  // by Down/died above. Reuse the existing once-only cooperative death route.
  PlayerDeath(health);
 }
 [HideFromIl2Cpp]
 void PrepareDownEpoch(){if(downEpochFloor<0||PartyFloorProgress.Advanced(downEpochFloor,downEpochHadLayout,floor,floorLayoutReady,world!=downEpochWorld)){downPlayers.Clear();downEpochFloor=floor;downEpochWorld=world;downEpochHadLayout=floorLayoutReady;}}
 void MarkDown(int id){PrepareDownEpoch();if(!downPlayers.Add(id))return;var packet=Message("playerdown");packet.TargetId=id;Network!.Send(packet);}
 void SendDown(){var packet=Message("down");packet.Floor=downFloor;packet.World=downWorld;Network!.Send(packet);downSendAt=Time.unscaledTime+1;}
 void ReviveLocal(){
  StopSpectator();
  if(SharedRun&&PartyFloorProgress.Advanced(downFloor,downHadLayout,floor,floorLayoutReady,world!=downWorld)&&reviveAnchor is {} anchor&&player){var cc=player.GetComponent<CharacterController>();if(cc)cc.enabled=false;player.transform.position=anchor;var move=player.GetComponent<Demo.Scripts.Runtime.Character.FPSMovement>();if(move){move._velocity=Vector3.zero;move.MoveVector=Vector3.zero;}if(cc)cc.enabled=true;}
  var health=Object.FindObjectOfType<PlayerHealthManager>();if(health){health.died=false;health.Health=Rules.ReviveHp(health.MaxHealth);}
  Down=false;if(restoreDeathInput&&!panel&&player){var input=player.GetComponent<PlayerInput>();if(input)input.ActivateInput();}deathInput=null;restoreDeathInput=false;reviveAnchor=null;detail="Revived with "+Rules.ReviveHealthPercent+"% HP.";
  GameplayLog.Record("player_revived",new{floor,world,hp=health?health.Health:0,maximum=health?health.MaxHealth:0,percent=Rules.ReviveHealthPercent});
 }
 void NativePartyWipe(){
  if(PartyWiped)return;pendingReviveCards=null;StopSpectator();PartyWiped=true;wipeCloseAt=Time.unscaledTime+1;
  var health=Object.FindObjectOfType<PlayerHealthManager>();if(!health)return;
  health.died=false;PlayingNativeDeath=true;try{health.Die();}finally{PlayingNativeDeath=false;}
 }
 void PartyDeathTick(){
  if(pendingReviveCards){
   if(Network==null||PartyWiped||scene is "Example_Scene_Night 1" or "MainMenu")pendingReviveCards=null;
   else if(SharedRun&&!Down&&!panel&&player&&player.Health&&!player.Health.died&&player.Health.Health>0){var cards=pendingReviveCards;pendingReviveCards=null;cards.Show();}
  }
  if(PartyWiped){
   if(Network!=null&&Time.unscaledTime>=wipeCloseAt)StopSession();
   // Clear only after the native respawn has created a healthy home player.
   // Keeping the flags through the replay prevents a second native death.
   if(Network==null&&scene=="Example_Scene_Night 1"&&player){var homeHealth=Object.FindObjectOfType<PlayerHealthManager>();if(homeHealth&&!homeHealth.died&&homeHealth.Health>0)ResetPartyDeath();}
   return;
  }
  if(!SharedRun||Network==null){if(Down&&scene=="Example_Scene_Night 1")ReviveLocal();return;}
  if(Down&&PartyFloorProgress.Advanced(downFloor,downHadLayout,floor,floorLayoutReady,world!=downWorld)&&SameWorld&&elevator&&!elevator.isMoving)ReviveLocal();
  if(Down&&Network.Hosting==false&&!downAcknowledged&&Connected&&Time.unscaledTime>=downSendAt)SendDown();
  if(downEpochFloor<0){downEpochFloor=floor;downEpochWorld=world;downEpochHadLayout=floorLayoutReady;}
  if(PartyFloorProgress.Advanced(downEpochFloor,downEpochHadLayout,floor,floorLayoutReady,world!=downEpochWorld)&&SameWorld&&elevator&&!elevator.isMoving){downPlayers.Clear();downEpochFloor=floor;downEpochWorld=world;downEpochHadLayout=floorLayoutReady;}
  if(Network.Hosting){
   // A floor transition revives each player's own HP locally; old down notices
   // must not cause a wipe while the new floor is loading.
   if(downPlayers.Count>0&&downPlayers.Contains(Network.LocalId)&&remotes.Keys.All(downPlayers.Contains)){
    var wipe=Message("wipe");Network.Send(wipe);NativePartyWipe();
   }
  }
 }
 [HideFromIl2Cpp]
 bool ReceivePartyDeath(Packet p){
  if(p.Kind=="down"&&Network?.Hosting==true){if(SharedRun&&p.Seed==Network.Seed&&p.Floor==floor&&p.Scene==scene&&p.World==world&&remotes.ContainsKey(p.PlayerId)){MarkDown(p.PlayerId);var ack=Message("playerdown");ack.TargetId=p.PlayerId;Network.SendTo(ack,p.PlayerId);}return true;}
  if(p.Kind=="playerdown"&&Network?.Hosting==false){if(SharedRun&&p.Seed==Network.Seed&&p.Floor==floor&&p.Scene==scene&&p.World==world){PrepareDownEpoch();downPlayers.Add(p.TargetId);if(p.TargetId==Network.LocalId)downAcknowledged=true;}return true;}
  if(p.Kind=="wipe"&&Network?.Hosting==false){if(SharedRun&&p.Seed==Network.Seed)NativePartyWipe();return true;}
  return false;
 }
 [HideFromIl2Cpp]
 internal bool ShowCoopCards(CardsManager cards){
  if(PartyWiped)return true;
  // Down is owned through scene/floor readiness changes. Keep the already
  // generated offer rather than opening it during a transient !SharedRun.
  if(Down){if(Network!=null)pendingReviveCards=cards;return false;}
  if(!SharedRun||Network==null)return true;
  if(!Down&&player&&player.Health&&!player.Health.died&&player.Health.Health>0)return true;
  pendingReviveCards=cards;return false;
 }
}
[HarmonyPatch(typeof(PlayerHealthManager),nameof(PlayerHealthManager.Die))]
static class PartyDeathIntercept {static bool Prefix(PlayerHealthManager __instance)=>Together.Current?.PlayerDeath(__instance)??true;}

[HarmonyPatch]
static class PartyDeathStateMachineIntercept
{
 static PropertyInfo owner=null!,state=null!,builder=null!;
 static MethodBase TargetMethod(){
  var machine=typeof(PlayerHealthManager).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic).Single(t=>t.Name.StartsWith("_Die_d__",StringComparison.Ordinal)||t.Name.Contains("<Die>d__",StringComparison.Ordinal));
  var properties=machine.GetProperties(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
  owner=properties.Single(p=>p.PropertyType==typeof(PlayerHealthManager));
  state=properties.Single(p=>p.Name=="__1__state"&&p.PropertyType==typeof(int)&&p.CanRead&&p.CanWrite);
  builder=properties.Single(p=>p.PropertyType==typeof(Il2CppSystem.Runtime.CompilerServices.AsyncVoidMethodBuilder)&&p.CanRead&&p.CanWrite);
  return machine.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!;
 }
 static bool Prefix(object __instance){
  var current=Together.Current;if(current==null)return true;
  var health=owner.GetValue(__instance) as PlayerHealthManager;
  if(!health||current.NativeDeathStep(health,((Il2CppObjectBase)__instance).Pointer))return true;
  CompleteBlockedInitial(__instance);return false;
 }
 static void CompleteBlockedInitial(object machine){
  try{
   if(state.GetValue(machine) is not int initial||initial!=-1)return;
   // No await has been started. Mark completion before the callback so a
   // repeated/reentrant MoveNext can never complete this builder twice.
   state.SetValue(machine,-2);
   var completion=builder.GetValue(machine) as Il2CppSystem.Runtime.CompilerServices.AsyncVoidMethodBuilder;
   if(completion==null)throw new InvalidOperationException("Native death builder unavailable");
   // Interop returns a boxed value-type copy; persist SetResult's changes.
   try{completion.SetResult();}finally{builder.SetValue(machine,completion);}
  }catch(Exception error){GameplayLog.Fault("native_death_cancel",error);}
 }
}

[HarmonyPatch]
static class CooperativeDeathVisualGuard
{
 static IEnumerable<MethodBase> TargetMethods()=>typeof(PlayerHealthManager).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic).Where(t=>t.Name.Contains("DeathAnimation")||t.Name.Contains("DeathVideoCanvas")).Select(t=>t.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)).OfType<MethodBase>();
 static bool Prefix(ref bool __result){if(Together.Current is not {Down:true,PartyWiped:false})return true;__result=false;return false;}
}
[HarmonyPatch(typeof(CardsManager),nameof(CardsManager.Show))]
static class CooperativeCardOfferGuard
{
 static bool Prefix(CardsManager __instance)=>Together.Current?.ShowCoopCards(__instance)??true;
}

// This pinned Damage RVA977850 is unique in the shipped metadata. The normal
// original, its result, armor/dodge and exception behavior remain untouched.
[HarmonyPatch(typeof(PlayerHealthManager),nameof(PlayerHealthManager.Damage))]
static class CompleteExactCoopLethalDamage
{
 static void Prefix(PlayerHealthManager __instance,out int __state)=>__state=Together.Current?.ExactZeroDamageBefore(__instance)??-1;
 static void Postfix(PlayerHealthManager __instance,int __state,bool __runOriginal){
  if(__runOriginal&&__state>0)Together.Current?.CompleteCoopLethalDamage(__instance,__state);
 }
}
