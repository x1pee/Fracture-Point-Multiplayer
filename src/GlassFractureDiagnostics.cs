using Ballistics;
using RayFire;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

// Observation only: never changes native glass, RayFire or SteamAudio state.
internal static class GlassFractureDiagnostics
{
 sealed class Entry {
  internal LivingEntity Owner=null!;internal ExplodingObject Actor=null!;
  internal GameObject Fragments=null!;internal RayfireRigid Rigid=null!;internal bool Verified;
 }
 const int CacheLimit=256,ComponentLimit=1024;
 static readonly Dictionary<IntPtr,Entry> entries=new();
 static int sceneHandle=int.MinValue,revision=int.MinValue;
 static long observed,verified,classificationFaults,explosions,pureExplosions,unitApplies,terminalApplies,logFaults,nextLog;
 static double beforeTotal,nativeTotal,afterTotal,unitTotal,beforeMaximum,nativeMaximum,afterMaximum,unitMaximum;

 static void EnsureWorld(){
  var handle=SceneManager.GetActiveScene().handle;var current= DungeonSeedPatch.Revision;
  if(sceneHandle==handle&&revision==current)return;
  entries.Clear();sceneHandle=handle;revision=current;
 }
 internal static void Observe(LivingEntity owner){
  try{
   EnsureWorld();if(!owner||!owner.ExplodingObject)return;
   var actor=owner.ExplodingObject;
   if(entries.TryGetValue(actor.Pointer,out var prior)&&prior.Actor&&prior.Actor.Pointer==actor.Pointer&&prior.Owner&&prior.Owner.Pointer==owner.Pointer)return;
   if(entries.Count>=CacheLimit)entries.Clear();
   var entry=new Entry{Owner=owner,Actor=actor};entries[actor.Pointer]=entry;observed++;
   entry.Verified=Classify(entry);if(entry.Verified)verified++;
  }catch{classificationFaults++;}
 }
 static bool EmptyOwnerCallbacks(LivingEntity owner){
  if(owner.OnDeath!=null)return false;
  var evt=owner.UnityEvent;if(evt==null)return true;
  if(evt.GetPersistentEventCount()!=0)return false;
  var calls=evt.m_Calls;
  return calls==null||((calls.m_RuntimeCalls==null||calls.m_RuntimeCalls.Count==0)&&
   (calls.m_PersistentCalls==null||calls.m_PersistentCalls.Count==0)&&
   (calls.m_ExecutingCalls==null||calls.m_ExecutingCalls.Count==0));
 }
 static bool Empty(RFEvent? evt)=>evt==null||(evt.LocalEvent==null&&evt.LocalEventMeshRoot==null&&evt.LocalEventRoot==null);
 static bool EmptyGlobals()=>RFDemolitionEvent.GlobalEvent==null&&RFActivationEvent.GlobalEvent==null&&
  RFActivationEvent.GlobalEventRoot==null&&RFRestrictionEvent.GlobalEvent==null&&RFConnectivityEvent.GlobalEvent==null;
 static bool EmptyRigidCallbacks(RayfireRigid rigid){
  if(!Empty(rigid.demolitionEvent)||!Empty(rigid.activationEvent)||!Empty(rigid.restrictionEvent))return false;
  var external=rigid.referenceDemolition;
  return external==null||(!external.reference&&(external.randomList==null||external.randomList.Count==0));
 }
 static string NativeType(MonoBehaviour script)=>script.GetIl2CppType().FullName;
 static bool Classify(Entry entry){
  var owner=entry.Owner;var actor=entry.Actor;
  if(owner.Boss||owner.EnemyHealth||owner.PlayerHealthManager||owner.StartHealth!=2||
   !owner.gameObject.scene.IsValid()||owner.gameObject.scene.handle!=sceneHandle||
   actor.gameObject.Pointer!=owner.gameObject.Pointer||!EmptyOwnerCallbacks(owner)||!EmptyGlobals())return false;
  // Exact hierarchy observed in the saved native street-glass evidence. Other
  // authored panes keep the ordinary chain path until independently identified.
  var intact=owner.transform;var parent=intact.parent;var fragments=actor.Explosion;
  if(intact.name!="glass (1)"||!parent||!parent.name.StartsWith("GlassPanel",StringComparison.Ordinal)||
   !fragments||fragments.activeSelf||fragments.name!="glass (1)_root"||
   fragments.scene.handle!=sceneHandle||fragments.transform.parent!=parent)return false;
  var rigid=fragments.GetComponent<RayfireRigid>();
  if(!rigid||rigid.physics==null||rigid.physics.mt!=RayFire.MaterialType.Glass)return false;
  foreach(var script in owner.GetComponents<MonoBehaviour>()){
   if(!script)continue;var type=NativeType(script);
   if(type!="DefaultBallisticObject"&&type!="ExplodingObject"&&type!="Ballistics.LivingEntity"&&
    type!="Ballistics.LivingEntityCollider"&&type!="SteamAudio.SteamAudioDynamicObject"&&type!="SteamAudio.SteamAudioGeometry")return false;
  }
  var scripts=fragments.GetComponentsInChildren<MonoBehaviour>(true);if(scripts.Length>ComponentLimit)return false;
  foreach(var script in scripts){
   if(!script)continue;var type=NativeType(script);
   if(type=="RayFire.RayfireRigid"){
    var part=script.TryCast<RayfireRigid>();
    if(!part||part.physics==null||part.physics.mt!=RayFire.MaterialType.Glass||!EmptyRigidCallbacks(part))return false;
   }else if(type=="RayFire.RayfireConnectivity"){
    var connectivity=script.TryCast<RayfireConnectivity>();
    if(!connectivity||(connectivity.connectivityEvent!=null&&connectivity.connectivityEvent.LocalEvent!=null))return false;
   }else if(type!="RayFire.RayfireUnyielding"&&type!="DefaultBallisticObject"&&
    type!="SteamAudio.SteamAudioDynamicObject"&&type!="SteamAudio.SteamAudioGeometry")return false;
  }
  entry.Fragments=fragments;entry.Rigid=rigid;return true;
 }
 internal static bool IsVerifiedPureGlass(ExplodingObject actor){
  try{
   EnsureWorld();
   if(!actor||!entries.TryGetValue(actor.Pointer,out var entry)||!entry.Verified||!entry.Actor||
    entry.Actor.Pointer!=actor.Pointer||!entry.Owner||entry.Owner.ExplodingObject!=actor||
    entry.Owner.Boss||entry.Owner.EnemyHealth||entry.Owner.PlayerHealthManager||
    !entry.Fragments||entry.Fragments.activeSelf||actor.Explosion!=entry.Fragments||
    !entry.Rigid||entry.Rigid.physics==null||entry.Rigid.physics.mt!=RayFire.MaterialType.Glass)return false;
   // Recheck mutable owner/global callbacks without a per-break shard census.
   return EmptyOwnerCallbacks(entry.Owner)&&EmptyGlobals()&&EmptyRigidCallbacks(entry.Rigid);
  }catch{classificationFaults++;return false;}
 }
 static double Milliseconds(double value)=>double.IsFinite(value)&&value>0?value:0;
 internal static void RecordExplosion(int id,bool pure,double beforeMs,double nativeMs,double afterMs){
  beforeMs=Milliseconds(beforeMs);nativeMs=Milliseconds(nativeMs);afterMs=Milliseconds(afterMs);
  explosions++;if(pure)pureExplosions++;beforeTotal+=beforeMs;nativeTotal+=nativeMs;afterTotal+=afterMs;
  beforeMaximum=Math.Max(beforeMaximum,beforeMs);nativeMaximum=Math.Max(nativeMaximum,nativeMs);afterMaximum=Math.Max(afterMaximum,afterMs);
  Emit(id,pure,beforeMs,nativeMs,afterMs,0,false);
 }
 internal static void RecordUnitApply(string key,double ms,bool terminal){
  ms=Milliseconds(ms);unitApplies++;if(terminal)terminalApplies++;unitTotal+=ms;unitMaximum=Math.Max(unitMaximum,ms);
  Emit(0,false,0,0,0,ms,terminal);
 }
 static void Emit(int id,bool pure,double beforeMs,double nativeMs,double afterMs,double unitMs,bool terminal){
  try{
   if(!GameplayLog.Operational)return;var now=System.Diagnostics.Stopwatch.GetTimestamp();if(now<nextLog)return;
   nextLog=now+System.Diagnostics.Stopwatch.Frequency;
   // nativeMs measures the immediate Explode call, not deferred OnDestroy,
   // later physics steps or SteamAudio work after this callback returns.
   GameplayLog.Record("glass.fracture_timing",new{id,pure,beforeMs,nativeMs,afterMs,unitMs,terminal,
    observed,verified,classificationFaults,explosions,pureExplosions,unitApplies,terminalApplies,
    beforeTotal,nativeTotal,afterTotal,unitTotal,beforeMaximum,nativeMaximum,afterMaximum,unitMaximum,cache=entries.Count});
  }catch{logFaults++;}
 }
 internal static object Diagnostics()=>new{observed,verified,classificationFaults,explosions,pureExplosions,unitApplies,terminalApplies,
  beforeTotal,nativeTotal,afterTotal,unitTotal,beforeMaximum,nativeMaximum,afterMaximum,unitMaximum,logFaults,cache=entries.Count};
}
