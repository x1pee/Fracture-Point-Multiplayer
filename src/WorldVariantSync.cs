using Demo.Scripts.Runtime.Character;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace FractureTogether;

// Pinned native EnableRandomGameObject.Start (9D59C0) is exactly:
// Instantiate(gameObjects[Random.Range(0,gameObjects.Count)], transform).
// Only this authored list is eligible; packets cannot name/create other assets.
// All work stays on Unity's main thread. No census, global RNG scope or timer IO.
static class WorldVariantSync
{
 sealed class Entry {
  public EnableRandomGameObject Selector=null!;public string Key="",Catalogue="";public int Count,Index;public GameObject? Created;
  public long Revision,Applied;public bool Started,Terminal,Active,Send,Retained,NeedSnapshot=true;public float Waiting,Retry;
 }
 static readonly Dictionary<string,Entry> entries=new(StringComparer.Ordinal);
 static readonly List<Entry> ordered=new();
 static readonly Dictionary<string,Packet> decisions=new(StringComparer.Ordinal);
 static readonly Dictionary<string,float> decisionAt=new(StringComparer.Ordinal);static readonly List<string> decisionKeys=new();static int decisionCursor;
 static readonly Dictionary<string,Packet> requests=new(StringComparer.Ordinal);
 static readonly Queue<EnableRandomGameObject> early=new();
 static readonly Queue<int> replayPeers=new();static int replayPeer=-1,replayCursor,cursor;
 static Session? session;static long generation,revision,requestSequence;
 static string epoch="";static int sceneHandle=int.MinValue,epochRevision=-1,epochFloor=-1,epochSeed;static long epochGeneration;static float diagnosticAt,earlyWaiting,activeClock,lastClock;
 static string failure="";static bool failing;
 internal static int Sent,Applied,WaitReplies,Rejected;
 static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
 static void ClearRecords(bool preserveEarly=false){entries.Clear();ordered.Clear();decisions.Clear();decisionAt.Clear();decisionKeys.Clear();decisionCursor=0;requests.Clear();if(!preserveEarly){early.Clear();earlyWaiting=0;}replayPeers.Clear();replayPeer=-1;replayCursor=cursor=0;epoch="";sceneHandle=int.MinValue;epochRevision=-1;epochFloor=-1;epochSeed=0;epochGeneration=0;}
 internal static void Reset(){ClearRecords();session=null;generation=revision=requestSequence=0;activeClock=lastClock=0;failure="";failing=false;}
 static void Bind(Together t){if(ReferenceEquals(session,t.Network))return;Reset();session=t.Network;}
 static void Report(string reason,string key=""){
  Rejected++;if(Time.unscaledTime<diagnosticAt)return;diagnosticAt=Time.unscaledTime+1;
  GameplayLog.Record("variant_sync",new{reason,key,registered=entries.Count,pending=decisions.Count,generation});
  Plugin.Instance.Log.LogWarning("World variant sync: "+reason+" ("+key+").");
 }
 static void Fail(string reason,string key=""){Report(reason,key);if(failure.Length==0)failure=reason;}
 // Called only after SceneChange has accepted the host's native transition.
 internal static long SceneGeneration(Together t,int index){Bind(t);if(t.Network?.Hosting!=true)return generation;if(index==1){generation++;ClearRecords();if(generation>1000000)Fail("generation_limit");}else if(index==2)ClearRecords();return generation;}
 static bool EnsureEpoch(Together t){
  Bind(t);var net=t.Network;if(net==null||net.Seed<=0)return false;
  if(generation<=0){if(!net.Hosting)return false;generation=1;}
  var active=SceneManager.GetActiveScene();var buildRevision=DungeonSeedPatch.Revision;
  if(sceneHandle==active.handle&&epochRevision==buildRevision&&epochGeneration==generation&&epochSeed==net.Seed)return true;
  if(active.name!="URP_OfficeCorridor")return false;
  var dungeon=DungeonSeedPatch.Latest;
  var sameDungeon=dungeon&&dungeon.gameObject.scene.handle==active.handle;
  var floor=sameDungeon?DungeonSeedPatch.BuiltFloor:PlayerPrefs.GetInt("currentFloor",0);
  var nativeSeed=sameDungeon&&dungeon!.Config?dungeon.Config.Seed:0u;
  var next=Hash(FormattableString.Invariant($"{net.Seed}|{generation}|URP_OfficeCorridor|{floor}|{nativeSeed}"));
  if(next!=epoch||sceneHandle!=active.handle){
   // Native scene/dungeon destruction owns old geometry. Never destroy arbitrary
   // arrival cabins, authored props or previous-floor roots on invalidation.
   var sameFloor=sceneHandle==active.handle&&epochGeneration==generation&&epochFloor==floor;
   if(sameFloor){
    // Native Config.Seed can appear after a static selector's Start. Retain its
    // already-created root / pending birth; Start is once-only and cannot rerun.
    var prior=ordered.ToArray();ClearRecords(true);
    foreach(var entry in prior){
     if(!entry.Selector||entry.Selector.gameObject.scene.handle!=active.handle)continue;
     entry.Retained=true;entry.Applied=0;entry.NeedSnapshot=!entry.Terminal;entry.Waiting=activeClock;entry.Retry=0;
     if(net.Hosting&&entry.Started){entry.Revision=++revision;entry.Send=true;}
     entries[entry.Key]=entry;ordered.Add(entry);
    }
   }else{
    // A departing floor must not silently discard an unresolved live birth.
    if(net.Hosting==false&&ordered.Any(e=>e.Selector&&!e.Started&&!e.Terminal))Fail("unsettled_floor_transition");
    ClearRecords(true);
   }
   epoch=next;sceneHandle=active.handle;
  }
  epochRevision=buildRevision;epochFloor=floor;epochSeed=net.Seed;epochGeneration=generation;return true;
 }

