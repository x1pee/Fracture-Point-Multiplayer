using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
namespace FractureTogether;

// Only the authored Victor approach button. This is not a generic UnityEvent RPC.
static class BossDoorWire
{
 internal const int MaximumDoors=16;
 internal static bool Valid(Packet p){
  if(p.Kind is not ("bossdoorreq" or "bossdoorstate")||p.ObjectId.Length is <10 or >192||!p.ObjectId.StartsWith("bossdoor:",StringComparison.Ordinal)||p.Sequence<=0||p.MachineId!=""||p.TargetId!=-1||p.Category!=0||p.Count!=0||p.Amount!=0||p.Direction!=0)return false;
  if(p.Kind=="bossdoorreq")return p.Pose.Length==0&&p.ItemId==0&&!p.Success&&!p.ByHand;
  return p.Pose.Length==3&&p.Pose.All(v=>float.IsFinite(v)&&Math.Abs(v)<=10000)&&p.ItemId is 0 or 1;
 }
}

public sealed partial class Together
{
 sealed class BossDoorEntry {
  internal string Id="";internal EventOnInteraction Button=null!;internal Collider Input=null!;
  internal Animation Animation=null!;internal Transform Leaf=null!;
  internal bool Started,Moving;internal float OpenedAt,SendAt;
  internal long Sequence;internal Packet? Snapshot;
  internal Vector3 From,To;internal float ReceivedAt;internal bool Interpolating;
 }
 readonly Dictionary<string,BossDoorEntry> bossDoors=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,string> bossDoorIds=new();readonly List<BossDoorEntry> bossDoorOrder=new();
 readonly Dictionary<string,Packet> bossDoorDeferred=new(StringComparer.Ordinal);
 readonly Dictionary<int,float> bossDoorRequestAt=new();
 Session? bossDoorSession;string bossDoorWorld="",bossDoorScene="";int bossDoorFloor=int.MinValue;
 long bossDoorLocalSequence,bossDoorHostSequence;int bossDoorCursor;float bossDoorTickAt,bossDoorRetryAt,bossDoorPendingUntil;
 Packet? bossDoorPending;bool bossDoorVisualWork;

