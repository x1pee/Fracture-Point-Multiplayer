using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.AI;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Native NPC simulation belongs to the host. Guests display replicas and submit damage requests.
public sealed partial class Together
{
 sealed class SyncedEnemy
 {
  public int Id;public EnemyHealth? Health;public GameObject? Root;public Animator? Animator;
  public string Prefab="";public EnemyFrame? Frame;public long Sequence;public bool AppliedDeath;public EnemyRagdoll? Body;public EnemyAnimationCache? AnimationCache;
 }
 readonly Dictionary<int,SyncedEnemy> syncedEnemies=new();
 readonly Dictionary<IntPtr,int> enemyIds=new();
 readonly Dictionary<string,GameObject> enemyPrefabs=new(StringComparer.Ordinal);
 readonly HashSet<string> enemyPrefabRootNames=new(StringComparer.Ordinal);
 sealed class PendingEnemySpawn {public EnemyFrame Frame=null!;public long Sequence;public float RetryAt;}
 readonly Dictionary<int,PendingEnemySpawn> pendingEnemySpawns=new();readonly Queue<int> pendingEnemySpawnQueue=new();
 bool retryingEnemySpawn;
 sealed class EnemyBirth {public EnemyHealth Health=null!;public GameObject? Root;public bool Started;public int EarliestFrame;public float RetryAt;}
 readonly Dictionary<IntPtr,EnemyHealth> nativeEnemyHealth=new();
 readonly Dictionary<IntPtr,EnemyBirth> pendingEnemyBirths=new();readonly Queue<IntPtr> enemyBirthQueue=new();
 readonly Dictionary<int,float> corpseLootRechecks=new();EnemyHealth? arenaVictorHealth;
 int enemyCensusRevision=-1,enemyCatalogueRevision=-1;string enemyCensusWorld="";float enemyCatalogueAt;bool enemyCatalogueDirty=true;public int EnemyCensusCount,EnemyBirthCount;
 readonly PeerTimeline hitReceipts=new();readonly HashSet<int> killReceipts=new();long lastHitResult;
 string enemyEpoch="";int nextEnemyId;long enemySequence,hitSequence,hurtSequence,lastHurt;float enemySendAt;
 public int SentHurt,ReceivedHurt;
 [ThreadStatic] internal static bool EnemyBullet;
 [ThreadStatic] internal static bool PlayerBullet;
 readonly PeerTimeline friendlyRequests=new();
 readonly Dictionary<IntPtr,(Ballistics.Weapon Weapon,int Added)> coopWeaponMasks=new();
 public float LastHurtAmount;float hurtFraction;
 public static bool ApplyingEnemyState;public static int DamageCreditPeer;public static long BlockedAiTicks;
 public static bool GuestEnemyAuthority=>Current?.SharedRun==true&&Current.Network?.Hosting==false;
 public int SyncedEnemyCount=>syncedEnemies.Count;
 [HideFromIl2Cpp]
 public object EnemyCombatDiagnostics()=>new{SentEnemySpeech,ReceivedEnemySpeech,LastEnemySpeech,EnemyCensusCount,EnemyBirthCount,filteredBodyColliders=EnemyBodyDetectionSafety.FilteredColliders,invalidBodyControllers=EnemyBodyDetectionSafety.InvalidControllers,pendingBirths=pendingEnemyBirths.Count,censusRevision=enemyCensusRevision,bodySequence=enemyBodySequence,bodyColliders=corpseColliders.Count,enemies=syncedEnemies.Values.Select(e=>new{e.Id,e.Prefab,dead=e.Health&&e.Health.dead,animator=e.Animator?e.Animator.name:null,controller=e.AnimationCache?.Name,parameterCount=e.AnimationCache?.Hashes.Length??0,parametersDisabled=e.AnimationCache!=null&&unsupportedEnemyParameters.Contains(e.AnimationCache.Name),layers=e.Animator?e.Animator.layerCount:0,states=e.Animator&&e.Animator.runtimeAnimatorController?Enumerable.Range(0,e.Animator.layerCount).Select(i=>new{hash=e.Animator.GetCurrentAnimatorStateInfo(i).fullPathHash,time=e.Animator.GetCurrentAnimatorStateInfo(i).normalizedTime,weight=e.Animator.GetLayerWeight(i)}).ToArray():null,puppet=e.Health&&e.Health.puppet?(bool?)e.Health.puppet.enabled:null,mode=e.Health&&e.Health.puppet?e.Health.puppet.mode.ToString():null,bones=e.Body?.Bones.Length??0,receivedBones=e.Body?.Sequences.Count(x=>x>0)??0,lootAnchor=e.Body?.Bones.FirstOrDefault()?.position.ToString()}).ToArray()};