 static bool Catalogue(EnableRandomGameObject selector,out string fingerprint,out int count){
  fingerprint="";count=0;var list=selector.gameObjects;if(list==null||(count=list.Count) is <1 or >WorldVariantWire.MaxChoices)return false;
  var names=new HashSet<string>(StringComparer.Ordinal);var text=new StringBuilder();text.Append(count).Append('|');
  for(var i=0;i<count;i++){var prefab=list[i];if(!prefab)return false;var name=prefab.name;if(string.IsNullOrEmpty(name)||name.Length>160||!names.Add(name))return false;text.Append(name.Length).Append(':').Append(name).Append('|');}
  fingerprint=Hash(text.ToString());return true;
 }
 static string Key(EnableRandomGameObject selector){
  var names=new List<string>();var node=selector.transform;
  for(var i=0;node&&i<48;i++,node=node.parent)names.Add(node.name);
  if(node)throw new InvalidOperationException("Selector path too deep");names.Reverse();
  // No sibling index: another selector's newly instantiated child must not change
  // this identity. Position/rotation distinguish repeated named room instances.
  var path=new StringBuilder();foreach(var name in names)path.Append(name.Length).Append(':').Append(name);
  return Hash(path+"|"+Together.SpatialKey(selector.transform));
 }
 static Entry? Register(Together t,EnableRandomGameObject selector){
  if(!selector||selector.gameObject.scene.handle!=sceneHandle)return null;
  if(selector.GetComponents<EnableRandomGameObject>().Length!=1){Fail("multiple_selectors_on_object");return null;}
  if(!Catalogue(selector,out var catalogue,out var count)){Fail("unsupported_catalogue","");return null;}
  var key=Key(selector);
  if(entries.TryGetValue(key,out var known)){
   if(!known.Selector&&known.Retained){entries.Remove(key);ordered.Remove(known);}
   else{
    if(!known.Selector||known.Selector.Pointer!=selector.Pointer){Fail("ambiguous_selector",key);return null;}
    if(known.Catalogue!=catalogue||known.Count!=count){Fail("changed_catalogue",key);return null;}
    return known;
   }
  }
  if(entries.Count>=WorldVariantWire.MaxSelectors){Fail("selector_limit",key);return null;}
  var entry=new Entry{Selector=selector,Key=key,Catalogue=catalogue,Count=count,Waiting=activeClock,Retry=0};entries.Add(key,entry);ordered.Add(entry);return entry;
 }
 internal static bool Start(EnableRandomGameObject selector){
  var t=Together.Current;if(t?.Network==null||selector.gameObject.scene.name!="URP_OfficeCorridor")return true;
  Bind(t);
  // A normal run has an accepted reliable scene-start before these Starts. If a
  // guest's Unity Start overtakes welcome/scene processing, retain the component;
  // never let it roll a competing local variant while the handshake completes.
  if(!EnsureEpoch(t)){
   if(early.Count>=WorldVariantWire.MaxSelectors)Fail("early_selector_limit");else{if(early.Count==0)earlyWaiting=activeClock;early.Enqueue(selector);}
   return false;
  }
  try{
   var entry=Register(t,selector);if(entry==null)return false;
   if(t.Network.Hosting)HostStart(entry);return false;
  }catch(Exception error){GameplayLog.Fault("variant_start",error);Fail("native_birth_failed");return false;}
 }
 static bool SupportedRoot(GameObject prefab)=>prefab&&!prefab.GetComponent<EnemyAI.EnemyHealth>()&&!prefab.GetComponent<FPSController>();
 static void HostStart(Entry entry){
  if(entry.Started||entry.Terminal)return;
  // Consume exactly the native one Range call; do not seed or restore global RNG.
  entry.Index=UnityEngine.Random.Range(0,entry.Count);var prefab=entry.Selector.gameObjects[entry.Index];
  if(!SupportedRoot(prefab)){Fail("actor_root_owned_elsewhere",entry.Key);return;}entry.Started=true;
  entry.Created=Object.Instantiate(prefab,entry.Selector.transform);
  if(!entry.Created){Fail("host_birth_null",entry.Key);return;}
  entry.Active=entry.Created.activeSelf;entry.Revision=++revision;entry.Send=true;entry.NeedSnapshot=false;
  GameplayLog.Record("variant_choice",new{role="host",entry.Key,entry.Catalogue,entry.Index,entry.Count,selector=entry.Selector.name,prefab=prefab.name,generation,epoch,floor=epochFloor});
 }
 static void RefreshHost(Entry entry){if(!entry.Started||entry.Terminal)return;var exists=(bool)entry.Created;var active=exists&&entry.Created!.activeSelf;if(!exists||entry.Active!=active){entry.Terminal=!exists;entry.Active=active;entry.Revision=++revision;entry.Send=true;}}
 static Packet Snapshot(Together t,Entry entry){RefreshHost(entry);return new(){Kind="worldvariant",Seed=t.Network!.Seed,Scene="URP_OfficeCorridor",Floor=epochFloor,World=epoch,VariantGeneration=generation,ObjectId=entry.Key,MachineId=entry.Catalogue,Count=entry.Count,ItemId=entry.Index,Sequence=entry.Revision,Direction=entry.Terminal?-1:1,Success=entry.Active};}
 static Packet Request(Together t,Entry entry)=>new(){Kind="variantreq",Seed=t.Network!.Seed,Scene="URP_OfficeCorridor",Floor=epochFloor,World=epoch,VariantGeneration=generation,ObjectId=entry.Key,MachineId=entry.Catalogue,Count=entry.Count,Sequence=++requestSequence};
 static bool Matches(Together t,Packet p)=>p.Seed==t.Network!.Seed&&p.VariantGeneration==generation&&p.Scene=="URP_OfficeCorridor"&&p.Floor==epochFloor&&p.World==epoch;
 internal static bool Receive(Together t,Packet p){
  Bind(t);
  if(p.Kind=="scene"&&t.Network?.Hosting==false){if(p.ItemId==1){if(p.VariantGeneration<=generation){Report("duplicate_or_stale_scene_generation");return true;}generation=p.VariantGeneration;ClearRecords(true);}else if(p.ItemId==2)ClearRecords();return false;}
  if(p.Kind=="joined"){
   if(t.Network?.Hosting==true&&replayPeers.Count<3)replayPeers.Enqueue(p.PlayerId);
   return false; // AppearanceSync still owns the normal joined path.
  }
  if(p.Kind is not ("variantreq" or "worldvariant"))return false;
  if(!WorldVariantWire.Valid(p)||!EnsureEpoch(t)||!Matches(t,p)){Report("epoch_or_wire_mismatch",p.ObjectId);return true;}
  if(p.Kind=="variantreq"){
   if(t.Network?.Hosting!=true||p.PlayerId<1)return true;
   var key=p.PlayerId+":"+p.ObjectId;
   if(requests.Count>=128&&!requests.ContainsKey(key)){Report("request_limit",p.ObjectId);return true;}
   requests[key]=p;return true;
  }
  if(t.Network?.Hosting!=false)return true;
  if(p.Direction==0){WaitReplies++;return true;}
  if(decisions.TryGetValue(p.ObjectId,out var old)&&old.Sequence>=p.Sequence)return true;
  if(decisions.Count>=WorldVariantWire.MaxSelectors&&!decisions.ContainsKey(p.ObjectId)){Fail("decision_limit",p.ObjectId);return true;}
  if(!decisions.ContainsKey(p.ObjectId)){decisionAt[p.ObjectId]=activeClock;decisionKeys.Add(p.ObjectId);}
  decisions[p.ObjectId]=p;return true;
 }
 static void Apply(Entry entry,Packet p){
  if(p.Sequence<=entry.Applied)return;
  if(entry.Catalogue!=p.MachineId||entry.Count!=p.Count){Fail("catalogue_mismatch",entry.Key);return;}
  if(p.Direction<0){entry.Terminal=true;entry.Applied=p.Sequence;entry.NeedSnapshot=false;entry.Retained=false;if(entry.Created)Object.Destroy(entry.Created);entry.Created=null;Applied++;return;}
  if(!Catalogue(entry.Selector,out var hash,out var count)||hash!=entry.Catalogue||count!=entry.Count){Fail("catalogue_mismatch",entry.Key);return;}
  if(entry.Terminal){entry.Applied=p.Sequence;return;}
  if(entry.Started&&entry.Index!=p.ItemId){Fail("choice_changed",entry.Key);return;}
  if(!entry.Started){
   // Mark BEFORE native Instantiate: an OnEnable fault may occur after native
   // side effects. Quarantine that birth rather than retrying a duplicate.
   entry.Index=p.ItemId;var prefab=entry.Selector.gameObjects[entry.Index];
   if(!SupportedRoot(prefab)){Fail("actor_root_owned_elsewhere",entry.Key);return;}entry.Started=true;
   entry.Created=Object.Instantiate(prefab,entry.Selector.transform);
   if(!entry.Created){Fail("guest_birth_null",entry.Key);return;}
   Together.Current?.InvalidateWorldDiscovery();
  }else if(!entry.Created){Fail("guest_birth_lost",entry.Key);return;}
  if(entry.Created.activeSelf!=p.Success)entry.Created.SetActive(p.Success);
  entry.Active=p.Success;entry.Applied=p.Sequence;entry.NeedSnapshot=false;entry.Retained=false;Applied++;
  GameplayLog.Record("variant_choice",new{role="guest",entry.Key,entry.Catalogue,entry.Index,entry.Count,selector=entry.Selector.name,prefab=entry.Selector.gameObjects[entry.Index].name,generation,epoch,floor=epochFloor});
 }
 internal static void Tick(Together t){
  if(t.Network==null){if(session!=null)Reset();return;}
  Bind(t);if(failing)return;
  if(failure.Length==0&&ordered.Count==0&&early.Count==0&&decisions.Count==0&&requests.Count==0&&replayPeers.Count==0&&replayPeer<0)return;
  var wall=Time.unscaledTime;var delta=lastClock>0?Math.Clamp(wall-lastClock,0,.25f):0;lastClock=wall;
  if(!DungeonSeedPatch.Building)activeClock+=delta;
  if(failure.Length>0){var reason=failure;failure="";failing=true;t.VariantSyncFailure(reason);return;}
  if(!EnsureEpoch(t)){if(early.Count>0&&!DungeonSeedPatch.Building&&activeClock-earlyWaiting>30)Fail("scene_generation_timeout");return;}
  // EnsureEpoch may invalidate an old generation's references. At ordinary
  // scene entry, drain only Starts retained before the scene/welcome became ready.
  var births=4;
  while(births>0&&early.Count>0){births--;var selector=early.Dequeue();if(!selector||selector.gameObject.scene.handle!=sceneHandle)continue;try{var entry=Register(t,selector);if(entry!=null&&t.Network!.Hosting)HostStart(entry);}catch(Exception error){GameplayLog.Fault("variant_early_birth",error);Fail("native_birth_failed");}}
  if(DungeonSeedPatch.Building)return;
  var network=t.Network!;var now=Time.unscaledTime;var budget=Math.Min(8,ordered.Count);var sends=4;
  while(budget-->0&&ordered.Count>0){
   if(cursor>=ordered.Count)cursor=0;var entry=ordered[cursor++];
   try{
    if(network.Hosting){
     if(entry.Retained&&!entry.Selector)continue;
     RefreshHost(entry);
     if(entry.Send&&sends>0&&t.Connected){network.Send(Snapshot(t,entry));entry.Send=false;entry.Retained=false;sends--;Sent++;}
    }else{
     if(!entry.Selector&&(!decisions.TryGetValue(entry.Key,out var terminal)||terminal.Direction>=0)){if(!entry.Retained&&activeClock-entry.Waiting>30)Fail("guest_selector_lost",entry.Key);continue;}
     if(decisions.TryGetValue(entry.Key,out var packet)&&packet.Sequence>entry.Applied&&births>0){births--;Apply(entry,packet);}
     if(entry.NeedSnapshot&&!entry.Terminal){
      if(activeClock-entry.Waiting>30){Fail("choice_timeout",entry.Key);continue;}
      if(sends>0&&t.Connected&&now>=entry.Retry){entry.Retry=now+2;network.Send(Request(t,entry));sends--;}
     }
    }
   }catch(Exception error){GameplayLog.Fault("variant_tick",error);Fail("native_apply_failed",entry.Key);}
  }
  if(!network.Hosting){
   for(var i=Math.Min(4,decisionKeys.Count);i>0;i--){
    if(decisionCursor>=decisionKeys.Count)decisionCursor=0;var key=decisionKeys[decisionCursor++];var decision=decisions[key];
    if(decision.Direction>0&&!entries.ContainsKey(key)&&activeClock-decisionAt[key]>30)Fail("selector_missing",key);
   }
   return;
  }
  // Request processing is bounded, and cannot register/spawn host objects.
  while(sends>0&&requests.Count>0){
   using var iterator=requests.GetEnumerator();iterator.MoveNext();var pair=iterator.Current;requests.Remove(pair.Key);var request=pair.Value;
   if(!Matches(t,request))continue;
   if(entries.TryGetValue(request.ObjectId,out var entry)&&entry.Started){
    if(entry.Catalogue!=request.MachineId||entry.Count!=request.Count){Fail("peer_catalogue_mismatch",entry.Key);continue;}
    network.SendTo(Snapshot(t,entry),request.PlayerId);
   }else{
    request.Kind="worldvariant";request.Direction=0;request.Success=false;network.SendTo(request,request.PlayerId);
   }
   sends--;Sent++;
  }
  while(sends>0){
   if(replayPeer<0){if(replayPeers.Count==0)break;replayPeer=replayPeers.Dequeue();replayCursor=0;}
   if(replayCursor>=ordered.Count){replayPeer=-1;continue;}
   var entry=ordered[replayCursor++];if(!entry.Started)continue;network.SendTo(Snapshot(t,entry),replayPeer);sends--;Sent++;
  }
 }
}

public sealed partial class Together
{
 // Fixed local UI copy. No remote strings or private CLR value types cross the
 // Il2Cpp class injector. Run this from Tick, never inside a native Start callback.
 [HideFromIl2Cpp]
 internal void VariantSyncFailure(string reason){
  GameplayLog.Record("variant_sync_stopped",new{reason});
  StopSession();BeginDisconnectRecovery();
  ShowConnectionIssue(new ConnectionIssue("MP-018","Shared world variants did not match","The session was stopped. Return home and use the same game and mod build on every computer.","world_variant"));
 }
}

[HarmonyPatch(typeof(EnableRandomGameObject),"Start")]
static class AuthoredVariantStart
{
 static bool Prefix(EnableRandomGameObject __instance)=>WorldVariantSync.Start(__instance);
}
