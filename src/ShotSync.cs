using Ballistics;
using HarmonyLib;
using System.Diagnostics;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Native weapons remain the sole source of bullets and damage. This channel
// carries only successful local firearm emissions and renders empty FX objects.
public sealed partial class Together
{
 readonly record struct PendingShot(int WeaponId,Vector3 Origin,Vector3 End,string Scene,string World,int Floor,float Born);
 sealed class ShotEffect {
  internal GameObject Root=null!;internal LineRenderer Trace=null!;internal AudioSource? Audio;
  internal ParticleSystem[] Particles=Array.Empty<ParticleSystem>();internal bool ManualEmit;internal int WeaponId;internal IntPtr MuzzleSource,AudioSource;internal float Until,Expires;internal Light? Flash;internal bool Active,VisualActive;internal float QuietAt;
  internal bool TraceMoving;internal Vector3 TraceOrigin,TraceDirection;internal float TraceBorn,TraceDistance,TraceSpeed,TraceLifetime;
  internal string Scene="",World="";internal int Floor;
  internal void Retire(){
   Active=VisualActive=TraceMoving=false;
   if(Audio)try{Audio!.Stop();}catch(Exception error){GameplayLog.Fault("shot.retire_audio",error);}
   foreach(var particles in Particles)if(particles)try{particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);}catch(Exception error){GameplayLog.Fault("shot.retire_particle",error);}
   if(Trace)Trace.enabled=false;if(Flash)Flash!.enabled=false;if(Root)Root.SetActive(false);
  }
  internal void UpdateTrace(float now){
   if(!TraceMoving)return;
   if(!RemoteTracerPolicy.Sample(now-TraceBorn,TraceDistance,TraceSpeed,TraceLifetime,out var head,out var tail)){TraceMoving=false;Trace.enabled=false;return;}
   // Native Trail width is widest at the moving head and zero at the tail.
   Trace.SetPosition(0,TraceOrigin+TraceDirection*head);Trace.SetPosition(1,TraceOrigin+TraceDirection*tail);Trace.enabled=head>tail;
  }
 }
 readonly Queue<PendingShot> pendingShots=new();
 readonly Queue<(Packet Packet,float Expires)> incomingShots=new();
 readonly Dictionary<int,(long Sequence,float Window,int Count)> shotReceipts=new();
 readonly ShotEffect?[] shotEffects=new ShotEffect?[24];
 readonly Queue<(Packet Packet,float Expires)> queuedRemoteFx=new(RemoteShotFxBudget.QueueCapacity);
 readonly RemoteShotFxBudget remoteFxBudget=new();
 readonly Dictionary<int,RemoteShotAssets.Asset> warmedShotAssets=new();
 readonly int[] warmShotIds=new int[Session.MaxPlayers-1];int warmShotCount;
 readonly int[] pendingFxWeaponIds=new int[RemoteShotFxBudget.QueueCapacity];
 long fxExpired,fxDropped,fxColdCreates,fxWarmCreates;double fxMaxMs;

 long shotSequence,shotsCaptured,shotsSent,shotsReceived,shotsDropped,muzzleEffectsPlayed,tracersPlayed,movingTracersPlayed,shotSoundsPlayed,shotAssetsMissing;int shotEffectCursor,shotPelletFrame=-1;IntPtr shotPelletWeapon;float shotWindow;int shotWindowCount;

 [HideFromIl2Cpp]
 internal bool CanCaptureShot(Ballistics.Weapon weapon){
  if(!Connected||!SameWorld||Down||PartyWiped||!player||!weapon)return false;
  // Match the equipped native firearm itself, excluding NPCs, fragments,
  // inactive inventory weapons and prototype ballistics on the camera.
  var item=player!.GetActiveItem();var firearm=item?item.TryCast<Demo.Scripts.Runtime.Item.Weapon>():null;
  return firearm&&firearm!.WeaponItem!=null&&(int)firearm.shootingType==0&&firearm.gameObject.activeInHierarchy&&firearm.hitManager&&firearm.hitManager.fireBallistics&&firearm.hitManager.fireBallistics.Pointer==weapon.Pointer;
 }
 [HideFromIl2Cpp]
 internal void CaptureNativeShot(Ballistics.Weapon weapon,int before,IntPtr beforeQueue){
  if(!CanCaptureShot(weapon)||shotPelletFrame==Time.frameCount&&shotPelletWeapon==weapon.Pointer)return;
  var handler=BulletHandler.instance;var queue=handler?handler.Bullets:null;if(queue==null||queue.Pointer!=beforeQueue||before<0)return;
  var version=queue._version;var queued=queue.Count;if(queued<=before||queued>4096)return;
  var now=Time.unscaledTime;var newWindow=now-shotWindow>=1;var windowCount=newWindow?0:shotWindowCount;
  if(windowCount>=30||pendingShots.Count>=32){shotsDropped++;return;}
  // Pinned native Queue.Enqueue writes array[tail], then advances/wraps tail.
  // Read that last slot directly; never enumerate/copy the global bullet queue.
  var storage=queue._array;var capacity=storage?.Length??0;var tail=queue._tail;
  if(capacity<=0||queued>capacity||tail<0||tail>=capacity)return;
  var emitted=storage![tail==0?capacity-1:tail-1];
  if(queue._version!=version||queue.Count!=queued||handler!.Bullets==null||handler.Bullets.Pointer!=beforeQueue)return;
  if(emitted==null||!emitted.parentWeapon||emitted.parentWeapon.Pointer!=weapon.Pointer)return;
  var origin=emitted.LastPos;var direction=emitted.bulletDir;
  if(!ShotVector(origin)||!ShotVector(direction)||direction.sqrMagnitude<.00001f)return;direction.Normalize();
  var end=origin+direction*240;
  if(Physics.Raycast(new Ray(origin,direction),out var hit,240,weapon.HitMask,QueryTriggerInteraction.Ignore))end=hit.point;
  if((end-origin).sqrMagnitude<=.0001f)return;
  var firearm=player!.GetActiveItem().TryCast<Demo.Scripts.Runtime.Item.Weapon>();if(!firearm||firearm.WeaponItem==null)return;
  pendingShots.Enqueue(new(firearm.WeaponItem.Id,origin,end,scene,world,floor,now));
  if(newWindow){shotWindow=now;shotWindowCount=0;}shotWindowCount++;
  shotPelletFrame=Time.frameCount;shotPelletWeapon=weapon.Pointer;shotsCaptured++;
 }
 static bool ShotVector(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z)&&Math.Abs(v.x)<=1000000&&Math.Abs(v.y)<=1000000&&Math.Abs(v.z)<=1000000;
 [HideFromIl2Cpp]
 internal void ShotTick(){
  var now=Time.unscaledTime;
  // Reliable events may precede their coalesced pose. Give that pose one short
  // readiness window; accept sequence/rate and relay only at final processing.
  var incomingCount=incomingShots.Count;
  for(var i=0;i<incomingCount;i++){var incoming=incomingShots.Dequeue();if(now>=incoming.Expires){shotsDropped++;continue;}if(!ApplyRemoteShot(incoming.Packet))incomingShots.Enqueue(incoming);}
  var fxWorldChecked=false;var fxWorldReady=false;
  for(var i=0;i<shotEffects.Length;i++){
   var e=shotEffects[i];if(e==null||!e.Active||!e.Root)continue;
   if(!fxWorldChecked){fxWorldReady=Connected&&SameWorld;fxWorldChecked=true;}
   if(!fxWorldReady||e.Scene!=scene||e.World!=world||e.Floor!=floor){e.Retire();continue;}
   e.UpdateTrace(now);if(now<e.Until)continue;
   if(e.VisualActive){e.VisualActive=false;if(!e.TraceMoving)e.Trace.enabled=false;if(e.Flash)e.Flash!.enabled=false;}
   if(now<e.QuietAt&&now<e.Expires)continue;e.QuietAt=now+.1f;
   if(now>=e.Expires||!e.TraceMoving&&!EffectPlaying(e)){e.Root.SetActive(false);e.Active=false;}
  }
  while(pendingShots.Count>0){var shot=pendingShots.Dequeue();
   if(!Connected||!SameWorld||Down||PartyWiped||now-shot.Born>.4f||shot.Scene!=scene||shot.World!=world||shot.Floor!=floor){shotsDropped++;continue;}
   var packet=Message("shot");packet.Sequence=++shotSequence;packet.Shot=new ShotFrame{WeaponId=shot.WeaponId,Ray=new[]{shot.Origin.x,shot.Origin.y,shot.Origin.z,shot.End.x,shot.End.y,shot.End.z}};
   if(!packet.Shot.Valid()){shotsDropped++;continue;}Network!.Send(packet);shotsSent++;
  }
 }
 [HideFromIl2Cpp]
 internal bool ReceiveShot(Packet packet){
  if(packet.Kind!="shot")return false;
  if(!Connected||!PacketWorld(packet)||packet.PlayerId==Network!.LocalId||packet.Shot==null||!packet.Shot.Valid()||packet.Sequence<=0)return true;
  if(incomingShots.Count>=32){shotsDropped++;return true;}
  foreach(var pending in incomingShots)if(pending.Packet.PlayerId==packet.PlayerId&&pending.Packet.Sequence==packet.Sequence)return true;
  incomingShots.Enqueue((packet,Time.unscaledTime+.4f));return true;
 }
 bool ApplyRemoteShot(Packet packet){
  if(!Connected||!PacketWorld(packet)||packet.PlayerId==Network!.LocalId)return true;
  if(!SameWorld||!remotes.TryGetValue(packet.PlayerId,out var peer)||!PeerMatches(peer))return false;
  if(peer.State!.Health<=0)return true;
  var prior=shotReceipts.GetValueOrDefault(packet.PlayerId);if(packet.Sequence<=prior.Sequence)return true;
  var ray=packet.Shot!.Ray;var origin=new Vector3(ray[0],ray[1],ray[2]);var position=peer.State.Pose;
  // A reliable shot may beat the coalesced pose after a same-world warp.
  // Keep the existing bounded readiness window before committing its sequence.
  if((origin-new Vector3(position[0],position[1],position[2])).sqrMagnitude>64)return false;
  if(!shotReceipts.ContainsKey(packet.PlayerId)&&shotReceipts.Count>=64){
   var retired=-1;foreach(var id in shotReceipts.Keys)if(!remotes.ContainsKey(id)){retired=id;break;}
   if(retired<0){shotsDropped++;return true;}shotReceipts.Remove(retired);
  }
  var now=Time.unscaledTime;var count=now-prior.Window>=1?0:prior.Count;var window=count==0?now:prior.Window;
  shotReceipts[packet.PlayerId]=(packet.Sequence,window,count+1);
  if(count>=40){shotsDropped++;return true;}
  if(Network.Hosting)Network.Relay(packet,packet.PlayerId);
  // Notify once after authenticated sequence/rate admission, independently of
  // cosmetic asset readiness and budget. No native gameplay is invoked.
  try{peer.NotifyShot(packet.Shot!.WeaponId,packet.Sequence);}catch(Exception error){GameplayLog.Fault("avatar_shot",error);}
  shotsReceived++;
  if(queuedRemoteFx.Count>=RemoteShotFxBudget.QueueCapacity)fxDropped++;
  else queuedRemoteFx.Enqueue((packet,Time.unscaledTime+RemoteShotFxBudget.Lifetime));
  return true;
 }
 [HideFromIl2Cpp]
 bool PlayRemoteShot(RemoteAvatar peer,ShotFrame shot,long sequence){
  if(!RemoteShotAssets.TryCached(shot.WeaponId,out var asset)||asset==null)return false;
  var muzzle=new Vector3(shot.Ray[0],shot.Ray[1],shot.Ray[2]);
  var forward=new Vector3(shot.Ray[3],shot.Ray[4],shot.Ray[5])-muzzle;
  // If a shot crosses a weapon swap, retain its own native origin rather than
  // attaching the old weapon's shot to the new weapon's rendered muzzle.
  if(peer.State?.WeaponId==shot.WeaponId&&peer.TryGetShotPose(out var visualMuzzle,out var visualForward)){muzzle=visualMuzzle;forward=visualForward;}
  var end=new Vector3(shot.Ray[3],shot.Ray[4],shot.Ray[5]);var delta=end-muzzle;if(delta.sqrMagnitude<.0001f)delta=forward;
  var direction=delta.normalized;
  if(!AcquireShotEffect(asset!,shot.WeaponId,false,out var effect)||effect==null)return false;
  if(effect.Audio)effect.Audio!.Stop();effect.Root.SetActive(false);effect.Root.transform.position=muzzle;effect.Root.transform.rotation=Quaternion.LookRotation(direction);effect.Root.SetActive(true);effect.Until=Time.unscaledTime+.09f;effect.Expires=Time.unscaledTime+5;effect.Active=effect.VisualActive=true;effect.QuietAt=effect.Until;effect.Scene=scene;effect.World=world;effect.Floor=floor;
  if(effect.Flash)effect.Flash!.enabled=true;
  var material=asset.TracerMaterial;effect.TraceMoving=false;effect.Trace.enabled=material;
  if(material){
   effect.Trace.sharedMaterial=material;
   if(asset.MovingTracer&&RemoteTracerPolicy.Duration(delta.magnitude,asset.TracerSpeed,asset.TracerLifetime,out _)){
    effect.Trace.startWidth=asset.TracerStartWidth;effect.Trace.endWidth=asset.TracerEndWidth;
    effect.TraceOrigin=muzzle;effect.TraceDirection=direction;effect.TraceBorn=Time.unscaledTime;effect.TraceDistance=delta.magnitude;effect.TraceSpeed=asset.TracerSpeed;effect.TraceLifetime=asset.TracerLifetime;effect.TraceMoving=true;effect.UpdateTrace(effect.TraceBorn);movingTracersPlayed++;
   }else{
    // Missing/unsupported authored parameters keep the prior bounded pulse.
    effect.Trace.startWidth=.018f;effect.Trace.endWidth=.003f;effect.Trace.SetPosition(0,muzzle);effect.Trace.SetPosition(1,end);
   }
   tracersPlayed++;
  }
  var played=false;foreach(var particles in effect.Particles)if(particles&&particles.gameObject.activeInHierarchy){particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);particles.Play(false);if(effect.ManualEmit||particles.particleCount==0)particles.Emit(1);played=true;}if(played)muzzleEffectsPlayed++;
  var clips=asset.FireClips;if(effect.Audio&&clips.Length>0){var clip=clips[(int)(sequence%clips.Length)];if(clip){effect.Audio!.PlayOneShot(clip,1);shotSoundsPlayed++;}}else{shotAssetsMissing++;if(shotAssetsMissing<=8)GameplayLog.Record("shot.audio_missing",new{weaponId=shot.WeaponId,template=(bool)asset.AudioTemplate,clips=clips.Length});}
  if(!played&&!material&&!effect.Audio)shotAssetsMissing++;return true;
 }
 [HideFromIl2Cpp]
 ShotEffect CreateShotEffect(RemoteShotAssets.Asset asset,int weaponId,IntPtr muzzleSource,IntPtr audioSource){
  var root=new GameObject("Coop shot cosmetic");
  try{
   Object.DontDestroyOnLoad(root);root.SetActive(false);
   var traceObject=new GameObject("Tracer");traceObject.transform.SetParent(root.transform,false);var trace=traceObject.AddComponent<LineRenderer>();ConfigureShotLine(trace,.018f,.003f);
   var effect=new ShotEffect{Root=root,Trace=trace,WeaponId=weaponId,MuzzleSource=muzzleSource,AudioSource=audioSource};
   if(asset.MuzzlePrefab&&asset.MuzzlePrefabSafe){
    // Only a validated visual-only hierarchy is instantiated. No weapon,
    // projectile, collider, Rigidbody or managed/native gameplay script runs.
    var flash=Object.Instantiate(asset.MuzzlePrefab,root.transform,false);flash.name="Native muzzle cosmetic";
    flash.transform.localPosition=Vector3.zero;flash.transform.localRotation=Quaternion.identity;
    foreach(var node in flash.GetComponentsInChildren<Transform>(true))node.gameObject.layer=0;
    effect.Particles=flash.GetComponentsInChildren<ParticleSystem>(true).ToArray();
    foreach(var particles in effect.Particles){var main=particles.main;main.loop=false;main.maxParticles=Math.Min(main.maxParticles,128);if(main.simulationSpace==ParticleSystemSimulationSpace.Custom)main.simulationSpace=ParticleSystemSimulationSpace.Local;particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);}
    flash.SetActive(true);
   }else if(asset.MuzzleParticles){
    // Some native prefabs carry a destroy script on the particle node. Do
    // not instantiate it: copy the authored particle curves/material onto
    // an empty emitter, with no scripts or collision module of its own.
    var source=asset.MuzzleParticles!;var sourceRenderer=source.GetComponent<ParticleSystemRenderer>();
    if(sourceRenderer&&sourceRenderer.sharedMaterial){
     var flash=new GameObject("Native muzzle particle cosmetic");flash.transform.SetParent(root.transform,false);
     var particles=flash.AddComponent<ParticleSystem>();particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);
     var main=particles.main;var native=source.main;main.loop=false;main.maxParticles=8;main.startLifetime=native.startLifetime;main.startSize=native.startSize;main.startColor=native.startColor;main.startSpeed=native.startSpeed;main.scalingMode=native.scalingMode;
     var emission=particles.emission;emission.enabled=false;
     // Shape.enabled is stripped in this build. Its supported cone setters
     // collapse the emitter to the muzzle point without an unstripped call.
     var shape=particles.shape;shape.shapeType=ParticleSystemShapeType.Cone;shape.radius=0;shape.angle=0;shape.length=0;
     particles.GetComponent<ParticleSystemRenderer>().sharedMaterials=sourceRenderer.sharedMaterials;
     effect.Particles=new[]{particles};effect.ManualEmit=true;
    }
   }
   {
    // SoundParent may carry spatial-audio scripts or child nodes. Copy only
    // supported AudioSource settings onto an empty leaf; never reject a valid
    // source because unrelated native components share its GameObject.
    var sound=new GameObject("Native shot audio");sound.transform.SetParent(root.transform,false);
    sound.transform.localPosition=Vector3.zero;sound.transform.localRotation=Quaternion.identity;
    var audio=sound.AddComponent<AudioSource>();audio.Stop();audio.clip=null;audio.playOnAwake=false;audio.loop=false;audio.spatialBlend=1;
    audio.volume=1;audio.minDistance=5;audio.maxDistance=50;audio.rolloffMode=AudioRolloffMode.Logarithmic;
    if(asset.AudioTemplate){var source=asset.AudioTemplate!;try{audio.volume=Mathf.Clamp01(source.volume);}catch(Exception e){GameplayLog.Fault("shot.audio_volume",e);}try{audio.pitch=source.pitch;}catch(Exception e){GameplayLog.Fault("shot.audio_pitch",e);}try{audio.outputAudioMixerGroup=source.outputAudioMixerGroup;}catch(Exception e){GameplayLog.Fault("shot.audio_mixer",e);}}
    effect.Audio=audio;sound.SetActive(true);
   }
   // A short, shadow-free world light also makes a muzzle perceptible when
   // the native particle material is intended for the first-person camera.
   var flashLight=new GameObject("Shot flash light");flashLight.transform.SetParent(root.transform,false);var light=flashLight.AddComponent<Light>();light.type=LightType.Point;light.color=new Color(1,.78f,.43f);light.range=2.2f;light.intensity=1.4f;light.shadows=LightShadows.None;light.enabled=false;effect.Flash=light;
   foreach(var nativeLight in root.GetComponentsInChildren<Light>(true))nativeLight.enabled=false;
   return effect;
  }catch{if(root)Object.Destroy(root);throw;}
 }
 [HideFromIl2Cpp]
 bool AcquireShotEffect(RemoteShotAssets.Asset asset,int weaponId,bool warm,out ShotEffect? effect){
  effect=null;if(Time.unscaledTime<asset.FxRetryAt)return false;var muzzleSource=asset.MuzzlePrefab?asset.MuzzlePrefab!.Pointer:asset.MuzzleParticles?asset.MuzzleParticles!.Pointer:IntPtr.Zero;var audioSource=asset.AudioTemplate?asset.AudioTemplate!.Pointer:IntPtr.Zero;
  var index=-1;var matching=-1;var oldest=float.PositiveInfinity;var ready=0;
  for(var i=0;i<shotEffects.Length;i++){
   var item=shotEffects[i];
   if(item!=null&&item.Root&&item.WeaponId==weaponId&&item.MuzzleSource==muzzleSource&&item.AudioSource==audioSource){
    ready++;if(!warm&&!item.Active){effect=item;return true;}
    if(item.Expires<oldest){oldest=item.Expires;matching=i;}
   }
   if(index<0&&(item==null||!item.Root||warm&&!item.Active&&(item.WeaponId==weaponId?(item.MuzzleSource!=muzzleSource||item.AudioSource!=audioSource):CanRecycleWarmShot(item.WeaponId))))index=i;
  }
  if(warm&&ready>=RemoteShotFxBudget.WarmPerWeapon)return true;
  if(!warm&&index<0&&matching>=0){effect=shotEffects[matching];return true;}
  if(index<0){if(warm)return true;index=shotEffectCursor++%shotEffects.Length;}
  if(!remoteFxBudget.Cold(FxMilliseconds()))return false;
  ShotEffect created;try{created=CreateShotEffect(asset,weaponId,muzzleSource,audioSource);}catch{asset.FxRetryAt=Time.unscaledTime+2;throw;}var previous=shotEffects[index];
  shotEffects[index]=created;if(previous!=null&&previous.Root)Object.Destroy(previous.Root);fxColdCreates++;if(warm)fxWarmCreates++;
  effect=created;return true;
 }
 [HideFromIl2Cpp]
 bool CanRecycleWarmShot(int weaponId){
  var needed=false;for(var i=0;i<warmShotCount;i++)if(warmShotIds[i]==weaponId){needed=true;break;}
  if(!needed)return true;var count=0;foreach(var effect in shotEffects)if(effect!=null&&effect.WeaponId==weaponId&&effect.Root)count++;
  return count>RemoteShotFxBudget.WarmPerWeapon;
 }
 [HideFromIl2Cpp]
 int WarmShotCount(int weaponId,RemoteShotAssets.Asset asset){
  var muzzle=asset.MuzzlePrefab?asset.MuzzlePrefab!.Pointer:asset.MuzzleParticles?asset.MuzzleParticles!.Pointer:IntPtr.Zero;var audio=asset.AudioTemplate?asset.AudioTemplate!.Pointer:IntPtr.Zero;var count=0;
  foreach(var effect in shotEffects)if(effect!=null&&effect.WeaponId==weaponId&&effect.MuzzleSource==muzzle&&effect.AudioSource==audio&&effect.Root)count++;
  return count;
 }
 [HideFromIl2Cpp]
 void WarmShotEffects(){
  // A valid shot must still resolve its assets when its avatar model/IK is
  // unavailable: wire origin remains the fallback. Resolve distinct queued
  // IDs outside reception, sharing the single asset-resolution frame budget.
  var pendingCount=0;
  foreach(var pending in queuedRemoteFx){
   var id=pending.Packet.Shot!.WeaponId;var found=false;
   for(var i=0;i<pendingCount;i++)if(pendingFxWeaponIds[i]==id){found=true;break;}
   if(!found&&pendingCount<pendingFxWeaponIds.Length)pendingFxWeaponIds[pendingCount++]=id;
  }
  for(var i=0;i<pendingCount;i++){
   if(!remoteFxBudget.Available(FxMilliseconds()))break;var id=pendingFxWeaponIds[i];
   if(RemoteShotAssets.TryCached(id,out _)||!RemoteShotAssets.NeedsResolve(id))continue;
   if(!remoteFxBudget.Asset(FxMilliseconds()))break;
   try{RemoteShotAssets.Resolve(id);}catch(Exception error){GameplayLog.Fault("shot.warm",error);}
  }
  warmShotCount=0;
  foreach(var peer in remotes.Values){
   if(!PeerMatches(peer)||!peer.Visible||!peer.HasCharacterModel||peer.State!.Health<=0||peer.State.WeaponId<0)continue;
   var id=peer.State.WeaponId;var found=false;for(var i=0;i<warmShotCount;i++)if(warmShotIds[i]==id){found=true;break;}
   if(!found&&warmShotCount<warmShotIds.Length)warmShotIds[warmShotCount++]=id;
  }
  for(var i=0;i<warmShotCount;i++){
   if(!remoteFxBudget.Available(FxMilliseconds()))break;var id=warmShotIds[i];
   try{
    if(!RemoteShotAssets.TryCached(id,out var asset)){
     if(!RemoteShotAssets.NeedsResolve(id))continue;
     if(!remoteFxBudget.Asset(FxMilliseconds()))continue;
     // Once per model-ready weapon/cache retry, never from a shot callback.
     asset=RemoteShotAssets.Resolve(id);
    }
    if(asset==null||warmedShotAssets.TryGetValue(id,out var warmed)&&ReferenceEquals(warmed,asset)&&WarmShotCount(id,asset)>=RemoteShotFxBudget.WarmPerWeapon)continue;
    AcquireShotEffect(asset,id,true,out _);
    if(WarmShotCount(id,asset)>=RemoteShotFxBudget.WarmPerWeapon){if(warmedShotAssets.Count>=128)warmedShotAssets.Clear();warmedShotAssets[id]=asset;}
   }catch(Exception error){GameplayLog.Fault("shot.warm",error);}
  }
 }
 static double FxMilliseconds()=>Stopwatch.GetTimestamp()*1000d/Stopwatch.Frequency;
 [HideFromIl2Cpp]
 internal void ShotLateTick(){
  var started=FxMilliseconds();remoteFxBudget.Begin(Time.frameCount,started);var now=Time.unscaledTime;
  try{
   var attempts=Math.Min(queuedRemoteFx.Count,RemoteShotFxBudget.QueueCapacity);
   for(var n=0;n<attempts;n++){
    var pending=queuedRemoteFx.Peek();var packet=pending.Packet;
    if(now>=pending.Expires){queuedRemoteFx.Dequeue();fxExpired++;continue;}
    if(!Connected||!PacketWorld(packet)||!SameWorld||!remotes.TryGetValue(packet.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0){queuedRemoteFx.Dequeue();fxDropped++;continue;}
    if(!remoteFxBudget.Effect(FxMilliseconds()))break;
    queuedRemoteFx.Dequeue();
    try{if(!PlayRemoteShot(peer,packet.Shot!,packet.Sequence))queuedRemoteFx.Enqueue(pending);}catch(Exception error){fxDropped++;GameplayLog.Fault("shot.fx",error);}
   }
   WarmShotEffects();
  }finally{var elapsed=FxMilliseconds()-started;if(elapsed>fxMaxMs)fxMaxMs=elapsed;}
 }
 static bool EffectPlaying(ShotEffect effect){if(effect.Audio&&effect.Audio!.isPlaying)return true;foreach(var particles in effect.Particles)if(particles&&particles.IsAlive(false))return true;return false;}
 static void ConfigureShotLine(LineRenderer line,float start,float end){line.enabled=false;line.useWorldSpace=true;line.positionCount=2;line.startWidth=start;line.endWidth=end;line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;line.receiveShadows=false;line.startColor=new Color(1,.86f,.58f,1);line.endColor=new Color(1,.78f,.4f,.65f);}
 [HideFromIl2Cpp]
 internal void ClearShotEffects(){
  pendingShots.Clear();incomingShots.Clear();queuedRemoteFx.Clear();remoteFxBudget.Reset();warmedShotAssets.Clear();warmShotCount=0;shotReceipts.Clear();for(var i=0;i<shotEffects.Length;i++){var e=shotEffects[i];if(e!=null&&e.Root)Object.Destroy(e.Root);shotEffects[i]=null;}
  shotSequence=0;shotPelletFrame=-1;shotPelletWeapon=IntPtr.Zero;shotWindow=0;shotWindowCount=0;shotEffectCursor=0;RemoteShotAssets.Reset();
 }
 [HideFromIl2Cpp]
 internal object ShotDiagnostics()=>new{shotsCaptured,shotsSent,shotsReceived,shotsDropped,muzzleEffectsPlayed,tracersPlayed,movingTracersPlayed,shotSoundsPlayed,shotAssetsMissing,pending=pendingShots.Count,incoming=incomingShots.Count,fxQueue=queuedRemoteFx.Count,fxExpired,fxDropped,fxColdCreates,fxWarmCreates,maxFxMs=fxMaxMs,effects=shotEffects.Count(e=>e!=null&&e.Root),activeEffects=shotEffects.Count(e=>e!=null&&e.Active),activeTracers=shotEffects.Count(e=>e!=null&&e.Active&&e.TraceMoving)};
}

[HarmonyPatch(typeof(Ballistics.Weapon),nameof(Ballistics.Weapon.ShootBullet))]
static class CaptureNativeFirearmShot
{
 struct CaptureState {internal int Count;internal IntPtr Queue;}
 static void Prefix(Ballistics.Weapon __instance,out CaptureState __state){
  __state=new CaptureState{Count=-1};try{if(Together.Current?.CanCaptureShot(__instance)==true){var handler=BulletHandler.instance;var queue=handler?handler.Bullets:null;if(queue!=null)__state=new CaptureState{Count=queue.Count,Queue=queue.Pointer};}}catch(Exception error){GameplayLog.Fault("shot.capture_prepare",error);}
 }
 static void Postfix(Ballistics.Weapon __instance,CaptureState __state){if(__state.Count<0)return;try{Together.Current?.CaptureNativeShot(__instance,__state.Count,__state.Queue);}catch(Exception error){GameplayLog.Fault("shot.capture",error);}}
}
