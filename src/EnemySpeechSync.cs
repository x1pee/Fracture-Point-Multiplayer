using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

public sealed partial class Together
{
 long enemySpeechSequence;readonly Dictionary<int,long> enemySpeechReceipts=new();public int SentEnemySpeech,ReceivedEnemySpeech;public string LastEnemySpeech="";
 const int EnemyVoiceControllerLimit=32,EnemyVoiceClipReadLimit=1024;
 long speechPackets,speechRejected,speechDuplicates,speechQueued,speechQueueDropped,speechExpired,speechWaitEnemy,speechWaitController,speechWaitSource,speechWaitClip,speechWaitInactive,speechLimits,speechFiltered,speechPlayAttempts,speechPlayFaults,speechFrameDeferred;
 int speechAttemptFrame=-1,speechAttemptsThisFrame;
 float speechLogAt;string speechLastReason="",speechLastClip="";int speechLastEnemy;long speechLastSequence;
 bool? speechSourceEnabled,speechSourceActive,speechSourcePlaying;float? speechSourceVolume,speechSourcePitch;
 sealed class PendingEnemySpeech {public Packet Packet=null!;public float Expires;}
  readonly List<PendingEnemySpeech> pendingEnemySpeech=new();int pendingEnemySpeechCursor;
  sealed class PendingHostEnemySpeech {
   public readonly EnemySoundController Source;public readonly IntPtr Pointer;public readonly string Clip;public readonly long Epoch;public readonly float Expires;
   public PendingHostEnemySpeech(EnemySoundController source,string clip,long epoch,float expires){Source=source;Pointer=source.Pointer;Clip=clip;Epoch=epoch;Expires=expires;}
  }
  readonly List<PendingHostEnemySpeech> pendingHostEnemySpeech=new();int pendingHostEnemySpeechCursor;
  readonly Dictionary<IntPtr,(EnemySoundController Source,int Enemy)> enemySpeechSources=new();
  long enemySpeechEpoch,hostEnemySpeechQueued,hostEnemySpeechExpired,hostEnemySpeechDropped,hostEnemySpeechRetries,enemySpeechResolveTicks,guestEnemySpeechRetryTicks;
  int enemySpeechSeed=int.MinValue,enemySpeechFloor=int.MinValue;string enemySpeechScene="",enemySpeechWorld="";
  [HideFromIl2Cpp]
  internal void ClearEnemySpeechState(){
   pendingHostEnemySpeech.Clear();pendingHostEnemySpeechCursor=0;enemySpeechSources.Clear();enemySpeechReceipts.Clear();pendingEnemySpeech.Clear();pendingEnemySpeechCursor=0;
   enemySpeechEpoch++;enemySpeechSeed=enemySpeechFloor=int.MinValue;enemySpeechScene=enemySpeechWorld="";
   // The wire sequence remains monotonic across floors and reconnects.
  }
  [HideFromIl2Cpp]
  void EnsureEnemySpeechWorld(){
   if(Network==null)return;
   if(enemySpeechSeed==Network.Seed&&enemySpeechFloor==floor&&enemySpeechScene==scene&&enemySpeechWorld==world)return;
   ClearEnemySpeechState();enemySpeechSeed=Network.Seed;enemySpeechFloor=floor;enemySpeechScene=scene;enemySpeechWorld=world;
  }
  [HideFromIl2Cpp]
  public object EnemySpeechDiagnostics()=>new{SentEnemySpeech,ReceivedEnemySpeech,hostPending=pendingHostEnemySpeech.Count,guestPending=pendingEnemySpeech.Count,sourceCache=enemySpeechSources.Count,hostEnemySpeechQueued,hostEnemySpeechExpired,hostEnemySpeechDropped,hostEnemySpeechRetries,resolveMs=enemySpeechResolveTicks*1000.0/System.Diagnostics.Stopwatch.Frequency,guestRetryMs=guestEnemySpeechRetryTicks*1000.0/System.Diagnostics.Stopwatch.Frequency,speechPackets,speechRejected,speechDuplicates,speechQueued,speechQueueDropped,speechExpired,speechWaitEnemy,speechWaitController,speechWaitSource,speechWaitClip,speechWaitInactive,speechLimits,speechFiltered,speechPlayAttempts,speechPlayFaults,speechFrameDeferred,lastReason=speechLastReason,lastEnemy=speechLastEnemy,lastSequence=speechLastSequence,lastClip=speechLastClip,sourceEnabled=speechSourceEnabled,sourceActive=speechSourceActive,sourcePlaying=speechSourcePlaying,sourceVolume=speechSourceVolume,sourcePitch=speechSourcePitch,sourceMute=(bool?)null,muteRead="native getter unavailable",listenerVolume=(float?)null};
 static string SpeechClipIdentity(AudioClip clip)=>clip.name+"|"+clip.samples+"|"+clip.frequency;
  [HideFromIl2Cpp]
  SyncedEnemy? ResolveEnemySpeechSource(EnemySoundController source){
   var started=System.Diagnostics.Stopwatch.GetTimestamp();
   try{
    if(!source||source.gameObject.scene.name!=scene)return null;
    if(enemySpeechSources.TryGetValue(source.Pointer,out var known)&&known.Source&&known.Source.Pointer==source.Pointer&&syncedEnemies.TryGetValue(known.Enemy,out var cached)&&cached.Root&&source.transform.IsChildOf(cached.Root.transform))return cached;
    enemySpeechSources.Remove(source.Pointer);
    foreach(var enemy in syncedEnemies.Values)if(enemy.Root&&source.transform.IsChildOf(enemy.Root.transform)){
     if(enemySpeechSources.Count>=256)enemySpeechSources.Clear();enemySpeechSources[source.Pointer]=(source,enemy.Id);return enemy;
    }
    return null;
   }finally{enemySpeechResolveTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;}
  }
  [HideFromIl2Cpp]
  void SendEnemySpeech(int enemy,string clip){
   var packet=Message("enemyspeech");packet.ItemId=enemy;packet.Sequence=++enemySpeechSequence;packet.ObjectId=clip;packet.Amount=1;Network!.Send(packet);SentEnemySpeech++;LastEnemySpeech=clip;
  }
 [HideFromIl2Cpp]
 internal void EnemySpeech(EnemySoundController source,Il2CppSystem.Collections.Generic.List<AudioClip> requested){
   if(!SharedRun||Network?.Hosting!=true||!Connected||!source||source.gameObject.scene.name!=scene||!source.Source||source.playableSounds==null||source.playableSounds.Count==0||!source.soundIndices.TryGetValue(requested,out var next))return;
  // Native PlaySound uses PlayOneShot, which leaves AudioSource.clip unchanged.
  // Its saved index points at the NEXT clip in the streamer-filtered list.
  var count=source.playableSounds.Count;var clip=source.playableSounds[(next+count-1)%count];if(!clip)return;
   // A first native utterance can run before the bounded NPC birth pass. Align
   // the epoch before retaining it, so the next EnemyTick cannot erase it.
   if(enemyEpoch!=world){ClearEnemies();enemyEpoch=world;}EnsureEnemySpeechWorld();
   var identity=SpeechClipIdentity(clip);var enemy=ResolveEnemySpeechSource(source);
   // A newly bound speaker must not overtake its own older retained utterance.
   if(enemy!=null&&!pendingHostEnemySpeech.Any(p=>p.Pointer==source.Pointer)){SendEnemySpeech(enemy.Id,identity);return;}
   if(pendingHostEnemySpeech.Count>=64){pendingHostEnemySpeech.RemoveAt(0);if(pendingHostEnemySpeechCursor>0)pendingHostEnemySpeechCursor--;hostEnemySpeechDropped++;}
   pendingHostEnemySpeech.Add(new PendingHostEnemySpeech(source,identity,enemySpeechEpoch,Time.unscaledTime+2));hostEnemySpeechQueued++;
 }
  [HideFromIl2Cpp]
  internal void PendingHostEnemySpeechTick(){
   EnsureEnemySpeechWorld();
   if(!SharedRun||Network?.Hosting!=true||!Connected){hostEnemySpeechDropped+=pendingHostEnemySpeech.Count;pendingHostEnemySpeech.Clear();pendingHostEnemySpeechCursor=0;return;}
   var count=Math.Min(4,pendingHostEnemySpeech.Count);var now=Time.unscaledTime;
   for(var i=0;i<count&&pendingHostEnemySpeech.Count>0;i++){
    if(pendingHostEnemySpeechCursor>=pendingHostEnemySpeech.Count)pendingHostEnemySpeechCursor=0;
    var pending=pendingHostEnemySpeech[pendingHostEnemySpeechCursor];
    if(pending.Epoch!=enemySpeechEpoch||!pending.Source||pending.Source.Pointer!=pending.Pointer){pendingHostEnemySpeech.RemoveAt(pendingHostEnemySpeechCursor);hostEnemySpeechDropped++;continue;}
    if(now>=pending.Expires){pendingHostEnemySpeech.RemoveAt(pendingHostEnemySpeechCursor);hostEnemySpeechExpired++;continue;}
    var earlier=false;for(var j=0;j<pendingHostEnemySpeechCursor;j++)if(pendingHostEnemySpeech[j].Pointer==pending.Pointer){earlier=true;break;}
    if(earlier){pendingHostEnemySpeechCursor++;continue;}
    hostEnemySpeechRetries++;var enemy=ResolveEnemySpeechSource(pending.Source);
    if(enemy!=null){pendingHostEnemySpeech.RemoveAt(pendingHostEnemySpeechCursor);SendEnemySpeech(enemy.Id,pending.Clip);}else pendingHostEnemySpeechCursor++;
   }
  }
 [HideFromIl2Cpp]
 bool ReceiveEnemySpeech(Packet packet){
  if(packet.Kind!="enemyspeech")return false;speechPackets++;
   if(!SharedRun||Network?.Hosting!=false||!PacketWorld(packet)){speechRejected++;EnemySpeechObserve(packet,"role_or_world",null);return true;}
   if(enemyEpoch!=world){ClearEnemies();enemyEpoch=world;}EnsureEnemySpeechWorld();
   if(packet.Sequence<=enemySpeechReceipts.GetValueOrDefault(packet.ItemId)){speechDuplicates++;return true;}
  // Reliable events can overtake the coalesced NPC snapshot. Retain a short
  // utterance until that snapshot creates its source, without replaying old
  // floor dialogue or accumulating a loading-time audio backlog.
   if(!pendingEnemySpeech.Any(p=>p.Packet.ItemId==packet.ItemId)&&TryApplyEnemySpeech(packet))return true;
  if(pendingEnemySpeech.Any(p=>p.Packet.ItemId==packet.ItemId&&p.Packet.Sequence==packet.Sequence))return true;
   if(pendingEnemySpeech.Count>=64){pendingEnemySpeech.RemoveAt(0);if(pendingEnemySpeechCursor>0)pendingEnemySpeechCursor--;speechQueueDropped++;}
  pendingEnemySpeech.Add(new PendingEnemySpeech{Packet=packet,Expires=Time.unscaledTime+2});speechQueued++;return true;
 }
 void PendingEnemySpeechTick(){
   if(pendingEnemySpeech.Count==0)return;EnsureEnemySpeechWorld();
   var started=System.Diagnostics.Stopwatch.GetTimestamp();
    // Retry at most four records without letting a later utterance consume its
    // speaker's receipt before an older retained one gets a chance to bind.
   try{for(var n=Math.Min(4,pendingEnemySpeech.Count);n>0&&pendingEnemySpeech.Count>0;n--){
    if(pendingEnemySpeechCursor>=pendingEnemySpeech.Count)pendingEnemySpeechCursor=0;
    var pending=pendingEnemySpeech[pendingEnemySpeechCursor];var packet=pending.Packet;
    if(!SharedRun||Network?.Hosting!=false||!PacketWorld(packet)||Time.unscaledTime>=pending.Expires||packet.Sequence<=enemySpeechReceipts.GetValueOrDefault(packet.ItemId)){if(Time.unscaledTime>=pending.Expires){speechExpired++;EnemySpeechObserve(packet,"expired",null);}pendingEnemySpeech.RemoveAt(pendingEnemySpeechCursor);continue;}
    var earlier=false;for(var j=0;j<pendingEnemySpeechCursor;j++)if(pendingEnemySpeech[j].Packet.ItemId==packet.ItemId){earlier=true;break;}
    if(!earlier&&TryApplyEnemySpeech(packet))pendingEnemySpeech.RemoveAt(pendingEnemySpeechCursor);else pendingEnemySpeechCursor++;
   }}finally{guestEnemySpeechRetryTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;}
 }
 [HideFromIl2Cpp]
 void EnemySpeechObserve(Packet packet,string reason,AudioSource? audio){
  speechLastReason=reason;speechLastEnemy=packet.ItemId;speechLastSequence=packet.Sequence;speechLastClip=packet.ObjectId;
  speechSourceEnabled=speechSourceActive=speechSourcePlaying=null;speechSourceVolume=speechSourcePitch=null;
  // Observation must not change playback or make a native state-read fault fatal.
  try{if(audio){speechSourceEnabled=audio!.enabled;speechSourceActive=audio.gameObject.activeInHierarchy;speechSourcePlaying=audio.isPlaying;speechSourceVolume=audio.volume;speechSourcePitch=audio.pitch;}}catch{}
  // Mute and listener-volume getters are stripped from the pinned native API.
  // Unknown stays explicit; no guessed getter or player audio setting changes.
  var now=Time.unscaledTime;if(now<speechLogAt)return;speechLogAt=now+5;
  GameplayLog.Record("enemy_speech_state",new{reason,enemy=packet.ItemId,sequence=packet.Sequence,clip=packet.ObjectId,sourceEnabled=speechSourceEnabled,sourceActive=speechSourceActive,sourcePlaying=speechSourcePlaying,sourceVolume=speechSourceVolume,sourcePitch=speechSourcePitch,sourceMute=(bool?)null,muteRead="native getter unavailable",listenerVolume=(float?)null,guestPending=pendingEnemySpeech.Count});
 }
 [HideFromIl2Cpp]
 bool EnemySpeechClip(EnemySoundController source,string identity,ref int remaining,out AudioClip? match,out bool limited){
  match=null;limited=false;
  foreach(var list in new[]{source.EyeContactSounds,source.HearAudioSounds,source.DamageTakenSounds,source.WhileFiringSounds,source.DeadbodySounds,source.GrenadeSounds}){
   if(list==null)continue;var count=list.Count;
   for(var i=0;i<count;i++){
    if(remaining<=0){limited=true;return false;}remaining--;
    var clip=list[i];if(clip&&SpeechClipIdentity(clip)==identity){match=clip;return true;}
   }
  }return false;
 }
 [HideFromIl2Cpp]
 bool TryApplyEnemySpeech(Packet packet){
  // Reception may bypass the pending retry loop. Share its four-attempt frame
  // cap so a reliable-message burst cannot multiply native clip searches.
  var frame=Time.frameCount;if(frame!=speechAttemptFrame){speechAttemptFrame=frame;speechAttemptsThisFrame=0;}
  if(speechAttemptsThisFrame>=4){speechFrameDeferred++;EnemySpeechObserve(packet,"frame_budget",null);return false;}speechAttemptsThisFrame++;
  if(!syncedEnemies.TryGetValue(packet.ItemId,out var enemy)||!enemy.Root){speechWaitEnemy++;EnemySpeechObserve(packet,"enemy_missing",null);return false;}
  var sources=enemy.Root.GetComponentsInChildren<EnemySoundController>(true);
  if(sources.Length>EnemyVoiceControllerLimit){speechLimits++;EnemySpeechObserve(packet,"controller_limit",null);return false;}
  var count=sources.Length;var remaining=EnemyVoiceClipReadLimit;var limited=false;
  EnemySoundController? selected=null;AudioClip? selectedClip=null;AudioSource? selectedAudio=null;var anyController=false;var matchedClip=false;
  for(var i=0;i<count;i++){
   var source=sources[i];if(!source)continue;anyController=true;
   if(!EnemySpeechClip(source,packet.ObjectId,ref remaining,out var clip,out var clipLimited)){limited|=clipLimited;if(limited)break;continue;}
   limited|=clipLimited;matchedClip=true;var audio=source.Source;if(!audio)continue;
   // Match exact clip identity, then prefer an active authored source. A first
   // inactive controller must not hide a later active owner of the same clip.
   if(selected==null||!selectedAudio!.gameObject.activeInHierarchy&&audio!.gameObject.activeInHierarchy){selected=source;selectedClip=clip;selectedAudio=audio;}
   if(audio!.gameObject.activeInHierarchy)break;
  }
  // One shared budget covers every controller and category in this attempt.
  // An incomplete search is explicit and never guesses a remaining owner.
  if(limited){speechLimits++;EnemySpeechObserve(packet,"clip_search_limit",selectedAudio);return false;}
  if(!selected||!selectedClip||!selectedAudio){
   if(!anyController){speechWaitController++;EnemySpeechObserve(packet,"controller_missing",null);}
   else if(!matchedClip){speechWaitClip++;EnemySpeechObserve(packet,"clip_missing",null);}
   else{speechWaitSource++;EnemySpeechObserve(packet,"source_missing",null);}
   return false;
  }
  // Streamer mode remains a local preference, consumed once without native
  // reselection or activating any authored hierarchy.
  if(selected.StreamerMode&&selected.StreamerModeExcludedSounds!=null&&selected.StreamerModeExcludedSounds.Contains(selectedClip)){
   enemySpeechReceipts[packet.ItemId]=packet.Sequence;speechFiltered++;EnemySpeechObserve(packet,"streamer_excluded",selectedAudio);return true;
  }
  // AudioSource.enabled cannot activate its GameObject/ancestors. Retain the
  // existing bounded retry instead of counting a void call on an inactive GO.
  if(!selectedAudio.gameObject.activeInHierarchy){speechWaitInactive++;EnemySpeechObserve(packet,"source_inactive",selectedAudio);return false;}
  selectedAudio.enabled=true;
  if(!selectedAudio.enabled){speechWaitInactive++;EnemySpeechObserve(packet,"source_disabled",selectedAudio);return false;}
  // Commit before native side effects, retaining at-most-once invocation if a
  // native call faults after entering playback. No global audio interception.
  enemySpeechReceipts[packet.ItemId]=packet.Sequence;speechPlayAttempts++;
  try{selectedAudio.PlayOneShot(selectedClip,Mathf.Clamp01(packet.Amount));ReceivedEnemySpeech++;LastEnemySpeech=packet.ObjectId;EnemySpeechObserve(packet,"play_invoked",selectedAudio);}
  catch(Exception error){speechPlayFaults++;EnemySpeechObserve(packet,"play_fault",selectedAudio);GameplayLog.Fault("enemy_speech_play",error);}
  return true;
 }
}
[HarmonyPatch(typeof(EnemySoundController),nameof(EnemySoundController.PlaySound))]
static class HostEnemySpeech
{
 static bool Prefix(ref bool __result){if(!Together.GuestEnemyAuthority)return true;__result=false;return false;}
 static void Postfix(EnemySoundController __instance,Il2CppSystem.Collections.Generic.List<AudioClip> __0,bool __result){if(__result)Together.Current?.EnemySpeech(__instance,__0);}
}
