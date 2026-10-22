using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

// Replace only the native global ANY-enemy classification. Native SetMode still
// owns the authored clips, mixer, volume targets, transitions and fade coroutines.
internal static class PersonalCombatMusic
{
 sealed class NativeMusic {
  internal MusicController Source=null!;internal int Scene;internal float RefreshAt;
  internal readonly HashSet<IntPtr> Fighting=new();
 }
 static readonly Dictionary<IntPtr,NativeMusic> native=new();
 static int rayFrame=-1,rays;
 internal static void Observe(MusicController music){
  if(!music||!music.gameObject.scene.IsValid())return;
  if(!native.TryGetValue(music.Pointer,out var value)){
   if(native.Count>=8){var expired=new List<IntPtr>();foreach(var pair in native)if(!pair.Value.Source)expired.Add(pair.Key);foreach(var key in expired)native.Remove(key);if(native.Count>=8)return;}
   native[music.Pointer]=value=new NativeMusic{Source=music,Scene=music.gameObject.scene.handle};
  }
  if(Time.unscaledTime<value.RefreshAt)return;value.RefreshAt=Time.unscaledTime+1;value.Fighting.Clear();
  var states=music.FighingStates;if(states==null)return;
  for(var i=0;i<states.Count&&i<64;i++){var state=states[i];if(state)value.Fighting.Add(state.Pointer);}
 }
 internal static void Forget(MusicController music){if(music is not null)native.Remove(music.Pointer);}
 internal static bool Engaged(StateController controller){
  if(!controller||!controller.currentState)return false;var scene=controller.gameObject.scene.handle;var state=controller.currentState.Pointer;
  foreach(var value in native.Values)if(value.Scene==scene&&value.Source&&value.Fighting.Contains(state))return true;
  return false;
 }
 internal static bool SpendRay(){if(rayFrame!=Time.frameCount){rayFrame=Time.frameCount;rays=0;}if(rays>=8)return false;rays++;return true;}
 internal static bool Prefix(MusicController music){
  try{Observe(music);var current=Together.Current;return current==null||!current.CheckPersonalCombatMusic(music);}
  catch(Exception error){GameplayLog.Fault("personal_music",error);return true;}
 }
}

public sealed partial class Together
{
 sealed class PersonalCombatEnemy {
  internal EnemyHealth Health=null!;internal GameObject? Root;internal StateController? Controller;internal float RetryAt;
 }
 sealed class PersonalCombatSeen {
  internal IntPtr Root;internal readonly PersonalCombatFreshness Fresh=new();
 }
 readonly Dictionary<IntPtr,PersonalCombatEnemy> personalCombatNative=new();
 readonly Dictionary<int,PersonalCombatSeen> personalCombatSeen=new();
 readonly SyncedEnemy?[] personalCombatCandidates=new SyncedEnemy?[16];
 readonly float[] personalCombatDistances=new float[16];
 readonly Il2CppStructArray<RaycastHit> personalCombatHits=new(16);
 string personalCombatEpoch="";IntPtr personalCombatBody;int personalCombatScanCursor,personalCombatRayCursor,personalCombatCount;
 float personalCombatVisibleUntil,personalCombatDamageUntil,personalCombatCheckAt;int personalCombatHp=-1;

