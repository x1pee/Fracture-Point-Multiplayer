using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

// Awake captures the authored instrument once. Receiving a note never performs
// a scene-wide search or instantiates a piano, a UI, or an extra AudioSource.
static class SharedPianoSources
{
 readonly record struct Entry(Piano Source,string Key);
 static readonly Dictionary<int,Entry> instances=new();
 static readonly Dictionary<string,Piano> sources=new();
 static readonly List<int> stale=new();
 internal static string Register(Piano source){
  if(!source||!source.gameObject.scene.IsValid())return "";
  var instance=source.GetInstanceID();
  if(instances.TryGetValue(instance,out var known)&&known.Source&&known.Source.Pointer==source.Pointer)return known.Key;
  if(instances.Count>=64){
   stale.Clear();foreach(var pair in instances)if(!pair.Value.Source)stale.Add(pair.Key);
   foreach(var id in stale){var old=instances[id];if(sources.TryGetValue(old.Key,out var current)&&(!current||current.Pointer==old.Source.Pointer))sources.Remove(old.Key);instances.Remove(id);}
   if(instances.Count>=64)return "";
  }
  var key=Together.SpatialKey(source.transform);if(key.Length is <1 or >256)return "";
  if(instances.TryGetValue(instance,out var replaced)&&replaced.Key!=key&&replaced.Source is not null&&sources.TryGetValue(replaced.Key,out var prior)&&prior is not null&&prior.Pointer==replaced.Source.Pointer)sources.Remove(replaced.Key);
  instances[instance]=new Entry(source,key);sources[key]=source;return key;
 }
 internal static Piano? Find(string key,string scene)=>sources.TryGetValue(key,out var source)&&source&&source.gameObject.activeInHierarchy&&source.gameObject.scene.name==scene?source:null;
 internal static bool ValidNote(Piano source,int index)=>source&&source.gameObject.activeInHierarchy&&source.audioSource&&source.audioSource.isActiveAndEnabled&&index>=0&&index<=PianoNoteWire.MaximumNote&&source.notes!=null&&index<source.notes.Length&&source.notes[index];
 internal static int Index(Piano source,AudioClip clip){
  if(!source||!clip||source.notes==null)return -1;
  var count=Math.Min(source.notes.Length,PianoNoteWire.MaximumNote+1);
  for(var i=0;i<count;i++){var candidate=source.notes[i];if(candidate&&candidate.Pointer==clip.Pointer)return i;}
  return -1;
 }
}