 void ClearEnemies(){
  ClearUnits();
  foreach(var changed in coopWeaponMasks.Values)if(changed.Weapon)changed.Weapon.HitMask=changed.Weapon.HitMask.value&~changed.Added;coopWeaponMasks.Clear();
  if(Network?.Hosting!=true)foreach(var e in syncedEnemies.Values)if(e.Root)Object.Destroy(e.Root);
  syncedEnemies.Clear();enemyIds.Clear();hitReceipts.Reset();friendlyRequests.Reset();killReceipts.Clear();lastHitResult=0;hurtFraction=0;
  // Unity prefab wrappers can outlive assets unloaded by scene travel. A key
  // surviving a reconnect must not count as a live native prefab.
  enemyPrefabs.Clear();enemyPrefabRootNames.Clear();enemyCatalogueDirty=true;enemyCatalogueRevision=-1;enemyCatalogueAt=0;pendingEnemySpawns.Clear();pendingEnemySpawnQueue.Clear();
  nextEnemyId=0;enemySequence=0;hitSequence=0;hurtSequence=lastHurt=0;enemySendAt=0;enemyEpoch="";ClearEnemySpeechState();pendingEnemyBodies.Clear();pendingEnemyBodyQueue.Clear();corpseColliders.Clear();corpseLootRechecks.Clear();enemyCensusRevision=-1;enemyCensusWorld="";arenaVictorHealth=null;
 }
 void EnemyTick(){
  if(Network==null)return;
  if(!SharedRun){if(enemyEpoch!="")ClearEnemies();return;}
  if(enemyEpoch!=world){ClearEnemies();enemyEpoch=world;}
  // The native street (floor0) has no generated dungeon/layout. Its Start
  // births still need host IDs and guest replicas; generated floors retain
  // the normal build/readiness gate supplied by this shared helper.
  if(SharedActorWorldReady){LoadEnemyPrefabs();EnemyCensus();RegisterEnemyBirths();PendingHostEnemySpeechTick();PendingEnemySpawnTick();}
  PendingEnemySpeechTick();
  PendingEnemyBodyTick();
  if(corpseLootRechecks.Count>0)foreach(var id in corpseLootRechecks.Where(p=>Time.unscaledTime>=p.Value).Select(p=>p.Key).ToArray()){corpseLootRechecks.Remove(id);if(syncedEnemies.TryGetValue(id,out var corpse)&&corpse.Root)DiscoverLootSubtree(corpse.Root.transform);}
  EnemyBodyTick();
  if(Network?.Hosting==true){
   if(!Connected||Time.unscaledTime<enemySendAt)return;enemySendAt=Time.unscaledTime+.15f;
   var frames=new List<EnemyFrame>();
   foreach(var e in syncedEnemies.Values){
    var prior=e.Frame;
    if(!e.Health||!e.Root){if(prior!=null){prior.Dead=true;prior.Health=0;prior.Engaged=false;prior.TargetPeerId=-1;frames.Add(prior);}continue;}
    var p=e.Health.transform.position;var visual=e.Health.dead?null:CaptureEnemyAnimation(e);
    e.Frame=new EnemyFrame{Id=e.Id,Prefab=e.Prefab,Pose=new[]{p.x,p.y,p.z,e.Health.transform.eulerAngles.y},Health=e.Health.health,Dead=e.Health.dead,Animation=visual?.States.FirstOrDefault()??0,AnimationTime=visual?.Times.FirstOrDefault()??0,Visual=visual};
    CapturePersonalCombat(e.Frame,e.Health,e.Root);frames.Add(e.Frame);
   }
   var seq=++enemySequence;var category=0;var batch=new List<EnemyFrame>();var basis=Message("enemy");basis.Sequence=seq;var overhead=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(basis).Length+32;var bytes=overhead;foreach(var frame in frames){var size=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame).Length+1;if(batch.Count>0&&(batch.Count>=12||bytes+size>7200)){var packet=Message("enemy");packet.Category=category++;packet.Sequence=seq;packet.Enemies=batch.ToArray();Network.Send(packet);batch.Clear();bytes=overhead;}batch.Add(frame);bytes+=size;}if(batch.Count>0){var packet=Message("enemy");packet.Category=category;packet.Sequence=seq;packet.Enemies=batch.ToArray();Network.Send(packet);}
  }else{
   foreach(var e in syncedEnemies.Values){
    if(!e.Root||!e.Health||e.Frame==null||e.Frame.Dead)continue;var f=e.Frame;var target=new Vector3(f.Pose[0],f.Pose[1],f.Pose[2]);var delta=target-e.Health.transform.position;
    var blend=1-Mathf.Exp(-15*Time.unscaledDeltaTime);e.Root.transform.position+=delta*(delta.magnitude>10?1:blend);
    e.Root.transform.rotation=Quaternion.Euler(0,Mathf.DeltaAngle(e.Health.transform.eulerAngles.y,f.Pose[3])*blend,0)*e.Root.transform.rotation;
   }
  }
 }
 [HideFromIl2Cpp]
 internal void EnemyHealthStarted(EnemyHealth health){
  if(!health||!health.gameObject.scene.IsValid())return;nativeEnemyHealth[health.Pointer]=health;EnemyBirthCount++;QueueEnemyBirth(health,true);
 }
 [HideFromIl2Cpp]
 void QueueEnemyBirth(EnemyHealth health,bool started){
  if(!health)return;var pointer=health.Pointer;if(enemyIds.TryGetValue(pointer,out var id)){if(syncedEnemies.TryGetValue(id,out var existing)&&existing.Health)return;enemyIds.Remove(pointer);}
  if(pendingEnemyBirths.TryGetValue(pointer,out var pending)){pending.Health=health;pending.Started|=started;return;}pendingEnemyBirths[pointer]=new EnemyBirth{Health=health,Started=started,EarliestFrame=Time.frameCount+1};enemyBirthQueue.Enqueue(pointer);
 }
 void EnemyCensus(){
  if(enemyCensusWorld==world&&enemyCensusRevision==DungeonSeedPatch.Revision)return;enemyCensusWorld=world;enemyCensusRevision=DungeonSeedPatch.Revision;EnemyCensusCount++;
  // One bootstrap census of this generated level; dynamic and late-enabled
  // actors enter through their native Start callback instead of global polling.
  var root=elevator?elevator.levelParent:null;var found=root?root.GetComponentsInChildren<EnemyHealth>(true).ToArray():Object.FindObjectsOfType<EnemyHealth>(true).ToArray();foreach(var health in found)QueueEnemyBirth(health,nativeEnemyHealth.ContainsKey(health.Pointer)||health.totalHealth>0);
  foreach(var pair in nativeEnemyHealth.ToArray()){if(!pair.Value){nativeEnemyHealth.Remove(pair.Key);continue;}if(pair.Value.gameObject.scene.name==scene)QueueEnemyBirth(pair.Value,true);}
 }
 void RegisterEnemyBirths(){
  var count=Math.Min(8,enemyBirthQueue.Count);for(var i=0;i<count;i++){
   var pointer=enemyBirthQueue.Dequeue();if(!pendingEnemyBirths.TryGetValue(pointer,out var birth))continue;var health=birth.Health;
   if(!health||health.gameObject.scene.name!=scene||enemyIds.ContainsKey(pointer)){pendingEnemyBirths.Remove(pointer);continue;}
   if(Time.frameCount<birth.EarliestFrame||Time.unscaledTime<birth.RetryAt||!health.gameObject.activeInHierarchy||!birth.Started&&health.totalHealth<=0){enemyBirthQueue.Enqueue(pointer);continue;}
   var root=birth.Root;if(!root)root=birth.Root=EnemyRoot(health);
   if(Network?.Hosting!=true){if(IsVictor(health)){arenaVictorHealth=health;FreezeEnemyReplica(root);}else Object.Destroy(root);pendingEnemyBirths.Remove(pointer);continue;}
   var key=FindEnemyPrefab(health,root);if(key==null){detail="NPC prefab missing from shared catalogue: "+root.name;enemyCatalogueDirty=true;birth.RetryAt=Time.unscaledTime+2;birth.Root=null;enemyBirthQueue.Enqueue(pointer);continue;}
   var entry=new SyncedEnemy{Id=++nextEnemyId,Health=health,Root=root,Animator=EnemyAnimator(health,root),Prefab=key};syncedEnemies.Add(entry.Id,entry);enemyIds[pointer]=entry.Id;pendingEnemyBirths.Remove(pointer);DiscoverLootSubtree(root.transform);if(health.dead)EnemyDied(health);
  }
 }
 void LoadEnemyPrefabs(){
  if(enemyCatalogueRevision==DungeonSeedPatch.Revision&&(!enemyCatalogueDirty||Time.unscaledTime<enemyCatalogueAt))return;enemyCatalogueRevision=DungeonSeedPatch.Revision;enemyCatalogueDirty=false;enemyCatalogueAt=Time.unscaledTime+2;
  foreach(var key in enemyPrefabs.Where(p=>!p.Value).Select(p=>p.Key).ToArray())enemyPrefabs.Remove(key);
  foreach(var data in Resources.FindObjectsOfTypeAll<EnemyData>()){
   if(!data||data.enemies==null)continue;
   foreach(var type in data.enemies){if(type==null||type.prefabs==null)continue;foreach(var prefab in type.prefabs)if(prefab){enemyPrefabs.TryAdd(type.type+"/"+prefab.name,prefab);enemyPrefabRootNames.Add(prefab.name);}}
  }
  if(enemyPrefabs.Count==0)enemyCatalogueDirty=true;
  // The arena owns Victor's hierarchy and scripted references. His canonical
  // identity does not require an asset-catalogue entry or a cloned soldier leaf.
 }
 [HideFromIl2Cpp]
 bool TryEnemyPrefab(string key,out GameObject prefab){
  if(enemyPrefabs.TryGetValue(key,out prefab!)&&prefab)return true;
  enemyPrefabs.Remove(key);enemyCatalogueDirty=true;prefab=null!;return false;
 }
 [HideFromIl2Cpp]
 void DeferEnemySpawn(EnemyFrame frame,long sequence){
  if(pendingEnemySpawns.TryGetValue(frame.Id,out var pending)){
   if(sequence>pending.Sequence&&(!pending.Frame.Dead||frame.Dead)){pending.Frame=frame;pending.Sequence=sequence;}return;
  }
  if(pendingEnemySpawns.Count>=512)throw new IOException("Authoritative NPC birth backlog exceeded 512 entries; unable to retain another host identity.");
  pendingEnemySpawns[frame.Id]=new PendingEnemySpawn{Frame=frame,Sequence=sequence};pendingEnemySpawnQueue.Enqueue(frame.Id);
 }
 void PendingEnemySpawnTick(){
  if(Network?.Hosting!=false)return;
  for(var n=Math.Min(2,pendingEnemySpawnQueue.Count);n>0&&pendingEnemySpawnQueue.Count>0;n--){
   var id=pendingEnemySpawnQueue.Dequeue();if(!pendingEnemySpawns.TryGetValue(id,out var pending))continue;
   if(Time.unscaledTime>=pending.RetryAt){pending.RetryAt=Time.unscaledTime+1;var packet=Message("enemy");packet.Sequence=pending.Sequence;packet.Enemies=new[]{pending.Frame};retryingEnemySpawn=true;try{ReceiveEnemy(packet);}finally{retryingEnemySpawn=false;}}
   if(pendingEnemySpawns.ContainsKey(id))pendingEnemySpawnQueue.Enqueue(id);
  }
 }
 static GameObject? LiveEnemyObject(EnemyHealth? health){
  if(!health)return null;
  try{var root=health.gameObject;return root?root:null;}
  catch(Il2CppInterop.Runtime.Il2CppException error){GameplayLog.Fault("enemy_source",error);return null;}
 }
 static bool IsVictor(EnemyHealth health){var boss=health.GetComponent<Ballistics.LivingEntity>();return boss&&boss.Boss&&BossHealthScaling.IsVictor(boss.BossName);}
 [HideFromIl2Cpp]
 EnemyHealth? NativeArenaVictor(EnemyFrame frame){
  var scope=SharedBirthRoot();EnemyHealth? result=null;var nearest=float.PositiveInfinity;var position=new Vector3(frame.Pose[0],frame.Pose[1],frame.Pose[2]);
  if(floor>0&&!scope)return null;
  [HideFromIl2Cpp]
  void Consider(EnemyHealth? health){
   if(!health||enemyIds.ContainsKey(health.Pointer)||!IsVictor(health)||health.gameObject.scene.name!=scene)return;
   if(scope&&!health.transform.IsChildOf(scope))return;
   var distance=(health.transform.position-position).sqrMagnitude;if(distance<nearest){nearest=distance;result=health;}
  }
  Consider(arenaVictorHealth);
  // The generated-room census includes inactive actors whose native Start has
  // not run yet. Do not wait for their activation before binding a host ID.
  foreach(var birth in pendingEnemyBirths.Values)Consider(birth.Health);
  foreach(var health in nativeEnemyHealth.Values)Consider(health);
  return result;
 }
 static void ActivateArenaVictor(GameObject root){
  // A host snapshot proves the authored encounter is active. Enable only the
  // native ancestor chain needed by this existing actor, never another copy.
  for(var t=root.transform;t&&!root.activeInHierarchy;t=t.parent)if(!t.gameObject.activeSelf)t.gameObject.SetActive(true);
 }
 static string CleanClone(string name)=>name.Replace("(Clone)","").Trim();
 GameObject EnemyRoot(EnemyHealth health){
  if(IsVictor(health))return health.gameObject;
  for(var t=health.transform;t;t=t.parent)if(enemyPrefabRootNames.Contains(CleanClone(t.name)))return t.gameObject;
  return health.gameObject;
 }
 string? FindEnemyPrefab(EnemyHealth health,GameObject root){
  if(IsVictor(health))return "Boss/VICTOR";
  var name=CleanClone(root.name);var exact=health.enemyType+"/"+name;if(enemyPrefabs.ContainsKey(exact))return exact;
  return enemyPrefabs.Keys.FirstOrDefault(k=>k.EndsWith("/"+name,StringComparison.Ordinal));
 }
 [HideFromIl2Cpp]
 bool ReceiveEnemy(Packet p){
  if(ReceiveEnemyBody(p)||ReceiveEnemySpeech(p))return true;
  if(p.Kind=="hurt"&&Network?.Hosting==false){if(SharedRun&&PacketWorld(p)&&p.TargetId==Network.LocalId&&p.Sequence>lastHurt&&p.Amount>0&&p.Amount<=20000){lastHurt=p.Sequence;ApplyOwnHurt(p.Amount);}return true;}
  if(p.Kind=="pvpreq"&&Network?.Hosting==true){FriendlyDamage(p);return true;}
  if(p.Kind=="enemy"&&Network?.Hosting==false){
   if(!SharedRun||!PacketWorld(p))return true;if(enemyEpoch!=world){ClearEnemies();enemyEpoch=world;}LoadEnemyPrefabs();
   foreach(var incoming in p.Enemies){
    var frame=incoming;var sequence=p.Sequence;
    if(pendingEnemySpawns.TryGetValue(frame.Id,out var delayed)){
     if(!retryingEnemySpawn){DeferEnemySpawn(frame,sequence);continue;}
     if(delayed.Sequence>sequence||delayed.Frame.Dead&&!frame.Dead){frame=delayed.Frame;sequence=delayed.Sequence;}
    }
    if(!syncedEnemies.TryGetValue(frame.Id,out var entry)){
     var pos=new Vector3(frame.Pose[0],frame.Pose[1],frame.Pose[2]);GameObject root;
     var isVictor=frame.Prefab=="Boss/VICTOR";
     // Discovery may still be waiting on the native room build. Keep the exact
     // authoritative ID/death state until its authored actor exists.
     if(isVictor&&!SharedActorWorldReady){DeferEnemySpawn(frame,sequence);continue;}
     if(isVictor)EnemyCensus();
     var arenaVictor=isVictor?NativeArenaVictor(frame):null;
     var arenaRoot=LiveEnemyObject(arenaVictor);
     if(isVictor&&!arenaRoot){DeferEnemySpawn(frame,sequence);detail="Waiting for the native Victor arena.";continue;}
     GameObject prefab=null!;
     if(!arenaRoot&&!TryEnemyPrefab(frame.Prefab,out prefab)){DeferEnemySpawn(frame,sequence);detail="Host NPC prefab unavailable: "+frame.Prefab;continue;}
     ApplyingEnemyState=true;try{root=arenaRoot?arenaRoot:Object.Instantiate(prefab,pos,Quaternion.Euler(0,frame.Pose[3],0));}
     catch(Il2CppInterop.Runtime.Il2CppException error) when(!arenaRoot&&!prefab){GameplayLog.Fault("enemy_prefab",error);enemyPrefabs.Remove(frame.Prefab);enemyCatalogueDirty=true;DeferEnemySpawn(frame,sequence);continue;}
     finally{ApplyingEnemyState=false;}
     if(!root){enemyPrefabs.Remove(frame.Prefab);enemyCatalogueDirty=true;DeferEnemySpawn(frame,sequence);continue;}
     var health=root.GetComponentInChildren<EnemyHealth>(true);
     if(!health){Object.Destroy(root);continue;}
     // Initialize once before applying a possibly already-dead host snapshot.
     if(arenaRoot)ActivateArenaVictor(root);
     if(!arenaRoot||!nativeEnemyHealth.ContainsKey(health.Pointer))health.Start();health.enabled=false;
     entry=new SyncedEnemy{Id=frame.Id,Root=root,Health=health,Animator=EnemyAnimator(health,root),Prefab=frame.Prefab};
     syncedEnemies.Add(entry.Id,entry);enemyIds[health.Pointer]=entry.Id;
     if(frame.Prefab=="Boss/VICTOR")foreach(var checker in Object.FindObjectsOfType<BossfightDoorsChecker>(true))
      if(checker.AchievementName=="KILLVICTOR")checker.BossHealth=health;
     FreezeEnemyReplica(root);
     DiscoverLootSubtree(root.transform);
    }
    pendingEnemySpawns.Remove(frame.Id);
    if(sequence<=entry.Sequence)continue;entry.Sequence=sequence;entry.Frame=frame;
    if(entry.Health){
     entry.Health.health=frame.Health;
     if(frame.Dead&&!entry.AppliedDeath){ApplyingEnemyState=true;try{EnemyBody(entry);entry.Health.Kill();entry.AppliedDeath=true;FreezeEnemyReplica(entry.Root!);PrepareEnemyCorpse(entry);}finally{ApplyingEnemyState=false;}}
     if(entry.Prefab=="Boss/VICTOR")BossHealthScaling.Reconcile(entry.Health);
    }
    ApplyEnemyAnimation(entry,frame);
   }
   return true;
  }
  if(p.Kind=="hitreq"&&Network?.Hosting==true){
   if(!SharedRun||!PacketWorld(p)||p.Pose.Length!=6||p.Amount<=0||p.Amount>20000||!remotes.TryGetValue(p.PlayerId,out var peer)||!PeerMatches(peer)||!syncedEnemies.TryGetValue(p.ItemId,out var e)||!e.Health||e.Health.dead)return true;
   if(!hitReceipts.Accept(p.PlayerId,p.Sequence))return true;
   var from=peer.State!.Pose;var position=new Vector3(from[0],from[1],from[2]);if(Vector3.Distance(position,e.Health.transform.position)>250)return true;
   DamageCreditPeer=p.PlayerId;var before=Math.Max(0,e.Health.health);var priorSuppression=PersonalProfile.SuppressCombatPerks;PersonalProfile.SuppressCombatPerks=true;
   try{e.Health.TakeDamage(p.Success?Math.Max(p.Amount,before+1):p.Amount);}finally{DamageCreditPeer=0;PersonalProfile.SuppressCombatPerks=priorSuppression;}
   var result=Message("hitresult");result.Sequence=p.Sequence;result.Amount=Math.Clamp(before-e.Health.health,0,before);Network.SendTo(result,p.PlayerId);
   if(e.Health.dead||e.Health.health<=0){var credit=Message("killcredit");credit.ItemId=(int)e.Health.enemyType;credit.Sequence=e.Id;Network.SendTo(credit,p.PlayerId);}
   return true;
  }
  if(p.Kind=="killcredit"&&Network?.Hosting==false){if(PacketWorld(p)&&p.Sequence>0&&p.Sequence<=int.MaxValue&&killReceipts.Add((int)p.Sequence)){
   Object.FindObjectOfType<QuestManager>()?.OnKillEnemy((Enemy.EnemyType)p.ItemId);
   var count=PlayerPrefs.GetInt("EnemiesKilled",0);if(count<int.MaxValue)count++;PlayerPrefs.SetInt("EnemiesKilled",count);
   if(count>=50)AchievementUnlocker.Unlock("KILL50");if(count>=200)AchievementUnlocker.Unlock("KILL200");
   var health=Object.FindObjectOfType<PlayerHealthManager>();var regen=PlayerPrefs.GetFloat("KillRegenEffect",0);if(health&&regen>0)health.AddHealthAccumulator(Math.Max(0,health.MaxHealth-health.Health)*regen/100);
  }return true;}
  if(p.Kind=="hitresult"&&Network?.Hosting==false){if(PacketWorld(p)&&p.Sequence>lastHitResult){lastHitResult=p.Sequence;var vampire=PlayerPrefs.GetFloat("Vampirism",0);if(vampire>0&&p.Amount>0)Object.FindObjectOfType<PlayerHealthManager>()?.AddHealthAccumulator(p.Amount*vampire/100);}return true;}
  return false;
 }
 static void FreezeEnemyReplica(GameObject root){
  var health=root.GetComponentInChildren<EnemyHealth>(true);
  foreach(var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)){
   if(behaviour.TryCast<EnemyHealth>()||behaviour.TryCast<LootableItem>()||behaviour.TryCast<LootableChanceItems>())continue;
   if(behaviour.TryCast<RootMotion.Dynamics.PuppetMaster>() is {} puppet){puppet.mode=RootMotion.Dynamics.PuppetMaster.Mode.Kinematic;puppet.enabled=health&&!health.dead;continue;}
   behaviour.StopAllCoroutines();behaviour.enabled=false;
  }
  foreach(var nav in root.GetComponentsInChildren<NavMeshAgent>(true))nav.enabled=false;
  foreach(var rb in root.GetComponentsInChildren<Rigidbody>(true)){rb.velocity=Vector3.zero;rb.angularVelocity=Vector3.zero;rb.isKinematic=true;}
  // Kill is terminal before this helper runs. Stop the dead guest's native
  // animation even if PrepareEnemyCorpse is still waiting for its bone rig.
  foreach(var animator in root.GetComponentsInChildren<Animator>(true)){animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;if(health&&health.dead)animator.enabled=false;}
 }
 [HideFromIl2Cpp]
 internal void EnemyDied(EnemyHealth health){if(!SharedRun||!health.dead)return;if(enemyIds.TryGetValue(health.Pointer,out var id)&&syncedEnemies.TryGetValue(id,out var enemy)){PrepareEnemyCorpse(enemy);if(enemy.Root)DiscoverLootSubtree(enemy.Root.transform);corpseLootRechecks[id]=Time.unscaledTime+2;}}
 [HideFromIl2Cpp]
 internal string? EnemyLootIdentity(Component component){
  foreach(var entry in syncedEnemies.Values){
   var basis=entry.Health&&component.transform.IsChildOf(entry.Health.transform)?entry.Health.transform:entry.Root?entry.Root.transform:null;
   if(!basis||!component.transform.IsChildOf(basis))continue;
   var path=new List<string>();for(var t=component.transform;t&&t!=basis;t=t.parent)path.Add(t.name+"["+t.GetSiblingIndex()+"]");path.Reverse();return "npc:"+entry.Id+":"+string.Join("/",path);
  }
  return null;
 }
 [HideFromIl2Cpp]
 public bool EnemyDamage(EnemyHealth health,object[] args){
  // LivingEntity's native setter also calls TakeDamage. Visual reconciliation
  // has already applied this authoritative health and must not hit twice.
  if(BossHealthScaling.Reconciling&&BossHealthScaling.ReconciledHealth==health.Pointer)return false;
  if(!GuestEnemyAuthority||ApplyingEnemyState)return true;
  if(!Connected||!enemyIds.TryGetValue(health.Pointer,out var id))return false;
  var damage=Math.Abs(args.OfType<float>().FirstOrDefault());if(damage<=0)return false;
  var packet=Message("hitreq");packet.ItemId=id;packet.Amount=damage*(1+Math.Max(0,PlayerPrefs.GetFloat("MoreDamage",0))/100);packet.Sequence=++hitSequence;
  packet.Success=args.Length>1&&UnityEngine.Random.value*100<PlayerPrefs.GetFloat("InstantKill",0);
  var location=args.Length>1?(Vector3)args[0]:health.transform.position;var direction=args.Length>1?(Vector3)args[1]:Vector3.forward;
  packet.Pose=new[]{location.x,location.y,location.z,direction.x,direction.y,direction.z};packet.ByHand=args.Length>5&&args[5] is true;Network!.Send(packet);return false;
 }
 [HideFromIl2Cpp]
 public bool LivingHealth(Ballistics.LivingEntity entity,float value){
  if(!SharedRun||Network==null)return true;
  if(ProjectileOwner.HasValue&&entity.PlayerHealthManager&&!ProjectileRules.HurtsPlayer(ProjectileOwner.Value,Network.LocalId))return false;
  foreach(var peer in remotes.Values)if(peer.HealthProxy&&peer.HealthProxy.Pointer==entity.Pointer){
   var damage=entity.myHealth-value;
   if(ProjectileOwner.HasValue){if(Network.Hosting&&PeerMatches(peer)&&damage>0&&damage<=20000&&ProjectileRules.HurtsPlayer(ProjectileOwner.Value,peer.Id))DeliverHurt(peer.Id,damage);}
   else if(EnemyBullet&&Network.Hosting&&PeerMatches(peer)&&damage>0&&damage<=20000)DeliverHurt(peer.Id,damage);
   else if(PlayerBullet&&damage>0&&damage<=20000){var hit=Message("pvpreq");hit.TargetId=peer.Id;hit.PlayerId=Network.LocalId;hit.Sequence=++hitSequence;hit.Amount=damage*(1+Math.Max(0,PlayerPrefs.GetFloat("MoreDamage",0))/100);if(Network.Hosting)FriendlyDamage(hit);else Network.Send(hit);}
   return false;
  }
  if(GuestEnemyAuthority&&entity.EnemyHealth){
   // Native authoritative death/health replay must reach its original setter;
   // submitting it as another guest hit request feeds replay back to the host.
   if(ApplyingEnemyState)return true;
   var damage=entity.myHealth-value;if(damage>0)EnemyDamage(entity.EnemyHealth,new object[]{damage});return false;
  }
  if(IsNetworkUnit(entity))return UnitHealth(entity,value);
  return true;
 }
 void ApplyOwnHurt(float damage){if(Down||PartyWiped)return;using var damageTrace=DamageDiagnostics.NetworkReplay();LastHurtAmount=damage;ReceivedHurt++;hurtFraction+=damage;var whole=(int)Math.Floor(hurtFraction);hurtFraction-=whole;if(whole>0)Object.FindObjectOfType<PlayerHealthManager>()?.Damage(whole);}
 void DeliverHurt(int id,float damage){if(damage<=0)return;DamageDiagnostics.Deliver(id,damage,Network!.LocalId);if(id==Network!.LocalId){ApplyOwnHurt(damage);return;}var hit=Message("hurt");hit.TargetId=id;hit.Sequence=++hurtSequence;hit.Amount=damage;Network.SendTo(hit,id);SentHurt++;}
 [HideFromIl2Cpp]
 internal void ChooseCoopTarget(StateController controller){
  if(Network?.Hosting!=true||!SharedRun||controller.type!=StateController.AiType.Enemy)return;
  var origin=controller.transform.position;Transform? target=null;var distance=float.PositiveInfinity;
  if(player&&!Down&&player.Health&&!player.Health.died){target=player.Camera?player.Camera:player.transform;distance=(target.position-origin).sqrMagnitude;}
  // Keep native NPC-vs-NPC combat when that target is closer.
  if(controller.targetHealth&&!controller.targetHealth.dead){var npcDistance=(controller.targetHealth.transform.position-origin).sqrMagnitude;if(npcDistance<distance){target=controller.targetHealth.transform;distance=npcDistance;}}
  foreach(var peer in remotes.Values)if(PeerMatches(peer)&&peer.State!.Health>0&&peer.Visible&&peer.AimTarget){var candidate=(peer.AimTarget.position-origin).sqrMagnitude;if(candidate<distance){target=peer.AimTarget;distance=candidate;}}
  if(target){controller.aimTarget=target;if(!controller.targetHealth||target!=controller.targetHealth.transform)controller.targetHealth=null;controller.noEnemiesAlive=false;if(distance<3600)controller.aiActive=true;}
 }
 [HideFromIl2Cpp]
 internal bool CoopTargetCandidates(StateController controller,float radius,Decision.HandeTargets callback,LayerMask layer,ref bool result){
  if(Network?.Hosting!=true||!SharedRun||controller.type!=StateController.AiType.Enemy||!controller.generalStats||layer.value!=controller.generalStats.targetMask.value||!controller.aimTarget)return true;
  var peer=remotes.Values.FirstOrDefault(r=>PeerMatches(r)&&r.State!.Health>0&&r.Visible&&r.Root&&controller.aimTarget.IsChildOf(r.Root.transform));if(peer==null||!peer.HealthProxy)return true;
  var collider=peer.HealthProxy.GetComponent<Collider>();if(!collider||!collider.enabled)return true;
  var within=Vector3.Distance(controller.transform.position,controller.aimTarget.position)<=radius;var candidates=new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(within?new[]{collider}:Array.Empty<Collider>());result=callback.Invoke(controller,within,candidates);return false;
 }
 [HideFromIl2Cpp]
 internal void IncludeCoopHitboxes(Ballistics.Weapon weapon){
  if(!SharedRun||Network==null||!weapon)return;var original=weapon.HitMask.value;var mask=original;foreach(var peer in remotes.Values)if(PeerMatches(peer)&&peer.Visible&&peer.HealthProxy){var bit=1<<peer.HealthProxy.gameObject.layer;mask|=bit;}if(mask!=original){var added=mask&~original;if(coopWeaponMasks.TryGetValue(weapon.Pointer,out var prior))added|=prior.Added;coopWeaponMasks[weapon.Pointer]=(weapon,added);weapon.HitMask=mask;}
 }
 [HideFromIl2Cpp]
 internal bool ProxyBulletHit(Ballistics.Weapon weapon,Ballistics.LivingEntityCollider collider,float speed){
  if(!SharedRun||Network==null||!weapon||!collider||!collider.ParentLivingEntity)return true;
  // Adding the shared player layer to a ray mask also exposes the shooter's
  // own body. Reject only a direct bullet from this exact local FPS owner;
  // grenade ownership and enemy bullets retain their native damage path.
  var localHealth=collider.ParentLivingEntity.PlayerHealthManager;
  if(!ProjectileOwner.HasValue&&!EnemyBullet&&PlayerBullet&&player&&localHealth&&localHealth.transform.IsChildOf(player.transform)){
   var owner=weapon.GetComponentInParent<Demo.Scripts.Runtime.Character.FPSController>();if(owner&&owner.Pointer==player.Pointer)return false;
  }
  var peer=remotes.Values.FirstOrDefault(r=>r.HealthProxy&&r.HealthProxy.Pointer==collider.ParentLivingEntity.Pointer);if(peer==null)return true;
  if(!PeerMatches(peer)||peer.State!.Health<=0)return false;
  // Match both native BulletHit damage terms once, then bypass its NPC blood
  // decal path. The remote proxy deliberately has no native player/blood data.
  var maximum=weapon.MaxBulletSpeed;var damage=(speed/maximum+100f/Math.Min(maximum,700f))*weapon.MuzzleDamage*collider.DamageMultiplier;
  if(!float.IsFinite(damage)||damage<=0||damage>20000)return false;
  if(ProjectileOwner.HasValue){if(Network.Hosting&&ProjectileRules.HurtsPlayer(ProjectileOwner.Value,peer.Id))DeliverHurt(peer.Id,damage);}
  else if(EnemyBullet){if(Network.Hosting)DeliverHurt(peer.Id,damage);}
  else if(PlayerBullet){var request=Message("pvpreq");request.TargetId=peer.Id;request.PlayerId=Network.LocalId;request.Sequence=++hitSequence;request.Amount=damage*(1+Math.Max(0,PlayerPrefs.GetFloat("MoreDamage",0))/100);if(Network.Hosting)FriendlyDamage(request);else Network.Send(request);}
  return false;
 }
 [HideFromIl2Cpp]
 void FriendlyDamage(Packet p){
  if(!SharedRun||!PacketWorld(p)||Rules.FriendlyFire<=0||p.Amount<=0||p.Amount>20000||p.TargetId==p.PlayerId||!friendlyRequests.Accept(p.PlayerId,p.Sequence))return;
  Vector3 from,to;
  if(p.PlayerId==Network!.LocalId){if(!player||Down)return;from=player.transform.position;}else{if(!remotes.TryGetValue(p.PlayerId,out var shooter)||!PeerMatches(shooter)||shooter.State!.Health<=0)return;var pose=shooter.State.Pose;from=new Vector3(pose[0],pose[1],pose[2]);}
  if(p.TargetId==Network.LocalId){if(!player||Down)return;to=player.transform.position;}else{if(!remotes.TryGetValue(p.TargetId,out var target)||!PeerMatches(target)||target.State!.Health<=0)return;var pose=target.State.Pose;to=new Vector3(pose[0],pose[1],pose[2]);}
  if(Vector3.Distance(from,to)>250)return;DamageDiagnostics.Friendly(p.PlayerId,p.TargetId,p.Amount,Rules.FriendlyFire,p.Sequence);DeliverHurt(p.TargetId,p.Amount*Rules.FriendlyFire);
 }
}
[HarmonyPatch(typeof(Ballistics.LivingEntity),"set_Health")]
static class SharedBallisticHealth {static bool Prefix(Ballistics.LivingEntity __instance,float __0)=>Together.Current?.LivingHealth(__instance,__0)??true;}
[HarmonyPatch(typeof(StateController),"Update")]
static class CoopEnemyTarget {static bool Prefix(StateController __instance){var current=Together.Current;if(current?.PartyWiped==true||current?.Recovering==true)return false;var health=__instance.GetComponentInChildren<EnemyHealth>();if(health&&health.dead)return false;current?.ChooseCoopTarget(__instance);return true;}}
[HarmonyPatch(typeof(StateController),"UpdateClosestEnemy")]
static class CoopClosestTarget {static void Postfix(StateController __instance)=>Together.Current?.ChooseCoopTarget(__instance);}
[HarmonyPatch(typeof(Decision),nameof(Decision.CheckTargetsInRadius))]
static class CoopTargetPerception {static bool Prefix(StateController __0,float __1,Decision.HandeTargets __2,LayerMask __3,ref bool __result)=>Together.Current?.CoopTargetCandidates(__0,__1,__2,__3,ref __result)??true;}
[HarmonyPatch(typeof(Ballistics.Weapon),nameof(Ballistics.Weapon.ShootBullet))]
static class CoopWeaponHitMask {static void Prefix(Ballistics.Weapon __instance)=>Together.Current?.IncludeCoopHitboxes(__instance);}
[HarmonyPatch(typeof(Ballistics.BulletHandler),"BulletHit")]
static class NativeEnemyBulletContext
{
 internal struct Context {public bool Enemy,Player;public ProjectileCombatScope? Projectile;}
 static bool Prefix(Ballistics.Weapon __0,Ballistics.LivingEntityCollider __1,float __5,out Context __state){__state=new(){Enemy=Together.EnemyBullet,Player=Together.PlayerBullet,Projectile=Together.Current?.FragmentScope(__0)};Together.EnemyBullet=__0&&(__0.GetComponentInParent<StateController>()||__0.GetComponentInParent<BTRTurretAI>()||__0.GetComponentInParent<Helicopter>());Together.PlayerBullet=__0&&__0.GetComponentInParent<Demo.Scripts.Runtime.Character.FPSController>();return Together.Current?.ProxyBulletHit(__0,__1,__5)??true;}
 static Exception? Finalizer(Context __state,Exception? __exception){__state.Projectile?.Dispose();Together.EnemyBullet=__state.Enemy;Together.PlayerBullet=__state.Player;return __exception;}
}
[HarmonyPatch(typeof(EnemySpawner),nameof(EnemySpawner.Spawn))]
static class HostEnemySpawnOnly {static bool Prefix()=>!Together.GuestEnemyAuthority;}
[HarmonyPatch(typeof(EnemyHealth),"Start")]
static class NativeEnemyBirth {static void Postfix(EnemyHealth __instance)=>Together.Current?.EnemyHealthStarted(__instance);}
[HarmonyPatch]
static class AuthoritativeEnemyDamage
{
 static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>typeof(EnemyHealth).GetMethods().Where(m=>m.Name==nameof(EnemyHealth.TakeDamage)&&m.DeclaringType==typeof(EnemyHealth));
 static bool Prefix(EnemyHealth __instance,object[] __args)=>Together.Current?.EnemyDamage(__instance,__args)??true;
 static void Postfix(EnemyHealth __instance){if(Together.Current?.SharedRun==true&&!Together.GuestEnemyAuthority&&!BossHealthScaling.Reconciling)BossHealthScaling.Reconcile(__instance);}
}
[HarmonyPatch(typeof(QuestManager),nameof(QuestManager.OnKillEnemy))]
static class OwnEnemyQuestCredit {static bool Prefix()=>!Together.ApplyingEnemyState&&!Together.ApplyingUnitState&&Together.DamageCreditPeer==0;}
[HarmonyPatch]
static class HostEnemyAiOnly
{
 static IEnumerable<System.Reflection.MethodBase> TargetMethods(){
  foreach(var name in new[]{"OnEnable","Start","Update","TransitionToState","AlertCallback"})yield return AccessTools.Method(typeof(StateController),name);
  foreach(var name in new[]{"Update","LateUpdate","OnAnimatorMove"})yield return AccessTools.Method(typeof(EnemyAnimation),name);
 }
 static bool Prefix(){if(!Together.GuestEnemyAuthority)return true;Together.BlockedAiTicks++;return false;}
}
