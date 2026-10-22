using EnemyAI;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Managed presentation only. Never copy an NPC/weapon hierarchy or call its
// native attack/Animator/animation-event path. All owned nodes are empty FX.
internal sealed class EnemyShotVisuals
{
 const int Capacity=24,AssetCapacity=512,PreparationsPerFrame=2;
 sealed class Asset
 {
  internal IntPtr Root,Weapon,Stats;internal StateController? Controller;internal int Revision,Mask=-1;
  internal float RetryAt,Speed,Lifetime,StartWidth=.018f,EndWidth=.003f;internal bool Moving,HasParticles;
  internal Material? LineMaterial,ParticleMaterial;
  internal ParticleSystem.MinMaxCurve ParticleLifetime,ParticleSize,ParticleSpeed;
  internal ParticleSystem.MinMaxGradient ParticleColor;internal ParticleSystemScalingMode Scaling;
 }
 sealed class Effect
 {
  internal GameObject Root=null!;internal LineRenderer Line=null!;internal ParticleSystem Particles=null!;internal Light Light=null!;
  internal int Enemy,Revision;internal bool Active,Moving,MuzzleActive,FlashActive,PulseTrace;internal Vector3 Origin,Direction;
  internal float Born,Distance,Speed,Lifetime,Expires;
 }
 readonly Dictionary<int,Asset> assets=new(128);readonly Effect?[] effects=new Effect?[Capacity];
 int preparationFrame=-1,preparations,cursor;bool clippingUnavailable;
 internal long Played,ParticlesPlayed,TracersPlayed,MovingTracersPlayed,Missing,Failures,Prepared;
 bool Prepare(){var frame=Time.frameCount;if(preparationFrame!=frame){preparationFrame=frame;preparations=0;}if(preparations>=PreparationsPerFrame)return false;preparations++;return true;}
 Asset? Resolve(int id,GameObject root,EnemyHealth? health,float now)
 {
  if(!assets.TryGetValue(id,out var asset)){if(assets.Count>=AssetCapacity)return null;asset=new Asset();assets[id]=asset;}
  var changed=asset.Root!=root.Pointer;
  if(!changed&&asset.Controller is not null&&!asset.Controller)changed=true;
  if(!changed&&asset.Controller){var current=asset.Controller!.Weapon;changed=(current?current.Pointer:IntPtr.Zero)!=asset.Weapon;var currentStats=asset.Controller!.classStats;changed|=(currentStats?currentStats.Pointer:IntPtr.Zero)!=asset.Stats;}
  if(!changed&&asset.Revision>0&&now<asset.RetryAt&&(asset.LineMaterial is null||asset.LineMaterial)&&(asset.ParticleMaterial is null||asset.ParticleMaterial))return asset;
  if(!Prepare())return null;
  // Resolve at most twice/frame, negative retry at most once/second per NPC.
  asset.Root=root.Pointer;asset.RetryAt=now+1;asset.Revision++;asset.HasParticles=false;asset.Moving=false;asset.LineMaterial=null;asset.ParticleMaterial=null;asset.Mask=-1;
  var controller=health?health!.GetComponent<StateController>():null;if(!controller)controller=root.GetComponentInChildren<StateController>(true);asset.Controller=controller;
  var weapon=controller?controller!.Weapon:null;asset.Weapon=weapon?weapon.Pointer:IntPtr.Zero;var stats=controller?controller!.classStats:null;asset.Stats=stats?stats.Pointer:IntPtr.Zero;
  if(weapon){
   asset.Mask=weapon.HitMask.value;var bullet=weapon.BulletPref;
   if(bullet){var line=bullet.GetComponentInChildren<LineRenderer>(true);var trail=line?null:bullet.GetComponentInChildren<TrailRenderer>(true);
    if(line)asset.LineMaterial=line.sharedMaterial;
    else if(trail){asset.LineMaterial=trail.sharedMaterial;var speed=weapon.MaxBulletSpeed;var lifetime=trail.time;var start=trail.startWidth;var end=trail.endWidth;
     if(RemoteTracerPolicy.ValidAsset(speed,start,end,lifetime)){asset.Moving=true;asset.Speed=speed;asset.Lifetime=lifetime;asset.StartWidth=start;asset.EndWidth=end;}}
   }
  }
  if(!asset.LineMaterial)asset.LineMaterial=null;
  // Prefer the authored asset over a possibly short-lived runtime flash.
  var muzzle=stats?stats.muzzleFlash:null;if(!muzzle&&controller)muzzle=controller!.MuzzleEffect;
  var particle=muzzle?muzzle.GetComponentInChildren<ParticleSystem>(true):null;
  var renderer=particle?particle.GetComponent<ParticleSystemRenderer>():null;
  if(particle&&renderer&&renderer.sharedMaterial){var main=particle.main;asset.ParticleMaterial=renderer.sharedMaterial;asset.ParticleLifetime=main.startLifetime;asset.ParticleSize=main.startSize;asset.ParticleSpeed=main.startSpeed;asset.ParticleColor=main.startColor;asset.Scaling=main.scalingMode;asset.HasParticles=true;}
  if(asset.Controller&&asset.Weapon!=IntPtr.Zero&&asset.LineMaterial&&asset.HasParticles)asset.RetryAt=float.PositiveInfinity;
  Prepared++;return asset;
 }
 static Effect Create()
 {
  var root=new GameObject("FractureTogether.Enemy shot cosmetic");
  try{
   Object.DontDestroyOnLoad(root);root.SetActive(false);
   var trace=new GameObject("Enemy cosmetic tracer");trace.transform.SetParent(root.transform,false);var line=trace.AddComponent<LineRenderer>();line.enabled=false;line.useWorldSpace=true;line.positionCount=2;line.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;line.receiveShadows=false;line.startColor=new Color(1,.86f,.58f,1);line.endColor=new Color(1,.78f,.4f,.65f);
   var flash=new GameObject("Enemy cosmetic muzzle particle");flash.transform.SetParent(root.transform,false);var particles=flash.AddComponent<ParticleSystem>();particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);var main=particles.main;main.loop=false;main.maxParticles=8;main.simulationSpace=ParticleSystemSimulationSpace.Local;var emission=particles.emission;emission.enabled=false;
   // Same supported point/cone setters as the player FX; shape.enabled is stripped.
   var shape=particles.shape;shape.shapeType=ParticleSystemShapeType.Cone;shape.radius=0;shape.angle=0;shape.length=0;
   var lamp=new GameObject("Enemy cosmetic muzzle light");lamp.transform.SetParent(root.transform,false);var light=lamp.AddComponent<Light>();light.type=LightType.Point;light.color=new Color(1,.78f,.43f);light.range=1.6f;light.intensity=1.1f;light.shadows=LightShadows.None;light.enabled=false;
   return new Effect{Root=root,Line=line,Particles=particles,Light=light};
  }catch{if(root)Object.Destroy(root);throw;}
 }
 static void Retire(Effect fx)
 {
  fx.Active=fx.Moving=fx.MuzzleActive=fx.FlashActive=fx.PulseTrace=false;
  try{if(fx.Line)fx.Line.enabled=false;}finally{try{if(fx.Light)fx.Light.enabled=false;}finally{try{if(fx.Particles)fx.Particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);}finally{if(fx.Root)fx.Root.SetActive(false);}}}
 }
 static void Configure(Effect fx,int enemy,Asset asset)
 {
  Retire(fx);fx.Enemy=enemy;fx.Revision=asset.Revision;fx.Line.sharedMaterial=asset.LineMaterial;fx.Line.startWidth=asset.Moving?asset.StartWidth:.018f;fx.Line.endWidth=asset.Moving?asset.EndWidth:.003f;
  if(asset.HasParticles){var main=fx.Particles.main;main.startLifetime=asset.ParticleLifetime;main.startSize=asset.ParticleSize;main.startSpeed=asset.ParticleSpeed;main.startColor=asset.ParticleColor;main.scalingMode=asset.Scaling;fx.Particles.GetComponent<ParticleSystemRenderer>().sharedMaterial=asset.ParticleMaterial;}
 }
 Effect? Acquire(int enemy,Asset asset)
 {
  var index=-1;for(var i=0;i<effects.Length;i++){var candidate=effects[i];if(candidate!=null&&candidate.Root&&!candidate.Active&&candidate.Enemy==enemy&&candidate.Revision==asset.Revision)return candidate;if(index<0&&(candidate==null||!candidate.Root||!candidate.Active))index=i;}
  if(index<0)index=cursor++%effects.Length;var fx=effects[index];
  if(fx!=null&&fx.Root&&fx.Enemy==enemy&&fx.Revision==asset.Revision)return fx;
  if(!Prepare())return null;
  if(fx==null||!fx.Root){fx=Create();effects[index]=fx;}Configure(fx,enemy,asset);return fx;
 }
 // false means not yet prepared; true consumes this event even after a partial
 // native FX failure. No uncertain Emit/Play is ever retried for the same event.
 internal bool TryPlay(int enemy,GameObject root,EnemyHealth? health,Vector3 origin,Vector3 end,float now)
 {
  var asset=Resolve(enemy,root,health,now);if(asset==null)return false;var fx=Acquire(enemy,asset);if(fx==null)return false;
  try{
   Retire(fx);var delta=end-origin;var distance=delta.magnitude;if(!float.IsFinite(distance)||distance<=.0001f||distance>300)return true;var direction=delta/distance;
   // The host ray's end is a fixed240m direction, not an authoritative hit.
   // One read-only local query shortens only the cosmetic line at geometry.
   if(!clippingUnavailable)try{if(Physics.Raycast(origin,direction,out var hit,distance,asset.Mask,QueryTriggerInteraction.Ignore))distance=hit.distance;}catch(Exception error){clippingUnavailable=true;GameplayLog.Fault("enemyshot.visual_clip",error);}
   fx.Root.transform.position=origin;fx.Root.transform.rotation=Quaternion.LookRotation(direction);fx.Origin=origin;fx.Direction=direction;fx.Distance=distance;fx.Born=now;fx.Moving=false;fx.Expires=now+.2f;fx.Active=true;fx.Root.SetActive(true);fx.Light.enabled=true;fx.FlashActive=true;
   if(asset.HasParticles&&asset.ParticleMaterial){fx.Particles.Play(false);fx.Particles.Emit(1);fx.MuzzleActive=true;ParticlesPlayed++;}else Missing++;
   if(asset.LineMaterial&&distance>.0001f){
    fx.Moving=asset.Moving&&RemoteTracerPolicy.Duration(distance,asset.Speed,asset.Lifetime,out _);fx.Speed=asset.Speed;fx.Lifetime=asset.Lifetime;
    if(fx.Moving){fx.Expires=now+Math.Max(.2f,distance/asset.Speed+asset.Lifetime);fx.Line.enabled=false;MovingTracersPlayed++;}
    else{fx.Line.SetPosition(0,origin);fx.Line.SetPosition(1,origin+direction*distance);fx.Line.enabled=true;fx.PulseTrace=true;}TracersPlayed++;
   }else Missing++;
   Played++;return true;
  }catch(Exception error){Failures++;try{Retire(fx);}catch{}GameplayLog.Fault("enemyshot.visual",error);return true;}
 }
 internal void Tick(float now,bool ready)
 {
  foreach(var fx in effects){if(fx==null||!fx.Active)continue;
   try{if(!ready||!fx.Root||now>=fx.Expires){Retire(fx);continue;}var elapsed=now-fx.Born;if(fx.FlashActive&&elapsed>=.06f){fx.Light.enabled=false;fx.FlashActive=false;}if(fx.MuzzleActive&&elapsed>=.2f){fx.Particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);fx.MuzzleActive=false;}
    if(fx.Moving){if(RemoteTracerPolicy.Sample(elapsed,fx.Distance,fx.Speed,fx.Lifetime,out var head,out var tail)){fx.Line.SetPosition(0,fx.Origin+fx.Direction*head);fx.Line.SetPosition(1,fx.Origin+fx.Direction*tail);fx.Line.enabled=head>tail;}else{fx.Moving=false;fx.Line.enabled=false;}}
    else if(fx.PulseTrace&&elapsed>=.09f){fx.Line.enabled=false;fx.PulseTrace=false;}
   }catch(Exception error){Failures++;try{Retire(fx);}catch{}GameplayLog.Fault("enemyshot.visual_tick",error);}
  }
 }
 internal void Reset()
 {
  for(var i=0;i<effects.Length;i++){var fx=effects[i];effects[i]=null;if(fx==null)continue;try{Retire(fx);}catch(Exception error){GameplayLog.Fault("enemyshot.visual_reset",error);}finally{try{if(fx.Root)Object.Destroy(fx.Root);}catch(Exception error){GameplayLog.Fault("enemyshot.visual_destroy",error);}}}
  assets.Clear();preparationFrame=-1;preparations=cursor=0;clippingUnavailable=false;
 }
}