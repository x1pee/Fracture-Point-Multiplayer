using Ballistics;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Non-humanoid ballistic units share one authority path. Their native HP is preserved.
public sealed partial class Together
{
 sealed class UnitState
 {
  public LivingEntity Entity=null!;public Transform ActivationRoot=null!;public BTRTurretAI? Turret;public Rigidbody? RootBody;
  public string Key="";public UnitFrame? Frame;public long Sequence;public bool Frozen,Motion,Boss,TerminalApplied;public int Bucket;public float SendAt;public Helicopter[] Helicopters=Array.Empty<Helicopter>();
 }
 readonly Dictionary<string,UnitState> units=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,string> unitKeys=new();
 readonly Dictionary<IntPtr,LivingEntity> spawnedUnits=new();
 readonly Queue<IntPtr> spawnedUnitQueue=new();
 readonly Dictionary<string,(UnitFrame Frame,long Sequence)> pendingUnits=new(StringComparer.Ordinal);
 readonly Queue<string> pendingUnitResolve=new();readonly HashSet<string> queuedUnitResolve=new(StringComparer.Ordinal);
 readonly Queue<string> terminalUnitApply=new();readonly HashSet<string> queuedTerminalUnitApply=new(StringComparer.Ordinal);
 readonly Dictionary<string,float> unitResolveAt=new(StringComparer.Ordinal);
 readonly Il2CppReferenceArray<Collider> nearbyUnitColliders=new(64);
 readonly Dictionary<(int Player,long Sequence),(Packet Request,float Until)> pendingUnitHits=new();
 readonly PeerTimeline unitHits=new();string unitEpoch="";int unitSessionSeed;float unitSendAt;long unitSequence,unitHitSequence;
 public static bool ApplyingUnitState;
 public int NetworkUnitCount=>units.Count;
 [HideFromIl2Cpp]
 public object UnitDiagnostics()=>units.Values.Select(e=>new{e.Key,name=e.Entity?e.Entity.BossName:"destroyed",health=e.Entity?e.Entity.myHealth:0,maximum=e.Entity?e.Entity.StartHealth:0,active=e.Entity&&e.Entity.gameObject.activeInHierarchy,e.Frozen,e.Sequence,position=e.Entity?e.Entity.transform.position.ToString():null,rotation=e.Entity?e.Entity.transform.rotation.ToString():null}).ToArray();
 internal static bool IsNetworkUnit(LivingEntity entity)=>entity&&(entity.Boss||entity.ExplodingObject)&&!entity.EnemyHealth&&!entity.PlayerHealthManager;
 void ClearUnits(){units.Clear();unitKeys.Clear();spawnedUnits.Clear();spawnedUnitQueue.Clear();pendingUnits.Clear();pendingUnitResolve.Clear();queuedUnitResolve.Clear();terminalUnitApply.Clear();queuedTerminalUnitApply.Clear();unitResolveAt.Clear();pendingUnitHits.Clear();unitHits.Reset();unitEpoch="";unitSendAt=0;unitSequence=unitHitSequence=0;}
 // Floor zero starts in an authored street scene without a Dungeon/GeneratedLevel.
 // Its glass still has native LivingEntity/ExplodingObject health and authority.
 bool UnitWorldReady=>SharedActorWorldReady;
 void EnsureUnitEpoch(){if(Network==null||unitEpoch==world&&unitSessionSeed==Network.Seed)return;ClearUnits();unitEpoch=world;unitSessionSeed=Network.Seed;}
 static string UnitKey(LivingEntity entity){
  // Module-local identity survives flight, lift motion, animation and mutable elevator names.
  var path=new List<string>();var node=entity.transform;
  while(node.parent&&node.parent.name!="GeneratedLevel"){path.Add(node.name+"["+node.GetSiblingIndex()+"]");node=node.parent;}
  path.Reverse();var p=node.localPosition;var r=node.localEulerAngles;
  var key=FormattableString.Invariant($"{entity.BossName}|{node.name}|{Quantize(p.x,100)},{Quantize(p.y,100)},{Quantize(p.z,100)}|{QuantizeAngle(r.x)},{QuantizeAngle(r.y)},{QuantizeAngle(r.z)}|{string.Join('/',path)}");
  return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
 }
 UnitState RegisterUnit(LivingEntity entity){
  if(unitKeys.TryGetValue(entity.Pointer,out var known)&&units.TryGetValue(known,out var existing))return existing;
  var key=UnitKey(entity);var root=entity.transform;
  GlassFractureDiagnostics.Observe(entity);
  // Do not activate the entire generated room to start one vehicle.
  if(entity.Boss)while(root.parent&&root.parent.parent&&root.parent.parent.name!="GeneratedLevel"&&root.parent.name!="GeneratedLevel")root=root.parent;
  // Inactive glass shards can contain hundreds of dynamic bodies. They do not
  // make the intact owner a moving unit and remain native fragment physics.
  Rigidbody? body=entity.Boss?null:root.GetComponent<Rigidbody>();
  var state=new UnitState{Entity=entity,ActivationRoot=root,Key=key,Boss=entity.Boss,RootBody=body,Motion=entity.Boss||body&&!body.isKinematic,Turret=entity.Boss?root.GetComponentInChildren<BTRTurretAI>(true):null,Helicopters=entity.Boss?root.GetComponentsInChildren<Helicopter>(true).ToArray():Array.Empty<Helicopter>()};
  // A first bullet may destroy glass before the next discovery/snapshot tick.
  // Keep its original pose/HP so a destroyed Unity object still has a tombstone.
  state.Bucket=units.TryGetValue(key,out var previous)?previous.Bucket:units.Count/12;state.Frame=CaptureUnitFrame(state);units[key]=state;unitKeys[entity.Pointer]=key;return state;
 }
 UnitState? ResolveUnitNear(string key,float[] pose){
  if(pose.Length<3)return null;
  var count=Physics.OverlapSphereNonAlloc(new Vector3(pose[0],pose[1],pose[2]),3,nearbyUnitColliders,~0,QueryTriggerInteraction.Collide);
  try{for(var i=0;i<count&&i<nearbyUnitColliders.Length;i++){
   var collider=nearbyUnitColliders[i];if(!collider||collider.isTrigger||collider.gameObject.scene.name!=scene)continue;
   var bridge=collider.GetComponent<LivingEntityCollider>();var entity=bridge?bridge.ParentLivingEntity:collider.GetComponentInParent<LivingEntity>();
   if(!IsNetworkUnit(entity)||entity.gameObject.scene.name!=scene)continue;
   if(unitKeys.TryGetValue(entity.Pointer,out var known)){if(known==key&&units.TryGetValue(known,out var bound))return bound;continue;}
   if(UnitKey(entity)==key)return RegisterUnit(entity);
  }}finally{for(var i=0;i<nearbyUnitColliders.Length;i++)nearbyUnitColliders[i]=null!;}
  return null;
 }
 void ResolvePendingUnits(bool allowDiscovery){
  var started=System.Diagnostics.Stopwatch.GetTimestamp();
  for(var n=4;n>0&&(terminalUnitApply.Count>0||pendingUnitResolve.Count>0);n--){
   // Prioritize known prop deaths, retaining one normal slot so an unresolved
   // remote object cannot starve discovery or the ordinary state queue.
   string key;if(terminalUnitApply.Count>0&&(n>1||pendingUnitResolve.Count==0)){key=terminalUnitApply.Dequeue();queuedTerminalUnitApply.Remove(key);}else{key=pendingUnitResolve.Dequeue();queuedUnitResolve.Remove(key);}
   if(!pendingUnits.TryGetValue(key,out var pending)){unitResolveAt.Remove(key);continue;}
   units.TryGetValue(key,out var entry);
   if(entry==null&&allowDiscovery&&Time.unscaledTime>=unitResolveAt.GetValueOrDefault(key)){unitResolveAt[key]=Time.unscaledTime+1;entry=ResolveUnitNear(key,pending.Frame.Pose);}
   if(entry!=null){
    var applyAt=System.Diagnostics.Stopwatch.GetTimestamp();ApplyUnitFrame(entry,pending.Frame,pending.Sequence);
    GlassFractureDiagnostics.RecordUnitApply(key,(System.Diagnostics.Stopwatch.GetTimestamp()-applyAt)*1000d/System.Diagnostics.Stopwatch.Frequency,pending.Frame.Dead);
    pendingUnits.Remove(key);unitResolveAt.Remove(key);
   }
   else if(queuedUnitResolve.Add(key))pendingUnitResolve.Enqueue(key);
   // Native fracture is indivisible. Once one heavy pane consumes this frame's
   // budget, retain the remaining panes for later frames instead of stacking it.
   if((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000d/System.Diagnostics.Stopwatch.Frequency>=2)break;
  }
 }
 static UnitFrame CaptureUnitFrame(UnitState entry){
  var e=entry.Entity;var p=e.transform.position;var q=e.transform.rotation;var maximum=Math.Max(1,e.StartHealth);
  var f=new UnitFrame{Key=entry.Key,Pose=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w},Maximum=maximum,Health=Math.Clamp(e.myHealth,0,maximum),Dead=e.IsDead,Active=e.gameObject.activeInHierarchy};
  if(entry.Turret&&entry.Turret.turretYaw&&entry.Turret.gunPitch){var a=entry.Turret.turretYaw.localRotation;var b=entry.Turret.gunPitch.localRotation;f.Turret=new[]{a.x,a.y,a.z,a.w,b.x,b.y,b.z,b.w};}
  return f;
 }
 [HideFromIl2Cpp]
 internal void RegisterUnitAwake(LivingEntity entity){if(!SharedRun||Network==null||!IsNetworkUnit(entity)||unitKeys.ContainsKey(entity.Pointer))return;if(!spawnedUnits.ContainsKey(entity.Pointer))spawnedUnitQueue.Enqueue(entity.Pointer);spawnedUnits[entity.Pointer]=entity;/* Capture identity only after native placement, in UnitTick. */}
 void UnitTick(){
  if(!SharedRun||Network==null){if(unitEpoch!="")ClearUnits();return;}
  EnsureUnitEpoch();
  var ready=UnitWorldReady&&elevator&&!elevator.isMoving&&SameWorld;
  // SharedLoot's once-per-build bounded GeneratedLevel walker supplies the
  // initial census, including inactive bosses. Awake supplies dynamic births;
  // first-hit registration below remains immediate. No stable-floor global scan.
  if(ready)for(var n=0;n<8&&spawnedUnitQueue.Count>0;n++){var pointer=spawnedUnitQueue.Dequeue();if(!spawnedUnits.Remove(pointer,out var entity)||!entity)continue;if(entity.Boss||entity.gameObject.activeInHierarchy||!Network.Hosting&&pendingUnits.Count>0&&pendingUnits.ContainsKey(UnitKey(entity)))RegisterUnit(entity);}
  if(!Network.Hosting){
   if(ready)foreach(var entry in units.Values)if(entry.Entity&&!entry.Frozen){FreezeUnit(entry);entry.Frozen=true;}
   // Current-world authoritative state for an already-bound pane does not
   // depend on unrelated peers being ready. Unknown-object discovery still does.
   ResolvePendingUnits(ready);
   return;
  }
  if(!ready)return;
  if(pendingUnitHits.Count>0)foreach(var key in pendingUnitHits.Keys.Take(8).ToArray()){
   var hit=pendingUnitHits[key];if(!units.ContainsKey(hit.Request.ObjectId)&&Time.unscaledTime<hit.Until)continue;
   pendingUnitHits.Remove(key);HandleUnitRequest(hit.Request,false);
  }
  // Keep native host Helicopter.Start's single AddSource camera target.
  // The generated GetSource/SetSource route is unsafe in this pinned interop:
  // GetSource's byref ConstraintSource wrapper is not written back to managed code.
  if(!Connected||Time.unscaledTime<unitSendAt)return;unitSendAt=Time.unscaledTime+.1f;
  var due=new HashSet<int>();
  foreach(var entry in units.Values){
   if(!entry.Motion&&Time.unscaledTime<entry.SendAt)continue;entry.SendAt=Time.unscaledTime+2;due.Add(entry.Bucket);
   if(!entry.Entity){if(entry.Frame!=null){entry.Frame.Dead=true;entry.Frame.Health=0;entry.Frame.Active=false;}continue;}
   var f=CaptureUnitFrame(entry);
   entry.Frame=f;
  }
  if(due.Count==0)return;
  // Transport coalesces snapshots by category. Keep membership stable and
  // include the whole bucket when any member changes, so a later vehicle pose
  // can never replace an unsent glass-death update in that category.
  var sequence=++unitSequence;foreach(var bucket in due){var p=Message("unit");p.Category=bucket;p.Sequence=sequence;p.Units=units.Values.Where(e=>e.Bucket==bucket&&e.Frame!=null).Select(e=>e.Frame!).ToArray();Network.Send(p);}
 }
 static void FreezeUnit(UnitState state){
  // Destructible props have no vehicle simulation. Never census or freeze
  // their inactive fracture hierarchy when applying one authoritative hit.
  if(!state.Boss){if(state.RootBody)state.RootBody.isKinematic=true;return;}
  foreach(var ai in state.ActivationRoot.GetComponentsInChildren<BTRTurretAI>(true)){ai.StopAllCoroutines();ai.enabled=false;}
  foreach(var ai in state.ActivationRoot.GetComponentsInChildren<Helicopter>(true)){ai.StopAllCoroutines();ai.enabled=false;if(ai.lookAt)ai.lookAt.enabled=false;}
  foreach(var ai in state.ActivationRoot.GetComponentsInChildren<RocketSpawnDelayed>(true)){ai.StopAllCoroutines();ai.enabled=false;}
  foreach(var body in state.ActivationRoot.GetComponentsInChildren<Rigidbody>(true)){body.isKinematic=true;}
 }
 static bool SameUnitFrame(UnitFrame a,UnitFrame b)=>a.Health==b.Health&&a.Maximum==b.Maximum&&a.Active==b.Active&&a.Dead==b.Dead&&a.Pose.SequenceEqual(b.Pose)&&a.Turret.SequenceEqual(b.Turret);
 void ApplyUnitFrame(UnitState entry,UnitFrame frame,long sequence){
  if(!entry.Boss&&entry.TerminalApplied&&!frame.Dead)return;
  if(sequence<=entry.Sequence)return;entry.Sequence=sequence;var old=entry.Frame;entry.Frame=frame;if(!entry.Entity)return;
  if(old!=null&&SameUnitFrame(old,frame))return;
  if(frame.Active){for(var node=entry.Entity.transform;node;node=node.parent){if(!node.gameObject.activeSelf)node.gameObject.SetActive(true);if(node==entry.ActivationRoot)break;}}
  if(!entry.Frozen){FreezeUnit(entry);entry.Frozen=true;}
  var e=entry.Entity;e.StartHealth=frame.Maximum;
  // Native death can destroy the entity (glass) or its parent. Apply the pose
  // first so native shards spawn in the right place and never dereference it
  // after the destruction callback.
  if(!entry.Motion){var p=frame.Pose;e.transform.SetPositionAndRotation(new Vector3(p[0],p[1],p[2]),new Quaternion(p[3],p[4],p[5],p[6]));}
  if((frame.Active||frame.Dead)&&(entry.Boss||!entry.TerminalApplied)){ApplyingUnitState=true;try{if(Math.Abs(e.myHealth-frame.Health)>.001f||frame.Dead&&!e.IsDead)e.Health=frame.Dead?0:frame.Health;if(!entry.Boss&&frame.Dead)entry.TerminalApplied=true;}finally{ApplyingUnitState=false;}}
  if(!frame.Active&&entry.ActivationRoot&&entry.ActivationRoot.gameObject.activeSelf)entry.ActivationRoot.gameObject.SetActive(false);
 }
 void UnitLateTick(){
  if(!SharedRun||Network?.Hosting!=false)return;
  foreach(var entry in units.Values){if(!entry.Motion||!entry.Entity||entry.Frame is not {} frame||!frame.Active)continue;
   var pose=frame.Pose;var target=new Vector3(pose[0],pose[1],pose[2]);var q=new Quaternion(pose[3],pose[4],pose[5],pose[6]);var blend=1-Mathf.Exp(-18*Time.unscaledDeltaTime);
   var t=entry.Entity.transform;t.position=Vector3.Distance(t.position,target)>20?target:Vector3.Lerp(t.position,target,blend);t.rotation=Quaternion.Slerp(t.rotation,q,blend);
   if(entry.Turret&&frame.Turret.Length==8){var a=frame.Turret;if(entry.Turret.turretYaw)entry.Turret.turretYaw.localRotation=Quaternion.Slerp(entry.Turret.turretYaw.localRotation,new Quaternion(a[0],a[1],a[2],a[3]),blend);if(entry.Turret.gunPitch)entry.Turret.gunPitch.localRotation=Quaternion.Slerp(entry.Turret.gunPitch.localRotation,new Quaternion(a[4],a[5],a[6],a[7]),blend);}
  }
 }
 [HideFromIl2Cpp]
 bool ReceiveUnit(Packet p){
  if(p.Kind=="unit"&&Network?.Hosting==false){if(!SharedRun||!PacketWorld(p))return true;EnsureUnitEpoch();foreach(var frame in p.Units){
   var known=units.TryGetValue(frame.Key,out var entry);
   if(known&&p.Sequence<=entry!.Sequence)continue;
   if(known&&!entry!.Boss&&(entry.TerminalApplied||entry.Frame?.Dead==true)&&!frame.Dead)continue;
   if(pendingUnits.TryGetValue(frame.Key,out var waiting)&&waiting.Frame.Dead&&!frame.Dead)continue;
   if(known&&entry!.Boss)ApplyUnitFrame(entry,frame,p.Sequence);
   // Keep the newest frame for every known prop, including all tombstones.
   // Native shattering is applied by ResolvePendingUnits at most four/frame.
   else if(known&&entry!.Frame is {} applied&&SameUnitFrame(applied,frame)){entry.Sequence=Math.Max(entry.Sequence,p.Sequence);}
   else if(known||pendingUnits.Count<128||pendingUnits.ContainsKey(frame.Key)){
    if(!pendingUnits.TryGetValue(frame.Key,out var old)||p.Sequence>old.Sequence)pendingUnits[frame.Key]=(frame,p.Sequence);
    if(known&&frame.Dead){if(queuedTerminalUnitApply.Add(frame.Key))terminalUnitApply.Enqueue(frame.Key);}
    else if(queuedUnitResolve.Add(frame.Key))pendingUnitResolve.Enqueue(frame.Key);
   }
  }return true;}
  if(p.Kind=="unitreq"&&Network?.Hosting==true){HandleUnitRequest(p,true);return true;}
  return false;
 }
 [HideFromIl2Cpp]
 void HandleUnitRequest(Packet p,bool allowDiscovery){
  if(!SharedRun||!PacketWorld(p)||!UnitWorldReady||!elevator||elevator.isMoving||p.Amount<=0||p.Amount>1000000||!remotes.TryGetValue(p.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0)return;
  EnsureUnitEpoch();
  // Reserve the sequence in TCP receipt order. A deferred first hit must not
  // become stale merely because a later hit found an already-bound object.
  if(allowDiscovery&&!unitHits.Accept(p.PlayerId,p.Sequence))return;
  var pose=peer.State.Pose;var point=new Vector3(pose[0],pose[1],pose[2]);
  if(p.Pose.Length!=3||Vector3.Distance(point,new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]))>500)return;
  if(!units.TryGetValue(p.ObjectId,out var entry))entry=ResolveUnitNear(p.ObjectId,p.Pose);
  if(entry==null){
   if(allowDiscovery&&pendingUnitHits.Count<64)pendingUnitHits.TryAdd((p.PlayerId,p.Sequence),(p,Time.unscaledTime+8));return;
  }
  if(!entry.Entity||entry.Entity.IsDead||!entry.Entity.gameObject.activeInHierarchy)return;
  if(Vector3.Distance(point,entry.Entity.transform.position)>500)return;
  var previousCredit=DamageCreditPeer;DamageCreditPeer=p.PlayerId;entry.SendAt=0;try{entry.Entity.Health=Math.Max(0,entry.Entity.myHealth-p.Amount);}finally{DamageCreditPeer=previousCredit;}
 }
 [HideFromIl2Cpp]
 bool UnitHealth(LivingEntity entity,float value){
  if(ApplyingUnitState)return true;if(Network?.Hosting!=false){if(SharedRun&&Network?.Hosting==true&&UnitWorldReady&&SameWorld&&elevator&&!elevator.isMoving){EnsureUnitEpoch();var state=RegisterUnit(entity);state.SendAt=0;}return true;}
  if(!UnitWorldReady||!elevator||elevator.isMoving||!SameWorld)return false;
  var damage=entity.myHealth-value;if(damage>0&&damage<=1000000&&Connected){EnsureUnitEpoch();var entry=RegisterUnit(entity);var p=Message("unitreq");p.ObjectId=entry.Key;p.Amount=damage;p.Sequence=++unitHitSequence;var point=entity.transform.position;p.Pose=new[]{point.x,point.y,point.z};Network.Send(p);}return false;
 }
 Transform? NearestUnitTarget(Vector3 origin,float maximum,int obstacles,bool visibility){
  Transform? target=null;var best=maximum*maximum;
  void Consider(Transform? candidate){if(!candidate)return;var distance=(candidate.position-origin).sqrMagnitude;if(distance>best)return;if(visibility&&Physics.Linecast(origin,candidate.position,out var hit,obstacles,QueryTriggerInteraction.Ignore)&&hit.transform&&!hit.transform.IsChildOf(candidate.root))return;target=candidate;best=distance;}
  if(player&&!Down&&player.Health&&!player.Health.died)Consider(player.Camera?player.Camera:player.transform);
  foreach(var peer in remotes.Values)if(PeerMatches(peer)&&peer.State!.Health>0&&peer.Visible)Consider(peer.AimTarget);
  return target;
 }
 [HideFromIl2Cpp]
 internal bool ScanVehicleTarget(BTRTurretAI turret){
  if(!SharedRun||Network==null)return true;if(!Network.Hosting)return false;
  var target=NearestUnitTarget(turret.firePoint?turret.firePoint.position:turret.transform.position,turret.detectionRadius,turret.obstacleLayer,true);turret._target=target;turret._hasTarget=target!=null;return false;
 }
}
[HarmonyPatch(typeof(LivingEntity),"Awake")]
static class DiscoverSpawnedUnit {static void Postfix(LivingEntity __instance)=>Together.Current?.RegisterUnitAwake(__instance);}
[HarmonyPatch(typeof(BTRTurretAI),"ScanForPlayer")]
static class CoopTurretTarget {static bool Prefix(BTRTurretAI __instance)=>Together.Current?.ScanVehicleTarget(__instance)??true;}
[HarmonyPatch(typeof(BTRTurretAI),"Update")]
static class AuthoritativeTurret {static bool Prefix()=>!Together.GuestEnemyAuthority&&Together.Current?.PartyWiped!=true&&Together.Current?.Recovering!=true;}
[HarmonyPatch(typeof(Helicopter),"Start")]
static class AuthoritativeHelicopter {static bool Prefix()=>!Together.GuestEnemyAuthority;}
[HarmonyPatch(typeof(RocketSpawnDelayed),"Start")]
static class AuthoritativeRocketSequence {static bool Prefix()=>!Together.GuestEnemyAuthority;}
[HarmonyPatch(typeof(RocketSpawnDelayed),"Fire")]
static class AuthoritativeRocketFire {static bool Prefix()=>!Together.GuestEnemyAuthority;}
