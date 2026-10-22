using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Attributes;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 readonly Dictionary<string,OnTriggerAction> worldTriggers=new();
 readonly Dictionary<string,Packet> pendingTriggers=new();
 readonly HashSet<string> firedTriggers=new();readonly Dictionary<string,PeerTimeline> triggerRequests=new();
 readonly Dictionary<string,long> triggerApplied=new();
 readonly Il2CppReferenceArray<Collider> nearbyTriggerColliders=new(64);
 readonly Dictionary<int,float> triggerResolveAt=new();
 readonly Dictionary<string,Packet> deferredTriggerDone=new();
 readonly string?[] triggerDoneApplyKeys=new string?[4];
 string triggerEpoch="";float triggerScanAt,triggerRetryAt;long triggerSequence,triggerBroadcast;
 readonly Stack<(Transform Node,int Child)> triggerNodes=new();readonly HashSet<IntPtr> visitedTriggerNodes=new();
 Il2CppArrayBase<GameObject>? triggerRoots;int triggerRootIndex,triggerSceneHandle=int.MinValue,triggerRevision=-1,triggerRootCount=-1;
 Il2CppArrayBase<OnTriggerAction>? triggerCandidates;int triggerCandidateIndex;bool triggerDiscoveryRequested=true;
 public int TriggersSent,TriggersApplied;
 [HideFromIl2Cpp]
 internal static bool SharedWorldTrigger(OnTriggerAction trigger){
  var calls=trigger.Event?.m_PersistentCalls?.m_Calls;if(calls==null||calls.Count==0)return false;
  var worldAction=false;
  foreach(var call in calls){
   if(call.m_CallState==UnityEngine.Events.UnityEventCallState.Off)continue;
   var target=call.m_Target;if(!target)return false;var method=call.m_MethodName;
   if(target.TryCast<LevelElevator>()!=null&&method is "set_enabled" or "PlayStartupAnim" or "StartMove"){worldAction=true;continue;}
   if(target.TryCast<EnemySpawner>()!=null&&method=="Spawn"){worldAction=true;continue;}
   if(target.TryCast<GameObject>() is {} npc&&method=="SetActive"&&call.m_Arguments?.m_BoolArgument==true&&npc.GetComponentInChildren<EnemyAI.EnemyHealth>(true)){worldAction=true;continue;}
   if(target.TryCast<GameObject>() is {} unit&&method=="SetActive"&&call.m_Arguments?.m_BoolArgument==true&&unit.GetComponentsInChildren<Ballistics.LivingEntity>(true).Any(e=>e.Boss&&!e.PlayerHealthManager)){worldAction=true;continue;}
   if(target.TryCast<Collider>()!=null&&method=="set_enabled")continue;
   if(target.TryCast<AudioSource>()!=null&&method is "Play" or "Stop")continue;
   // Personal quest, inventory and upgrade callbacks are deliberately local.
   return false;
  }
  return worldAction;
 }
 static bool TriggerOnce(OnTriggerAction trigger){
  if(trigger.singleAction)return true;
  foreach(var call in trigger.Event.m_PersistentCalls.m_Calls)if(call.m_Target&&call.m_Target.TryCast<Collider>() is {} collider&&collider.gameObject==trigger.gameObject&&call.m_MethodName=="set_enabled"&&call.m_Arguments?.m_BoolArgument==false)return true;
  return false;
 }
 string RegisterTrigger(OnTriggerAction trigger){var key=SpatialKey(trigger.transform);worldTriggers[key]=trigger;return key;}
 void ResetTriggerDiscovery(){
  triggerRoots=null;triggerRootIndex=0;triggerNodes.Clear();visitedTriggerNodes.Clear();triggerCandidates=null;triggerCandidateIndex=0;
  triggerResolveAt.Clear();
  triggerSceneHandle=int.MinValue;triggerRevision=-1;triggerRootCount=-1;triggerScanAt=0;triggerDiscoveryRequested=true;
 }
 void BeginTriggerDiscovery(Scene active){
  triggerRoots=active.GetRootGameObjects();triggerRootIndex=0;triggerNodes.Clear();visitedTriggerNodes.Clear();
  triggerCandidates=null;triggerCandidateIndex=0;triggerRootCount=active.rootCount;triggerDiscoveryRequested=false;
 }
 void WalkTriggerDiscovery(){
  var classify=8;var walk=64;
  while(triggerRoots!=null){
   if(triggerCandidates!=null){
    while(triggerCandidateIndex<triggerCandidates.Length&&classify>0){
     var trigger=triggerCandidates[triggerCandidateIndex++];classify--;
     if(trigger&&trigger.gameObject.scene.handle==triggerSceneHandle&&SharedWorldTrigger(trigger))RegisterTrigger(trigger);
    }
    if(triggerCandidateIndex<triggerCandidates.Length)break;
    triggerCandidates=null;triggerCandidateIndex=0;
   }
   if(walk--<=0)break;
   if(triggerNodes.Count==0){
    if(triggerRootIndex>=triggerRoots.Length){triggerRoots=null;visitedTriggerNodes.Clear();break;}
    var root=triggerRoots[triggerRootIndex++];if(root&&root.scene.handle==triggerSceneHandle)triggerNodes.Push((root.transform,-1));
    continue;
   }
   var frame=triggerNodes.Pop();var node=frame.Node;if(!node||node.gameObject.scene.handle!=triggerSceneHandle)continue;
   if(frame.Child<0){
    if(!visitedTriggerNodes.Add(node.Pointer))continue;
    triggerNodes.Push((node,0));RegisterBossDoorNode(node);triggerCandidates=node.GetComponents<OnTriggerAction>();
   }else if(frame.Child<node.childCount){
    // Resume the parent's child cursor next time instead of pushing every child
    // in one frame: a large GeneratedLevel root must respect the same budget.
    triggerNodes.Push((node,frame.Child+1));triggerNodes.Push((node.GetChild(frame.Child),-1));
   }
  }
 }
 void WorldTriggerTick(){
  if(Network==null||!SharedRun){if(Network==null)deferredTriggerDone.Clear();ResetTriggerDiscovery();return;}
  EnsureTriggerEpoch();
  var active=SceneManager.GetActiveScene();var revision=DungeonSeedPatch.Revision;
  if(triggerSceneHandle!=active.handle||triggerRevision!=revision){ResetTriggerDiscovery();triggerSceneHandle=active.handle;triggerRevision=revision;}
  // Root count is scene metadata, not a global object census. A detached exit
  // elevator becomes a root and schedules another bounded pass. Inactive module
  // descendants are included in the original pass, before later activation.
  if(Time.unscaledTime>=triggerScanAt){triggerScanAt=Time.unscaledTime+2;var roots=active.rootCount;if(roots!=triggerRootCount){triggerRootCount=roots;triggerDiscoveryRequested=true;}}
  if(!DungeonSeedPatch.Building){
   if(triggerRoots==null&&triggerDiscoveryRequested)BeginTriggerDiscovery(active);
   WalkTriggerDiscovery();
  }
  ApplyDeferredWorldTriggers();
  if(Network?.Hosting==false&&Time.unscaledTime>=triggerRetryAt){triggerRetryAt=Time.unscaledTime+1;foreach(var request in pendingTriggers.Values)Network.Send(request);}
 }
 void EnsureTriggerEpoch(){
  if(Network==null)return;var epoch=Network.Seed+":"+world;if(triggerEpoch==epoch)return;
  triggerEpoch=epoch;worldTriggers.Clear();pendingTriggers.Clear();firedTriggers.Clear();triggerApplied.Clear();triggerRequests.Clear();deferredTriggerDone.Clear();Array.Clear(triggerDoneApplyKeys,0,triggerDoneApplyKeys.Length);ResetTriggerDiscovery();
 }
 void ApplyDeferredWorldTriggers(){
  if(deferredTriggerDone.Count==0)return;var count=0;
  // Inspect at most the bounded 256 entries without mutating during enumeration.
  // Invoke native events only afterwards, outside the discovery/registration walk.
  foreach(var pair in deferredTriggerDone){if(worldTriggers.TryGetValue(pair.Key,out var trigger)&&trigger){triggerDoneApplyKeys[count++]=pair.Key;if(count==triggerDoneApplyKeys.Length)break;}}
  try{for(var i=0;i<count;i++){var key=triggerDoneApplyKeys[i]!;if(!deferredTriggerDone.TryGetValue(key,out var packet))continue;deferredTriggerDone.Remove(key);if(PacketWorld(packet)&&worldTriggers.TryGetValue(key,out var trigger)&&trigger)ApplyWorldTrigger(packet,trigger);}}
  finally{Array.Clear(triggerDoneApplyKeys,0,triggerDoneApplyKeys.Length);}
 }
 [HideFromIl2Cpp]
 public bool TriggerEnter(OnTriggerAction trigger,Collider other){
  if(!other)return false;
  // Native code consumes a single-use trigger BEFORE comparing the collider's
  // tag. Reject non-player colliders before that destructive native side effect.
  if(!other.CompareTag("Player"))return false;
  if(!SharedRun||Network==null||!SharedWorldTrigger(trigger))return true;
  if(Down||PartyWiped)return false;
  EnsureTriggerEpoch();
  var key=RegisterTrigger(trigger);if(firedTriggers.Contains(key)||pendingTriggers.ContainsKey(key))return false;
  var p=Message("triggerreq");p.ObjectId=key;p.PlayerId=Network.LocalId;p.Sequence=++triggerSequence;
  if(Network.Hosting)HandleWorldTrigger(p);else{pendingTriggers[key]=p;Network.Send(p);TriggersSent++;}
  return false;
 }
 static bool TriggerKeyPoint(string key,out Vector3 point){
  point=default;var end=key.LastIndexOf('|');if(end<=0)return false;var start=key.LastIndexOf('|',end-1);if(start<0)return false;
  var position=key.AsSpan(start+1,end-start-1);var first=position.IndexOf(',');if(first<=0)return false;var rest=position[(first+1)..];var second=rest.IndexOf(',');if(second<=0)return false;
  if(!int.TryParse(position[..first],NumberStyles.Integer,CultureInfo.InvariantCulture,out var x)||!int.TryParse(rest[..second],NumberStyles.Integer,CultureInfo.InvariantCulture,out var y)||!int.TryParse(rest[(second+1)..],NumberStyles.Integer,CultureInfo.InvariantCulture,out var z))return false;
  point=new Vector3(x/100f,y/100f,z/100f);return true;
 }
 OnTriggerAction? FindWorldTriggerNear(string key,Vector3 point){
  try{
   var count=Physics.OverlapSphereNonAlloc(point,2,nearbyTriggerColliders,~0,QueryTriggerInteraction.Collide);var active=SceneManager.GetActiveScene().handle;
   for(var i=0;i<count&&i<nearbyTriggerColliders.Length;i++){var collider=nearbyTriggerColliders[i];if(!collider||collider.gameObject.scene.handle!=active)continue;var trigger=collider.GetComponent<OnTriggerAction>();if(trigger&&SpatialKey(trigger.transform)==key&&SharedWorldTrigger(trigger)){RegisterTrigger(trigger);return trigger;}}
   return null;
  }finally{for(var i=0;i<nearbyTriggerColliders.Length;i++)nearbyTriggerColliders[i]=null!;}
 }
 bool TriggerInRange(OnTriggerAction trigger,Vector3 point){var collider=trigger.GetComponent<Collider>();if(!collider||!collider.enabled)return false;var bounds=collider.bounds;bounds.Expand(new Vector3(1.2f,3,1.2f));return bounds.Contains(point);}
 [HideFromIl2Cpp]
 OnTriggerAction? ResolveNearbyWorldTrigger(string key,int peerId,Vector3 point){
  // An exit can be reached before a large scene's bounded hierarchy walk ends.
  // Resolve only this authenticated request near the host's stored player pose;
  // do not consume its sequence until the normal range checks pass below.
  var now=Time.unscaledTime;if(triggerResolveAt.TryGetValue(peerId,out var until)&&now<until)return null;
  if(triggerResolveAt.Count>remotes.Count+1)foreach(var id in triggerResolveAt.Keys.Where(id=>id!=Network!.LocalId&&!remotes.ContainsKey(id)).ToArray())triggerResolveAt.Remove(id);
  triggerResolveAt[peerId]=now+1;
  return FindWorldTriggerNear(key,point);
 }
 void HandleWorldTrigger(Packet p){
  if(!SharedRun||!PacketWorld(p)||!SameWorld)return;
  EnsureTriggerEpoch();
  // Consumed native triggers can disable their own colliders. A retry still
  // needs its acknowledgement even when no overlap/range query can find them.
  if(firedTriggers.Contains(p.ObjectId)){var ack=Message("triggerdone");ack.ObjectId=p.ObjectId;ack.Sequence=triggerApplied.GetValueOrDefault(p.ObjectId,1);Network!.SendTo(ack,p.PlayerId);return;}
  Vector3 point;
  if(p.PlayerId==Network!.LocalId){if(!player||Down)return;point=player.transform.position;}
  else{if(!remotes.TryGetValue(p.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0||downPlayers.Contains(p.PlayerId))return;var pose=peer.State.Pose;point=new Vector3(pose[0],pose[1],pose[2]);}
  if(!worldTriggers.TryGetValue(p.ObjectId,out var trigger)||!trigger){
   trigger=ResolveNearbyWorldTrigger(p.ObjectId,p.PlayerId,point);
   // A saturated local query or a not-yet-enabled collider can still be found
   // by the bounded discovery pass; the guest retains its one-second retry.
   if(!trigger){triggerDiscoveryRequested=true;return;}
  }
  if(!TriggerInRange(trigger,point)||!SharedWorldTrigger(trigger))return;
  if(!triggerRequests.TryGetValue(p.ObjectId,out var receipts))triggerRequests[p.ObjectId]=receipts=new PeerTimeline();
  if(!receipts.Accept(p.PlayerId,p.Sequence))return;
  var done=Message("triggerdone");done.ObjectId=p.ObjectId;done.Sequence=++triggerBroadcast;
  Network.Send(done);ApplyWorldTrigger(done,trigger);
 }
 void ApplyWorldTrigger(Packet p,OnTriggerAction trigger){
  pendingTriggers.Remove(p.ObjectId);
  if(p.Sequence<=triggerApplied.GetValueOrDefault(p.ObjectId))return;
  if(deferredTriggerDone.TryGetValue(p.ObjectId,out var queued)&&queued.Sequence<=p.Sequence)deferredTriggerDone.Remove(p.ObjectId);
  triggerApplied[p.ObjectId]=p.Sequence;if(TriggerOnce(trigger))firedTriggers.Add(p.ObjectId);
  if(trigger.singleAction)trigger.activated=true;
  trigger.Event.Invoke();TriggersApplied++;
 }
 [HideFromIl2Cpp]
 bool ReceiveWorldTrigger(Packet p){
  if(p.Kind=="triggerreq"&&Network?.Hosting==true){HandleWorldTrigger(p);return true;}
  if(p.Kind=="triggerdone"&&Network?.Hosting==false){
   if(!PacketWorld(p))return true;EnsureTriggerEpoch();if(p.Sequence<=triggerApplied.GetValueOrDefault(p.ObjectId))return true;
   if(deferredTriggerDone.TryGetValue(p.ObjectId,out var queued)&&queued.Sequence>p.Sequence)p=queued;
   if(!worldTriggers.TryGetValue(p.ObjectId,out var trigger)||!trigger){if(TriggerKeyPoint(p.ObjectId,out var point))trigger=FindWorldTriggerNear(p.ObjectId,point);}
   if(trigger)ApplyWorldTrigger(p,trigger);
   else{
    if(deferredTriggerDone.TryGetValue(p.ObjectId,out var prior)){if(p.Sequence>prior.Sequence)deferredTriggerDone[p.ObjectId]=p;}
    else{if(deferredTriggerDone.Count>=256)throw new IOException("Authoritative world-trigger backlog exceeded 256 entries; stopping the session rather than losing a host event.");deferredTriggerDone[p.ObjectId]=p;}
    triggerDiscoveryRequested=true;
   }
   return true;
  }
  return false;
 }
}
[HarmonyPatch(typeof(OnTriggerAction),nameof(OnTriggerAction.OnTriggerEnter))]
static class SharedTriggerEntry {static bool Prefix(OnTriggerAction __instance,Collider other)=>Together.Current?.TriggerEnter(__instance,other)??(other&&other.CompareTag("Player"));}