 [HideFromIl2Cpp]
 void BossDoorReset(){
  // References only: these are native arena objects, never owned clones.
  bossDoors.Clear();bossDoorIds.Clear();bossDoorOrder.Clear();bossDoorDeferred.Clear();bossDoorRequestAt.Clear();
  bossDoorSession=null;bossDoorWorld=bossDoorScene="";bossDoorFloor=int.MinValue;bossDoorCursor=0;
  bossDoorPending=null;bossDoorVisualWork=false;bossDoorTickAt=bossDoorRetryAt=bossDoorPendingUntil=0;
 }
 [HideFromIl2Cpp]
 void EnsureBossDoorEpoch(){
  if(ReferenceEquals(bossDoorSession,Network)&&bossDoorWorld==world&&bossDoorScene==scene&&bossDoorFloor==floor)return;
  BossDoorReset();bossDoorSession=Network;bossDoorWorld=world;bossDoorScene=scene;bossDoorFloor=floor;
  // Sequences remain monotonic for this Together instance across world resets.
 }
 [HideFromIl2Cpp]
 static bool VictorApproachButton(EventOnInteraction? button)=>button&&button!.name=="SM_Elevator_Buttons02"&&button.transform.parent&&button.transform.parent.name.Replace("(Clone)","").Trim()=="BossFight_Corridor";
 [HideFromIl2Cpp]
 void RegisterBossDoorNode(Transform node){
  try{if(!node||node.name!="SM_Elevator_Buttons02")return;var button=node.GetComponent<EventOnInteraction>();if(VictorApproachButton(button))BindBossDoor(button!);}
  catch(Exception error){GameplayLog.Fault("boss_door_discovery",error);}
 }
 [HideFromIl2Cpp]
 BossDoorEntry? BindBossDoor(EventOnInteraction button){
  EnsureBossDoorEpoch();
  if(bossDoorIds.TryGetValue(button.Pointer,out var oldId)&&bossDoors.TryGetValue(oldId,out var old)&&old.Button&&old.Leaf)return old;
  if(bossDoors.Count>=BossDoorWire.MaximumDoors||!VictorApproachButton(button)||button.gameObject.scene.name!=scene||scene!="URP_OfficeCorridor"||button.gameObject.scene.handle!=SceneManager.GetActiveScene().handle)return null;
  var scope=SharedBirthRoot();if(floor>0&&(!scope||!button.transform.IsChildOf(scope)))return null;
  var calls=button.Event?.m_PersistentCalls?.m_Calls;if(calls==null||calls.Count!=5||button.Hold||button.Description!="Open the door")return null;
  for(var i=0;i<5;i++)if(calls[i].m_CallState!=UnityEventCallState.RuntimeOnly||!calls[i].m_Target)return null;
  var input=calls[0].m_Target.TryCast<Collider>();var trigger=calls[1].m_Target.TryCast<OnTriggerAnim>();
  var audio=calls[2].m_Target.TryCast<AudioSource>();var activate=calls[3].m_Target.TryCast<ActivateBosses>();var hide=calls[4].m_Target.TryCast<GameObject>();
  if(!input||input!.gameObject.Pointer!=button.gameObject.Pointer||calls[0].m_MethodName!="set_enabled"||calls[0].m_Mode!=PersistentListenerMode.Bool||calls[0].m_Arguments==null||calls[0].m_Arguments.m_BoolArgument)return null;
  if(!trigger||trigger!.gameObject.Pointer!=button.gameObject.Pointer||calls[1].m_MethodName!="Trigger"||calls[1].m_Mode!=PersistentListenerMode.Void||trigger.InUpdate||trigger.delayInSeconds!=0||trigger.clip!="garagedoor1")return null;
  var animation=trigger.animation;var corridor=button.transform.parent;
  if(!animation||animation!.name!="GarageDoor"||!animation.transform.parent||animation.transform.parent.Pointer!=corridor.Pointer||animation.gameObject.scene.handle!=button.gameObject.scene.handle)return null;
  if(!audio||audio!.gameObject.Pointer!=animation.gameObject.Pointer||calls[2].m_MethodName!="Play"||calls[2].m_Mode!=PersistentListenerMode.Void)return null;
  if(!activate||activate!.gameObject.Pointer!=button.gameObject.Pointer||calls[3].m_MethodName!="Activate"||calls[3].m_Mode!=PersistentListenerMode.Void)return null;
  if(!hide||hide!.Pointer!=button.gameObject.Pointer||calls[4].m_MethodName!="SetActive"||calls[4].m_Mode!=PersistentListenerMode.Bool||calls[4].m_Arguments==null||calls[4].m_Arguments.m_BoolArgument)return null;
  var leaf=animation.transform.Find("GameObject");var state=animation[trigger.clip];
  if(!leaf||leaf!.parent.Pointer!=animation.transform.Pointer||state==null||!float.IsFinite(state.length)||state.length<=0||state.length>10)return null;
  // Saved clip5135 has exactly one localPosition curve for this direct child;
  // its solid DoorSection8 collider moves with the child. Never disable it.
  var id="bossdoor:"+SpatialKey(button.transform);if(id.Length>192)return null;
  if(bossDoors.TryGetValue(id,out var duplicate))return duplicate.Button&&duplicate.Button.Pointer==button.Pointer?duplicate:null;
  var entry=new BossDoorEntry{Id=id,Button=button,Input=input,Animation=animation,Leaf=leaf};
  bossDoors.Add(id,entry);bossDoorIds[button.Pointer]=id;bossDoorOrder.Add(entry);return entry;
 }
 [HideFromIl2Cpp]
 bool BossDoorHostReady()=>Network?.Hosting==true&&Connected&&RulesReady&&SharedRun&&SameWorld&&SharedActorWorldReady&&!PartyWiped&&!returningAfterDisconnect&&!PersonalSavePending&&!(elevator&&elevator.isMoving)&&SceneManager.GetActiveScene().name==scene;
 [HideFromIl2Cpp]
 bool BossDoorPeerPoint(int id,out Vector3 point){
  point=default;if(Network==null)return false;
  if(id==Network.LocalId){if(!player||!player.Health||player.Health.died||player.Health.Health<=0||Down)return false;point=player.transform.position;return true;}
  if(!remotes.TryGetValue(id,out var remote)||!PeerMatches(remote)||remote.State!.Health<=0||downPlayers.Contains(id))return false;
  var p=remote.State.Pose;point=new Vector3(p[0],p[1],p[2]);return true;
 }
 [HideFromIl2Cpp]
 internal bool BossDoorInteract(EventOnInteraction button){
  try{
   if(!VictorApproachButton(button))return true;
   // A retained session with a lost connection is not genuine offline play.
   if(returningAfterDisconnect)return false;if(Network==null)return true;if(!Connected)return false;
   var entry=BindBossDoor(button);if(entry==null){detail="This boss door is not ready.";return false;}
   if(entry.Started)return false;
   if(!RulesReady||!SharedRun||!SharedActorWorldReady||!SameWorld||!BossDoorPeerPoint(Network!.LocalId,out _))return false;
   if(bossDoorPending!=null)return false;
   var request=Message("bossdoorreq");request.ObjectId=entry.Id;request.Sequence=++bossDoorLocalSequence;request.PlayerId=Network.LocalId;
   if(Network.Hosting)HandleBossDoorRequest(request);
   else{bossDoorPending=request;bossDoorPendingUntil=Time.unscaledTime+10;bossDoorRetryAt=Time.unscaledTime+1;Network.Send(request);}
  }catch(Exception error){GameplayLog.Fault("boss_door_request",error);}
  return false;
 }
 [HideFromIl2Cpp]
 void HandleBossDoorRequest(Packet request){
  if(!BossDoorWire.Valid(request)||!PacketWorld(request)||request.Seed!=Network?.Seed||!BossDoorHostReady())return;
  EnsureBossDoorEpoch();
  var now=Time.unscaledTime;if(bossDoorRequestAt.TryGetValue(request.PlayerId,out var at)&&now<at)return;
  // Only current authenticated members reach this map; at most four entries.
  if(!BossDoorPeerPoint(request.PlayerId,out var point))return;bossDoorRequestAt[request.PlayerId]=now+.25f;
  if(!bossDoors.TryGetValue(request.ObjectId,out var entry)||!entry.Button||!entry.Input||!entry.Leaf)return;
  if(entry.Started){if(entry.Snapshot!=null)Network!.SendTo(entry.Snapshot,request.PlayerId);return;}
  if(!entry.Button.gameObject.activeInHierarchy||!entry.Input.enabled||entry.Input.isTrigger||Vector3.Distance(point,entry.Input.bounds.ClosestPoint(point))>7)return;
  // Reserve BEFORE native side effects; duplicate requests and uncertain native
  // failures never run ActivateBosses twice. No guest replays this event.
  entry.Started=true;entry.Moving=true;entry.OpenedAt=Time.time;entry.SendAt=0;
  try{entry.Button.Event.Invoke();}
  catch(Exception error){GameplayLog.Fault("boss_door_native",error);}
  CaptureBossDoor(entry,true);
 }
 [HideFromIl2Cpp]
 void CaptureBossDoor(BossDoorEntry entry,bool force){
  if(!entry.Button||!entry.Input||!entry.Animation||!entry.Leaf){entry.SendAt=Time.unscaledTime+2;return;}
  var now=Time.unscaledTime;if(!force&&now<entry.SendAt)return;
  if(entry.Moving){
   // Native Animation uses scaled game time; a pause cannot prematurely turn
   // its unfinished pose into the durable endpoint.
   var moving=Time.time<entry.OpenedAt+.1f||entry.Animation.isPlaying;
   var p=entry.Leaf.localPosition;if(!float.IsFinite(p.x)||!float.IsFinite(p.y)||!float.IsFinite(p.z)||Math.Abs(p.x)>10000||Math.Abs(p.y)>10000||Math.Abs(p.z)>10000){entry.SendAt=now+2;throw new InvalidOperationException("Native boss door position is invalid.");}
   var snapshot=Message("bossdoorstate");snapshot.ObjectId=entry.Id;snapshot.Sequence=++bossDoorHostSequence;snapshot.Pose=new[]{p.x,p.y,p.z};
   snapshot.ItemId=moving?1:0;snapshot.Success=entry.Button.gameObject.activeSelf;snapshot.ByHand=entry.Input.enabled;
   if(!BossDoorWire.Valid(snapshot))return;entry.Snapshot=snapshot;entry.Moving=moving;
  }
  entry.SendAt=now+(entry.Moving ? .1f : 2);
  if(entry.Snapshot!=null)Network!.Send(entry.Snapshot);
 }
 [HideFromIl2Cpp]
 void ApplyBossDoorState(BossDoorEntry entry,Packet p){
  if(p.Sequence<=entry.Sequence||!entry.Button||!entry.Input||!entry.Leaf||!entry.Animation)return;
  entry.Started=true;
  // Stop any competing legacy playback, but never Play/Sample/Invoke a guest
  // animation, button event, encounter callback, audio callback or native AI.
  entry.Animation.Stop();
  var target=new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]);
  entry.From=entry.Leaf.localPosition;entry.To=target;entry.ReceivedAt=Time.unscaledTime;entry.Interpolating=p.ItemId==1;if(entry.Interpolating)bossDoorVisualWork=true;
  if(!entry.Interpolating)entry.Leaf.localPosition=target;
  entry.Input.enabled=p.ByHand;if(entry.Button.gameObject.activeSelf!=p.Success)entry.Button.gameObject.SetActive(p.Success);
  // Presentation writes are idempotent. Commit only after they succeed so a
  // repeated durable host snapshot can repair a transient native read error.
  entry.Sequence=p.Sequence;
  if(bossDoorPending?.ObjectId==p.ObjectId)bossDoorPending=null;
 }
 [HideFromIl2Cpp]
 bool ReceiveBossDoor(Packet p){
  if(p.Kind is not ("bossdoorreq" or "bossdoorstate"))return false;
  if(!Connected||!BossDoorWire.Valid(p)||!PacketWorld(p)||p.Seed!=Network!.Seed)return true;
  EnsureBossDoorEpoch();
  try{
   if(p.Kind=="bossdoorreq"){if(Network.Hosting)HandleBossDoorRequest(p);return true;}
   if(Network.Hosting||p.PlayerId!=0)return true;
   if(SharedActorWorldReady&&bossDoors.TryGetValue(p.ObjectId,out var entry)&&entry.Button&&entry.Leaf)ApplyBossDoorState(entry,p);
   else if(bossDoorDeferred.TryGetValue(p.ObjectId,out var old)){if(p.Sequence>old.Sequence)bossDoorDeferred[p.ObjectId]=p;}
   else if(bossDoorDeferred.Count<BossDoorWire.MaximumDoors)bossDoorDeferred[p.ObjectId]=p;
  }catch(Exception error){GameplayLog.Fault("boss_door_state",error);}
  return true;
 }
 [HideFromIl2Cpp]
 void BossDoorTick(){
  if(Network==null){if(bossDoorSession!=null)BossDoorReset();return;}EnsureBossDoorEpoch();
  if(!SharedRun||!Connected||!SharedActorWorldReady)return;
  var now=Time.unscaledTime;
  if(Network.Hosting){
   if(now<bossDoorTickAt)return;bossDoorTickAt=now+.1f;
   // Round robin, <=4 captures/sends per tick, <=16 retained arena doors.
   var budget=4;for(var n=0;n<bossDoorOrder.Count&&budget>0;n++){
    if(bossDoorCursor>=bossDoorOrder.Count)bossDoorCursor=0;var entry=bossDoorOrder[bossDoorCursor++];
    if(!entry.Started||now<entry.SendAt)continue;budget--;
    try{CaptureBossDoor(entry,false);}catch(Exception error){entry.SendAt=now+2;GameplayLog.Fault("boss_door_capture",error);}
   }
  }else{
   if(bossDoorPending!=null){if(now>bossDoorPendingUntil)bossDoorPending=null;else if(now>=bossDoorRetryAt){bossDoorRetryAt=now+1;Network.Send(bossDoorPending);}}
   if(!bossDoorVisualWork&&bossDoorDeferred.Count==0)return;var busy=false;
   foreach(var entry in bossDoorOrder)try{
    if(bossDoorDeferred.TryGetValue(entry.Id,out var p)){bossDoorDeferred.Remove(entry.Id);ApplyBossDoorState(entry,p);}
    if(entry.Interpolating&&entry.Leaf){var t=Mathf.Clamp01((now-entry.ReceivedAt)/.1f);entry.Leaf.localPosition=Vector3.Lerp(entry.From,entry.To,t);if(t>=1)entry.Interpolating=false;else busy=true;}
   }catch(Exception error){entry.Interpolating=false;GameplayLog.Fault("boss_door_apply",error);}
   bossDoorVisualWork=busy;
  }
 }
}

[HarmonyPatch(typeof(EventOnInteraction),nameof(EventOnInteraction.Interact))]
static class VictorApproachButtonSync
{
 static bool Prefix(EventOnInteraction __instance){
  // RVA7E6440 is also shared by unrelated native event callbacks. Check the
  // actual IL2CPP class before accessing any EventOnInteraction field.
  var button=__instance.TryCast<EventOnInteraction>();
  return button?Together.Current?.BossDoorInteract(button)??true:true;
 }
}
