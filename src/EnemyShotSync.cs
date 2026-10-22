using Ballistics;
using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 readonly record struct CapturedEnemyShot(int Id,Vector3 Origin,Vector3 End,float Born);
 sealed class QueuedEnemyShot {internal Packet Packet=null!;internal float Expires;internal bool Accepted,VisualDone,AudioDone;}
 sealed class EnemyShotAsset {internal AudioClip[] Clips=Array.Empty<AudioClip>();internal AudioSource? Template;internal float RetryAt;internal bool MissingReported;}
 sealed class EnemyShotVoice {internal GameObject Root=null!;internal AudioSource Audio=null!;internal float Expires,QuietCheckAt;internal IntPtr Template;}
 struct EnemyShotBudget {internal float Window;internal int Count,Frame;internal long Sequence;}
 readonly Queue<CapturedEnemyShot> capturedEnemyShots=new(32);
 readonly Queue<QueuedEnemyShot> queuedEnemyShots=new(32);
 readonly Dictionary<IntPtr,(Ballistics.Weapon Weapon,int Id)> enemyShotOwners=new(128);
 readonly Dictionary<int,EnemyShotAsset> enemyShotAssets=new(128);
 readonly Dictionary<int,EnemyShotBudget> enemyShotSendBudgets=new(128),enemyShotReceiveBudgets=new(128);
 readonly EnemyShotVoice?[] enemyShotVoices=new EnemyShotVoice?[24];
 readonly EnemyShotVisuals enemyShotVisuals=new();
 Session? enemyShotSession;string enemyShotWorld="",enemyShotScene="";int enemyShotFloor=int.MinValue,enemyShotVoiceCursor;
 long enemyShotSequence,enemyShotsCaptured,enemyShotsSent,enemyShotsReceived,enemyShotsDropped,enemyShotSounds,enemyShotAssetsMissing,enemyShotAudioFallbacks;
 float enemyShotSendWindow,enemyShotReceiveWindow;int enemyShotSendCount,enemyShotReceiveCount;bool enemyShotPresentationReady;

 [HideFromIl2Cpp]
 void EnsureEnemyShotWorld(){
  if(ReferenceEquals(enemyShotSession,Network)&&enemyShotWorld==world&&enemyShotScene==scene&&enemyShotFloor==floor)return;
  ClearEnemyShotEffects();enemyShotSession=Network;enemyShotWorld=world;enemyShotScene=scene;enemyShotFloor=floor;
 }
 [HideFromIl2Cpp]
 SyncedEnemy? EnemyShotOwner(Ballistics.Weapon weapon){
  if(!weapon)return null;
  if(enemyShotOwners.TryGetValue(weapon.Pointer,out var known)&&known.Weapon&&syncedEnemies.TryGetValue(known.Id,out var prior)&&prior.Health&&prior.Root&&weapon.transform.IsChildOf(prior.Root.transform))return prior;
  enemyShotOwners.Remove(weapon.Pointer);
  var health=weapon.GetComponentInParent<EnemyHealth>();
  if(!health||!enemyIds.TryGetValue(health.Pointer,out var id)||!syncedEnemies.TryGetValue(id,out var enemy)||!enemy.Root)return null;
  if(enemyShotOwners.Count<512)enemyShotOwners[weapon.Pointer]=(weapon,id);return enemy;
 }
 [HideFromIl2Cpp]
 internal bool CanCaptureEnemyShot(Ballistics.Weapon weapon){
  if(!SharedRun||Network?.Hosting!=true||!Connected||!weapon)return false;EnsureEnemyShotWorld();
  var owner=EnemyShotOwner(weapon);return owner!=null&&owner.Health&&!owner.Health.dead&&owner.Health.gameObject.activeInHierarchy;
 }
 [HideFromIl2Cpp]
 internal void CaptureNativeEnemyShot(Ballistics.Weapon weapon,int before,IntPtr queuePointer){
  if(!CanCaptureEnemyShot(weapon))return;var enemy=EnemyShotOwner(weapon);if(enemy==null)return;
  var handler=BulletHandler.instance;var queue=handler?handler.Bullets:null;
  if(queue==null||queue.Pointer!=queuePointer||before<0||queue.Count<=before||queue.Count>4096)return;
  var version=queue._version;var count=queue.Count;var storage=queue._array;var length=storage?.Length??0;var tail=queue._tail;
  if(length<1||count>length||tail<0||tail>=length)return;
  var emitted=storage![tail==0?length-1:tail-1];
  if(queue._version!=version||queue.Count!=count||!handler||handler.Bullets==null||handler.Bullets.Pointer!=queuePointer||emitted==null||!emitted.parentWeapon||emitted.parentWeapon.Pointer!=weapon.Pointer)return;
  var origin=emitted.LastPos;var direction=emitted.bulletDir;if(!EnemyShotVector(origin)||!EnemyShotVector(direction)||direction.sqrMagnitude<.00001f)return;
  var now=Time.unscaledTime;var budget=enemyShotSendBudgets.GetValueOrDefault(enemy.Id);
  if(budget.Frame==Time.frameCount)return; // Shotgun pellets share one report.
  if(now-budget.Window>=1){budget.Window=now;budget.Count=0;}
  if(now-enemyShotSendWindow>=1){enemyShotSendWindow=now;enemyShotSendCount=0;}
  if(budget.Count>=40||enemyShotSendCount>=120||capturedEnemyShots.Count>=32||!enemyShotSendBudgets.ContainsKey(enemy.Id)&&enemyShotSendBudgets.Count>=512){enemyShotsDropped++;return;}
  budget.Frame=Time.frameCount;budget.Count++;enemyShotSendBudgets[enemy.Id]=budget;enemyShotSendCount++;
  capturedEnemyShots.Enqueue(new(enemy.Id,origin,origin+direction.normalized*240,now));enemyShotsCaptured++;
 }
 static bool EnemyShotVector(Vector3 value)=>float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z)&&Math.Abs(value.x)<=1000000&&Math.Abs(value.y)<=1000000&&Math.Abs(value.z)<=1000000;

 [HideFromIl2Cpp]
 internal void EnemyShotTick(){
  EnsureEnemyShotWorld();var now=Time.unscaledTime;var presentationReady=SharedRun&&Network?.Hosting==false&&Connected&&RulesReady&&SharedActorWorldReady;enemyShotVisuals.Tick(now,presentationReady);
  if(!presentationReady&&enemyShotPresentationReady)foreach(var voice in enemyShotVoices){if(voice==null)continue;try{if(voice.Audio)voice.Audio.Stop();}catch(Exception error){GameplayLog.Fault("enemyshot.audio_retire",error);}try{if(voice.Root)voice.Root.SetActive(false);}catch(Exception error){GameplayLog.Fault("enemyshot.audio_hide",error);}}
  enemyShotPresentationReady=presentationReady;
  if(!SharedRun||Network==null||!Connected){capturedEnemyShots.Clear();queuedEnemyShots.Clear();return;}
  while(capturedEnemyShots.Count>0){var shot=capturedEnemyShots.Dequeue();
   if(!Network.Hosting||now-shot.Born>.5f){enemyShotsDropped++;continue;}
   var packet=Message("enemyshot");packet.Sequence=++enemyShotSequence;packet.EnemyShot=new EnemyShotFrame{EnemyId=shot.Id,Ray=new[]{shot.Origin.x,shot.Origin.y,shot.Origin.z,shot.End.x,shot.End.y,shot.End.z}};
   if(!packet.EnemyShot.Valid()){enemyShotsDropped++;continue;}Network.Send(packet);enemyShotsSent++;
  }
  var pending=Math.Min(8,queuedEnemyShots.Count);
  for(var i=0;i<pending;i++){var shot=queuedEnemyShots.Dequeue();if(now>=shot.Expires){enemyShotsDropped++;continue;}if(!ApplyEnemyShot(shot))queuedEnemyShots.Enqueue(shot);}
  for(var i=0;i<enemyShotVoices.Length;i++){var voice=enemyShotVoices[i];if(voice==null||!voice.Root||!voice.Root.activeSelf||now<voice.QuietCheckAt)continue;if(now>=voice.Expires||!voice.Audio||!voice.Audio.isPlaying)voice.Root.SetActive(false);}
 }
 [HideFromIl2Cpp]
 internal bool ReceiveEnemyShot(Packet packet){
  if(packet.Kind!="enemyshot")return false;
  if(!SharedRun||Network?.Hosting!=false||!Connected||!PacketWorld(packet)||packet.Sequence<=0||packet.EnemyShot is not {} shot||!shot.Valid())return true;
  EnsureEnemyShotWorld();if(packet.Sequence<=enemyShotReceiveBudgets.GetValueOrDefault(shot.EnemyId).Sequence)return true;
  foreach(var pending in queuedEnemyShots)if(pending.Packet.EnemyShot!.EnemyId==shot.EnemyId&&pending.Packet.Sequence==packet.Sequence)return true;
  if(queuedEnemyShots.Count>=32){queuedEnemyShots.Dequeue();enemyShotsDropped++;}
  queuedEnemyShots.Enqueue(new QueuedEnemyShot{Packet=packet,Expires=Time.unscaledTime+.6f});return true;
 }
 [HideFromIl2Cpp]
 bool ApplyEnemyShot(QueuedEnemyShot pending){
  var packet=pending.Packet;if(Network?.Hosting!=false||!SharedRun||!PacketWorld(packet))return true;
  var shot=packet.EnemyShot!;var budget=enemyShotReceiveBudgets.GetValueOrDefault(shot.EnemyId);if(!pending.Accepted&&packet.Sequence<=budget.Sequence)return true;
  // Recipient readiness only. Another peer's floor/loading state must not mute
  // a valid host event for this recipient while host hurt packets still apply.
  if(!RulesReady||!SharedActorWorldReady||!syncedEnemies.TryGetValue(shot.EnemyId,out var enemy)||!enemy.Root)return false;
  if(enemy.Frame?.Dead==true||enemy.Health&&enemy.Health.dead)return true;
  var ray=shot.Ray;var origin=new Vector3(ray[0],ray[1],ray[2]);if((origin-enemy.Root.transform.position).sqrMagnitude>64)return false;
  var now=Time.unscaledTime;
  if(!pending.Accepted){
   if(now-budget.Window>=1){budget.Window=now;budget.Count=0;}
   if(now-enemyShotReceiveWindow>=1){enemyShotReceiveWindow=now;enemyShotReceiveCount=0;}
   if(budget.Count>=40||enemyShotReceiveCount>=120||!enemyShotReceiveBudgets.ContainsKey(shot.EnemyId)&&enemyShotReceiveBudgets.Count>=512){enemyShotsDropped++;return true;}
   // Receipt/rate commit once; each presentation channel below has its own
   // completion flag, so missing audio cannot block or repeat a visible shot.
   budget.Sequence=packet.Sequence;budget.Count++;enemyShotReceiveBudgets[shot.EnemyId]=budget;enemyShotReceiveCount++;enemyShotsReceived++;pending.Accepted=true;
  }
  if(!pending.VisualDone)try{pending.VisualDone=enemyShotVisuals.TryPlay(shot.EnemyId,enemy.Root,enemy.Health,origin,new Vector3(ray[3],ray[4],ray[5]),now);}catch(Exception error){pending.VisualDone=true;GameplayLog.Fault("enemyshot.visual_prepare",error);enemyShotsDropped++;}
  if(!pending.AudioDone)try{var asset=EnemyShotSoundAsset(enemy);if(asset.Clips.Length>0){pending.AudioDone=true;PlayEnemyShotSound(origin,asset,packet.Sequence);}else enemyShotAssetsMissing++;}catch(Exception error){pending.AudioDone=true;GameplayLog.Fault("enemyshot.play",error);enemyShotsDropped++;}
  return pending.VisualDone&&pending.AudioDone;
 }
 [HideFromIl2Cpp]
 EnemyShotAsset EnemyShotSoundAsset(SyncedEnemy enemy){
  if(enemyShotAssets.TryGetValue(enemy.Id,out var prior)&&Time.unscaledTime<prior.RetryAt&&(prior.RetryAt!=float.PositiveInfinity||prior.Template&&prior.Clips.Length>0))return prior;
  var asset=prior??new EnemyShotAsset();asset.RetryAt=Time.unscaledTime+.25f;
  if(enemyShotAssets.Count>=512&&!enemyShotAssets.ContainsKey(enemy.Id))return asset;
  var controller=enemy.Health?enemy.Health.GetComponent<StateController>():null;if(!controller&&enemy.Root)controller=enemy.Root.GetComponentInChildren<StateController>(true);
  var sound=controller?controller.FireSoundController:null;if(!sound&&enemy.Root)sound=enemy.Root.GetComponentInChildren<FireSoundController>(true);
  if(sound){
   var clips=sound.fireClips;if(clips!=null&&clips.Count>0){var found=new List<AudioClip>(Math.Min(16,clips.Count));for(var i=0;i<clips.Count&&i<32;i++)if(clips[i])found.Add(clips[i]);asset.Clips=found.ToArray();}
   var sources=sound.sources;if(sources!=null)for(var i=0;i<sources.Length;i++)if(sources[i]){asset.Template=sources[i];break;}
   if(!asset.Template){var parent=sound.SoundParent?sound.SoundParent:sound.gameObject;asset.Template=parent.GetComponentInChildren<AudioSource>(true);}
  }
  if(asset.Template&&asset.Clips.Length>0)asset.RetryAt=float.PositiveInfinity;
  if(asset.Clips.Length==0&&!asset.MissingReported){asset.MissingReported=true;GameplayLog.Record("enemyshot_assets_missing",new{enemyId=enemy.Id,enemy.Prefab,clips=asset.Clips.Length,hasSource=(bool)asset.Template});}
  enemyShotAssets[enemy.Id]=asset;return asset;
 }
 [HideFromIl2Cpp]
 void PlayEnemyShotSound(Vector3 origin,EnemyShotAsset asset,long sequence){
  var index=-1;for(var i=0;i<enemyShotVoices.Length;i++)if(enemyShotVoices[i]==null||!enemyShotVoices[i]!.Root||!enemyShotVoices[i]!.Root.activeSelf){index=i;break;}
  if(index<0)index=enemyShotVoiceCursor++%enemyShotVoices.Length;
  var voice=enemyShotVoices[index];var source=asset.Template;var pointer=source?source!.Pointer:IntPtr.Zero;
  if(voice==null||!voice.Root||!voice.Audio){var root=new GameObject("FractureTogether.Enemy shot audio");Object.DontDestroyOnLoad(root);root.SetActive(false);voice=new EnemyShotVoice{Root=root,Audio=root.AddComponent<AudioSource>()};enemyShotVoices[index]=voice;voice.Template=new IntPtr(-1);}
  voice.Audio.Stop();voice.Root.SetActive(false);
  if(voice.Template!=pointer){ConfigureEnemyShotAudio(voice.Audio,source);voice.Template=pointer;if(!source)enemyShotAudioFallbacks++;}
  voice.Root.transform.position=origin;voice.Expires=Time.unscaledTime+5;voice.QuietCheckAt=Time.unscaledTime+.1f;voice.Root.SetActive(true);
  // Only this empty cosmetic AudioSource plays. Native Fire/ShootBullet never
  // runs on the guest; native host audio is never replayed by this channel.
  var clip=asset.Clips[(int)(sequence%asset.Clips.Length)];voice.Audio.PlayOneShot(clip,1);enemyShotSounds++;
 }
 static void ConfigureEnemyShotAudio(AudioSource target,AudioSource? source){
  target.clip=null;target.playOnAwake=false;target.loop=false;target.spatialBlend=1;target.volume=1;target.pitch=1;target.minDistance=5;target.maxDistance=50;target.rolloffMode=AudioRolloffMode.Logarithmic;
  try{target.dopplerLevel=0;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_doppler",e);}
  try{target.outputAudioMixerGroup=null;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_mixer_reset",e);}
  if(!source)return;
  // Some pinned getters are stripped. Copy each independently so one missing
  // getter cannot silently disable all enemy gunfire or clone native scripts.
  try{target.volume=Mathf.Clamp01(source!.volume);}catch(Exception e){GameplayLog.Fault("enemyshot.audio_volume",e);}
  try{var pitch=source!.pitch;if(float.IsFinite(pitch)&&pitch>0)target.pitch=Mathf.Clamp(pitch,.1f,3);}catch(Exception e){GameplayLog.Fault("enemyshot.audio_pitch",e);}
  try{target.outputAudioMixerGroup=source!.outputAudioMixerGroup;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_mixer",e);}
  try{var distance=source!.minDistance;if(float.IsFinite(distance)&&distance>0)target.minDistance=distance;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_min",e);}
  try{var distance=source!.maxDistance;if(float.IsFinite(distance)&&distance>=target.minDistance)target.maxDistance=distance;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_max",e);}
  try{target.rolloffMode=source!.rolloffMode;}catch(Exception e){GameplayLog.Fault("enemyshot.audio_rolloff",e);}
 }
 [HideFromIl2Cpp]
 internal void ClearEnemyShotEffects(){
  enemyShotVisuals.Reset();capturedEnemyShots.Clear();queuedEnemyShots.Clear();enemyShotOwners.Clear();enemyShotAssets.Clear();enemyShotSendBudgets.Clear();enemyShotReceiveBudgets.Clear();
  for(var i=0;i<enemyShotVoices.Length;i++){var voice=enemyShotVoices[i];if(voice!=null&&voice.Root)Object.Destroy(voice.Root);enemyShotVoices[i]=null;}
  enemyShotPresentationReady=false;enemyShotSession=null;enemyShotWorld=enemyShotScene="";enemyShotFloor=int.MinValue;enemyShotVoiceCursor=0;enemyShotSendWindow=enemyShotReceiveWindow=0;enemyShotSendCount=enemyShotReceiveCount=0;
 }
 [HideFromIl2Cpp]
 internal object EnemyShotDiagnostics()=>new{enemyShotsCaptured,enemyShotsSent,enemyShotsReceived,enemyShotSounds,enemyShotsDropped,enemyShotAssetsMissing,enemyShotAudioFallbacks,enemyShotVisuals.Played,enemyShotVisuals.ParticlesPlayed,enemyShotVisuals.TracersPlayed,enemyShotVisuals.MovingTracersPlayed,visualAssetsMissing=enemyShotVisuals.Missing,visualFailures=enemyShotVisuals.Failures,visualPreparations=enemyShotVisuals.Prepared,pending=capturedEnemyShots.Count,queued=queuedEnemyShots.Count,voices=enemyShotVoices.Count(v=>v!=null&&v.Root&&v.Root.activeSelf)};
}

[HarmonyPatch(typeof(Ballistics.Weapon),nameof(Ballistics.Weapon.ShootBullet))]
static class CaptureHostEnemyFirearmShot
{
 struct CaptureState {internal int Count;internal IntPtr Queue;}
 static void Prefix(Ballistics.Weapon __instance,out CaptureState __state){
  __state=new CaptureState{Count=-1};try{if(Together.Current?.CanCaptureEnemyShot(__instance)==true){var handler=BulletHandler.instance;var queue=handler?handler.Bullets:null;if(queue!=null)__state=new CaptureState{Count=queue.Count,Queue=queue.Pointer};}}catch(Exception error){GameplayLog.Fault("enemyshot.capture_prepare",error);}
 }
 static void Postfix(Ballistics.Weapon __instance,CaptureState __state){if(__state.Count<0)return;try{Together.Current?.CaptureNativeEnemyShot(__instance,__state.Count,__state.Queue);}catch(Exception error){GameplayLog.Fault("enemyshot.capture",error);}}
}