public sealed partial class Together
{
 sealed class PianoBudget {public float Tokens=16,At;}
 readonly Dictionary<int,PianoBudget> pianoBudgets=new();
 readonly PeerTimeline pianoRequests=new();
 bool replayingPiano;long pianoSequence,pianoBroadcast,lastPianoBroadcast;
 int pianoSeed=int.MinValue;
 [HideFromIl2Cpp]
 internal void PianoReset(){pianoBudgets.Clear();pianoRequests.Reset();pianoSequence=pianoBroadcast=lastPianoBroadcast=0;pianoSeed=int.MinValue;replayingPiano=false;}
 void EnsurePianoSession(){if(Network!=null&&pianoSeed!=Network.Seed){PianoReset();pianoSeed=Network.Seed;}}
 bool AdmitPiano(int peer){
  var now=Time.unscaledTime;
  if(!pianoBudgets.TryGetValue(peer,out var bucket)){
   // Authenticated IDs never get reused; keep disconnect churn bounded too.
   if(pianoBudgets.Count>=64){pianoBudgets.Clear();}
   bucket=new PianoBudget{At=now};pianoBudgets[peer]=bucket;
  }
  bucket.Tokens=Math.Min(16,bucket.Tokens+Math.Max(0,now-bucket.At)*32);bucket.At=now;
  if(bucket.Tokens<1)return false;bucket.Tokens--;return true;
 }
 [HideFromIl2Cpp]
 internal void PianoNotePlayed(Piano source,int index){
  if(replayingPiano||Replay||returningAfterDisconnect||!Connected||!RulesReady||Down||!SharedPianoSources.ValidNote(source,index)||source.gameObject.scene.name!=scene||!player)return;
  EnsurePianoSession();var key=SharedPianoSources.Register(source);if(key.Length==0)return;
  if(Vector3.Distance(player!.transform.position,source.transform.position)>7||!AdmitPiano(Network!.LocalId))return;
  var note=Message(Network.Hosting?"pianonote":"pianoreq");var position=source.transform.position;
  note.ObjectId=key;note.ItemId=index;note.Pose=new[]{position.x,position.y,position.z};
  if(Network.Hosting){note.Sequence=++pianoBroadcast;note.TargetId=Network.LocalId;}
  else note.Sequence=++pianoSequence;
  Network.Send(note); // The native local note has already played exactly once.
 }
 void PlayRemotePiano(Piano source,int index){
  var previous=replayingPiano;replayingPiano=true;
  try{source.PlayNote(index);}catch(Exception error){Plugin.Instance.Log.LogWarning("Remote piano note failed: "+error.GetType().Name);}finally{replayingPiano=previous;}
 }
 [HideFromIl2Cpp]
 internal bool ReceivePiano(Packet note){
  if(note.Kind is not ("pianoreq" or "pianonote"))return false;
  if(!Connected||!RulesReady||!PacketWorld(note)||!PianoNoteWire.Valid(note))return true;
  EnsurePianoSession();
  var source=SharedPianoSources.Find(note.ObjectId,scene);if(!source||!SharedPianoSources.ValidNote(source,note.ItemId))return true;
  var position=source.transform.position;
  if((position-new Vector3(note.Pose[0],note.Pose[1],note.Pose[2])).sqrMagnitude>.25f)return true;
  if(Network!.Hosting){
   if(note.Kind!="pianoreq"||!remotes.TryGetValue(note.PlayerId,out var sender)||!PeerMatches(sender)||sender.State!.Health<=0||downPlayers.Contains(note.PlayerId))return true;
   var pose=sender.State.Pose;if(Vector3.Distance(position,new Vector3(pose[0],pose[1],pose[2]))>7||!pianoRequests.Accept(note.PlayerId,note.Sequence)||!AdmitPiano(note.PlayerId))return true;
   PlayRemotePiano(source,note.ItemId);
   var playback=Message("pianonote");playback.ObjectId=note.ObjectId;playback.ItemId=note.ItemId;playback.Pose=new[]{position.x,position.y,position.z};playback.Sequence=++pianoBroadcast;playback.TargetId=note.PlayerId;
   Network.Send(playback);
  }else{
   if(note.Kind!="pianonote"||note.PlayerId!=0||note.Sequence<=lastPianoBroadcast)return true;
   lastPianoBroadcast=note.Sequence;
   if(note.TargetId!=Network.LocalId)PlayRemotePiano(source,note.ItemId); // Skip the performer's host echo.
  }
  return true;
 }
}

[HarmonyPatch(typeof(Piano),"Awake")]
static class SharedPianoAwake {static void Postfix(Piano __instance)=>SharedPianoSources.Register(__instance);}
[HarmonyPatch(typeof(Piano),nameof(Piano.PlayNote),new[]{typeof(int)})]
static class SharedPianoIndex {static void Postfix(Piano __instance,int __0)=>Together.Current?.PianoNotePlayed(__instance,__0);}
[HarmonyPatch(typeof(Piano),nameof(Piano.PlayNote),new[]{typeof(AudioClip)})]
static class SharedPianoClip {static void Postfix(Piano __instance,AudioClip __0){var index=SharedPianoSources.Index(__instance,__0);if(index>=0)Together.Current?.PianoNotePlayed(__instance,index);}}