 [HideFromIl2Cpp]
 internal void CapturePersonalCombat(EnemyFrame frame,EnemyHealth health,GameObject? enemyRoot){
  frame.Engaged=false;frame.TargetPeerId=-1;
  if(Network?.Hosting!=true||!health||health.dead||health.health<=0)return;
  var controller=PersonalCombatController(health,enemyRoot);if(!controller||controller!.type!=StateController.AiType.Enemy)return;
  frame.Engaged=PersonalCombatMusic.Engaged(controller);var target=controller.aimTarget;
  if(!target)return;
  if(player&&target.IsChildOf(player!.transform)){if(!Down&&!PartyWiped&&player.Health&&!player.Health.died&&player.Health.Health>0)frame.TargetPeerId=Network.LocalId;return;}
  foreach(var peer in remotes.Values)if(PeerMatches(peer)&&peer.State!.Health>0&&peer.Root&&target.IsChildOf(peer.Root!.transform)){frame.TargetPeerId=peer.Id;return;}
 }
 [HideFromIl2Cpp]
 StateController? PersonalCombatController(EnemyHealth health,GameObject? root){
  if(personalCombatNative.TryGetValue(health.Pointer,out var cached)&&cached.Health&&cached.Root==root&&(cached.Controller||Time.unscaledTime<cached.RetryAt))return cached.Controller;
  if(personalCombatNative.Count>=1024)return null;
  StateController? controller=health.GetComponent<StateController>();
  if(!controller&&root){
   var controllers=root!.GetComponentsInChildren<StateController>(true);
   for(var i=0;i<controllers.Length&&i<8;i++){var candidate=controllers[i];if(!candidate)continue;var owner=candidate.GetComponentInParent<EnemyHealth>();if(owner&&owner.Pointer==health.Pointer){controller=candidate;break;}}
  }
  personalCombatNative[health.Pointer]=new PersonalCombatEnemy{Health=health,Root=root,Controller=controller,RetryAt=Time.unscaledTime+2};return controller;
 }
 [HideFromIl2Cpp]
 void ResetPersonalCombatMusic(string epoch,IntPtr body){
  personalCombatEpoch=epoch;personalCombatBody=body;personalCombatSeen.Clear();personalCombatNative.Clear();
  personalCombatScanCursor=personalCombatRayCursor=personalCombatCount=0;personalCombatVisibleUntil=personalCombatDamageUntil=personalCombatCheckAt=0;personalCombatHp=-1;
  Array.Clear(personalCombatCandidates);
 }
 [HideFromIl2Cpp]
 internal bool CheckPersonalCombatMusic(MusicController music){
  // Forced/manual tracks include native scripted scenes and boss music. They
  // remain native; personal threat filtering applies only to automatic raids.
  if(!music||Network==null||!Connected||!SharedRun||music.manualControl||music.controlMode!=MusicController.ControlMode.Auto||music.isChangingMusic||music.gameObject.scene!=SceneManager.GetActiveScene())return false;
  var now=Time.unscaledTime;var epoch=Network.Seed+":"+scene+":"+floor+":"+world;var body=player?player!.Pointer:IntPtr.Zero;
  if(personalCombatEpoch!=epoch||personalCombatBody!=body)ResetPersonalCombatMusic(epoch,body);
  var alive=player&&player!.gameObject.activeInHierarchy&&player.Health&&!player.Health.died&&player.Health.Health>0&&!Down&&!PartyWiped&&!Spectating;
  if(!alive){personalCombatSeen.Clear();personalCombatVisibleUntil=personalCombatDamageUntil=0;personalCombatHp=-1;music.SetMode(MusicController.MusicState.Chill);return true;}
  if(now<personalCombatCheckAt){music.SetMode(now<personalCombatVisibleUntil||now<personalCombatDamageUntil?MusicController.MusicState.InAction:MusicController.MusicState.Chill);return true;}personalCombatCheckAt=now+.25f;
  var hp=player!.Health.Health;var lostHealth=personalCombatHp>=0&&hp<personalCombatHp;personalCombatHp=hp;
  var eye=player.Camera?player.Camera.position:player.transform.position+Vector3.up*1.2f;
  personalCombatCount=0;Array.Clear(personalCombatCandidates);
  // At most512 managed entries per check, fair across larger dictionaries.
  // Positions are already received/captured scalar data, not scene discovery.
  var index=0;var scanned=0;var count=syncedEnemies.Count;if(personalCombatScanCursor>=count)personalCombatScanCursor=0;
  foreach(var enemy in syncedEnemies.Values){
   if(index++<personalCombatScanCursor)continue;if(scanned>=512)break;scanned++;
   var frame=enemy.Frame;if(frame==null||frame.Dead||frame.Health<=0||!enemy.Root||!enemy.Root!.activeInHierarchy)continue;
   var pose=frame.Pose;if(pose==null||pose.Length<3)continue;
   var point=new Vector3(pose[0],pose[1]+1.15f,pose[2]);var distance=(point-eye).sqrMagnitude;
   if(!float.IsFinite(distance)||distance>PersonalCombatMusicPolicy.MaximumDistance*PersonalCombatMusicPolicy.MaximumDistance)continue;
   var fresh=Network.Hosting;
   if(!Network.Hosting){
    if(!personalCombatSeen.TryGetValue(enemy.Id,out var seen)){
     if(personalCombatSeen.Count>=1024)continue;personalCombatSeen[enemy.Id]=seen=new PersonalCombatSeen{Root=enemy.Root.Pointer};
    }else if(seen.Root!=enemy.Root.Pointer){seen.Root=enemy.Root.Pointer;seen.Fresh.Reset();}
    fresh=seen.Fresh.Observe(enemy.Sequence,now);
   }
   if(!fresh||!frame.Engaged||frame.TargetPeerId!=Network.LocalId)continue;
   var insert=personalCombatCount;while(insert>0&&personalCombatDistances[insert-1]>distance)insert--;
   if(insert>=personalCombatCandidates.Length)continue;
   var end=Math.Min(personalCombatCount,personalCombatCandidates.Length-1);for(var n=end;n>insert;n--){personalCombatCandidates[n]=personalCombatCandidates[n-1];personalCombatDistances[n]=personalCombatDistances[n-1];}
   personalCombatCandidates[insert]=enemy;personalCombatDistances[insert]=distance;personalCombatCount=Math.Min(personalCombatCount+1,personalCombatCandidates.Length);
  }
  personalCombatScanCursor=personalCombatScanCursor+scanned>=count?0:personalCombatScanCursor+scanned;
  // A hold must not outlive the last fresh matching target (including TTL,
  // death, retargeting or leaving range). Native chill fade remains untouched.
  if(personalCombatCount==0)personalCombatVisibleUntil=personalCombatDamageUntil=0;
  var visible=false;var qualifiedTarget=false;
  for(var n=0;n<personalCombatCount&&n<8;n++){
   if(personalCombatRayCursor>=personalCombatCount)personalCombatRayCursor=0;var slot=personalCombatRayCursor++;var enemy=personalCombatCandidates[slot]!;var frame=enemy.Frame!;
   var engaged=frame.Engaged;var target=frame.TargetPeerId;
   if(Network.Hosting){
    if(!enemy.Health||enemy.Health!.dead||enemy.Health.health<=0)continue;
    var controller=PersonalCombatController(enemy.Health,enemy.Root);if(!controller||controller!.type!=StateController.AiType.Enemy)continue;
    engaged=PersonalCombatMusic.Engaged(controller);var aim=controller.aimTarget;target=aim&&aim.IsChildOf(player.transform)?Network.LocalId:-1;
   }
   if(!PersonalCombatMusicPolicy.Threat(alive,true,true,engaged,target,Network.LocalId,personalCombatDistances[slot],true))continue;
   qualifiedTarget=true;
   if(!PersonalCombatMusic.SpendRay())break;
   if(PersonalCombatVisible(eye,new Vector3(frame.Pose[0],frame.Pose[1]+1.15f,frame.Pose[2]),enemy.Root!.transform)){visible=true;break;}
  }
  // Damage alone includes falls/self-harm. It bridges cover only while a
  // fresh nearby hostile is actually engaged against this local player.
  if(PersonalCombatMusicPolicy.DamageBridge(alive,lostHealth,qualifiedTarget))personalCombatDamageUntil=now+2;
  // Bridge one bounded candidate rotation. Native chill delay/fade still owns
  // musical exit timing, and cannot be retriggered by a distant host alarm.
  if(visible)personalCombatVisibleUntil=now+1.2f;
  music.SetMode(now<personalCombatVisibleUntil||now<personalCombatDamageUntil?MusicController.MusicState.InAction:MusicController.MusicState.Chill);return true;
 }
 [HideFromIl2Cpp]
 bool PersonalCombatVisible(Vector3 from,Vector3 to,Transform enemy){
  var delta=to-from;var length=delta.magnitude;if(!float.IsFinite(length)||length<.05f)return true;
  var count=Physics.RaycastNonAlloc(from,delta/length,personalCombatHits,length,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);
  // Unsorted/saturated results cannot prove that an omitted wall is absent.
  if(count>=personalCombatHits.Length)return false;
  for(var i=0;i<count;i++){
   var collider=personalCombatHits[i].collider;if(!collider)continue;var hit=collider.transform;
   if(player&&hit.IsChildOf(player!.transform)||hit.IsChildOf(enemy))continue;
   var remote=false;foreach(var peer in remotes.Values)if(peer.Root&&hit.IsChildOf(peer.Root!.transform)){remote=true;break;}
   if(!remote)return false;
  }
  return true;
 }
}

[HarmonyPatch(typeof(MusicController),"Start")]
static class PersonalCombatMusicStarted {static void Postfix(MusicController __instance){try{PersonalCombatMusic.Observe(__instance);}catch(Exception error){GameplayLog.Fault("personal_music_start",error);}}}
[HarmonyPatch(typeof(MusicController),nameof(MusicController.CheckEnemies))]
static class PersonalCombatMusicCheck {static bool Prefix(MusicController __instance)=>PersonalCombatMusic.Prefix(__instance);}
[HarmonyPatch(typeof(MusicController),"OnDisable")]
static class PersonalCombatMusicDisabled {static void Postfix(MusicController __instance)=>PersonalCombatMusic.Forget(__instance);}
