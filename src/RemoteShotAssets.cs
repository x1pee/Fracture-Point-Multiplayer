using Ballistics;
using Demo.Scripts.Runtime.Character;
using KINEMATION.FPSAnimationFramework.Runtime.Core;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.PoseSamplerLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.IkLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Layers.AdsLayer;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using UnityEngine;
using Object=UnityEngine.Object;
using Firearm=Demo.Scripts.Runtime.Item.Weapon;
namespace FractureTogether;

// Read native catalogue assets only. Consumers copy renderer data onto their
// own empty objects; no FPSItem, weapon, loot or ballistic behaviour is cloned.
static class RemoteShotAssets
{
 internal sealed class Asset {
  internal Firearm? Weapon;
  internal Transform? VisualSource,MuzzleSource,AimSource;
  internal AudioClip[] FireClips=Array.Empty<AudioClip>();
  internal Material? TracerMaterial;
  internal bool MovingTracer;internal float TracerSpeed,TracerStartWidth,TracerEndWidth,TracerLifetime;
  internal GameObject? MuzzlePrefab;
  internal bool MuzzlePrefabSafe;internal float FxRetryAt;
  internal ParticleSystem? MuzzleParticles;
  internal AudioSource? AudioTemplate;
  internal PoseSamplerLayerSettings? Pose;
  internal IkLayerSettings? Ik;
  internal AdsLayerSettings? Ads;
  internal FPSAnimationAsset? GrenadeAnimation;
  internal FPSAnimationAsset? ReloadAnimation,ReloadEmptyAnimation;
  // Baked managed data belongs to this catalogue entry; pulses stay per avatar.
  internal RemoteFireMotion.Profile? FireMotionProfile;internal float FireMotionRetryAt;
 }
 static readonly Dictionary<int,(Asset? Value,float RetryAt)> cache=new();
 static WeaponsData? catalogue;static Inventory? inventory;static float retryAt;
 static AudioSource? nativeFireAudio;static IntPtr audioOwner;static float audioRetry;
 static GameObject? nativeMuzzle;static float muzzleRetry;
 internal static Firearm? ActiveWeapon {get;private set;}
 internal static Ballistics.Weapon? ActiveBallistics {get;private set;}
 internal static int HeldWeaponId {get;private set;}=-1;
 internal static void Tick(FPSController? player){
  ActiveWeapon=null;ActiveBallistics=null;HeldWeaponId=-1;
  if(!player)return;
  var active=player!.GetActiveItem();var firearm=active?active.TryCast<Firearm>():null;
  if(firearm&&firearm!.gameObject.activeInHierarchy&&firearm.WeaponItem!=null){
   ActiveWeapon=firearm;HeldWeaponId=firearm.WeaponItem.Id;
   var manager=firearm.hitManager;var ballistics=manager?manager.fireBallistics:null;
   if((int)firearm.shootingType==0&&ballistics&&ballistics.transform.IsChildOf(player.transform))ActiveBallistics=ballistics;
   if(firearm.WeaponsData)Bind(firearm.WeaponsData);
   RememberNativeMuzzle(firearm);
   var sound=firearm.fireSound;
   if(sound&&(audioOwner!=sound.Pointer||!nativeFireAudio&&Time.unscaledTime>=audioRetry)){
    audioOwner=sound.Pointer;audioRetry=Time.unscaledTime+2;nativeFireAudio=FindAudio(sound);
   }
  }
  if(catalogue||Time.unscaledTime<retryAt)return;retryAt=Time.unscaledTime+2;
  if(!inventory)inventory=Object.FindAnyObjectByType<Inventory>();
  if(inventory&&inventory!.WeaponData)Bind(inventory.WeaponData);
 }
 static void Bind(WeaponsData value){if(catalogue&&catalogue!.Pointer==value.Pointer)return;catalogue=value;cache.Clear();}
 static void RememberNativeMuzzle(Firearm firearm){
  // Native Weapon.OnEquip 8AB35B/8AB364/8AB370 resolves this exact authored
  // scene object and stores it in the runtime-only fireParticlesEffect field.
  // A catalogue prefab has not run OnEquip and need not contain that reference.
  var candidate=firearm.fireParticlesEffect;
  if(candidate&&nativeMuzzle&&nativeMuzzle!.Pointer==candidate.Pointer)return;
  if(!candidate){
   if(nativeMuzzle||Time.unscaledTime<muzzleRetry)return;
   muzzleRetry=Time.unscaledTime+2;candidate=GameObject.Find("MuzzleFlash10");
  }
  if(!candidate||candidate!.name!="MuzzleFlash10"||!candidate.scene.IsValid())return;
  nativeMuzzle=candidate;cache.Clear();
 }
 internal static Asset? Resolve(int id){
  if(id<0||id>100000||!catalogue||catalogue!.Weapons==null)return null;
  if(cache.TryGetValue(id,out var ready)&&Time.unscaledTime<ready.RetryAt&&
     (ready.Value==null||ready.Value.VisualSource&&ready.Value.Weapon&&
      (ready.RetryAt!=float.PositiveInfinity||Complete(ready.Value))))return ready.Value;
  if(cache.Count>=128)cache.Clear();
  WeaponItem? definition=null;foreach(var entry in catalogue.Weapons)if(entry!=null&&entry.Id==id){definition=entry;break;}
  if(definition==null||!definition.Weapon){cache[id]=(null,Time.unscaledTime+2);return null;}
  var weapon=definition.Weapon;var result=new Asset{Weapon=weapon,VisualSource=weapon.transform};
  // Native Weapon.OnEquip8AB014 initializes this same-GO reference. Catalogue
  // prefabs have not run OnEquip:26/30 pinned fields are null despite a manager.
  var manager=weapon.hitManager?weapon.hitManager:weapon.GetComponent<FPSFireManager>();var ballistics=manager?manager.fireBallistics:null;
  result.MuzzleSource=weapon.FireParticlePosition?weapon.FireParticlePosition:ballistics&&ballistics.VisualSpawnPoint?ballistics.VisualSpawnPoint:null;
  var clips=weapon.fireClips;var valid=new List<AudioClip>(8);if(clips!=null)for(var i=0;i<clips.Count&&valid.Count<8;i++)if(clips[i])valid.Add(clips[i]);
  // The native sound controller owns the actual playback list after equip.
  // Catalogue firearm lists can be empty while this runtime list is ready.
  if(valid.Count==0&&weapon.fireSound&&weapon.fireSound!.fireClips!=null)foreach(var clip in weapon.fireSound.fireClips){if(clip)valid.Add(clip);if(valid.Count>=8)break;}
  if(valid.Count==0&&ActiveWeapon&&HeldWeaponId==id&&ActiveWeapon!.fireSound&&ActiveWeapon.fireSound!.fireClips!=null)foreach(var clip in ActiveWeapon.fireSound.fireClips){if(clip)valid.Add(clip);if(valid.Count>=8)break;}
  result.FireClips=valid.ToArray();
  // A particle/view-model material is not a LineRenderer material. Only reuse
  // an authored line/trail material for the optional tracer; the muzzle uses
  // its native particle prefab, including its own renderer and shader.
  if(ballistics&&ballistics.BulletPref){
   var line=ballistics.BulletPref.GetComponentInChildren<LineRenderer>(true);
   var trail=line?null:ballistics.BulletPref.GetComponentInChildren<TrailRenderer>(true);
   if(line)result.TracerMaterial=line.sharedMaterial;
   else if(trail){
    result.TracerMaterial=trail.sharedMaterial;
    // Pinned getters:Trail time33EC280/startWidth33EC3D0/endWidth33EC520.
    // MaxBulletSpeed is the native nominal firearm field at+3C. Freeze all
    // values in this cache; received shots never inspect the catalogue/rig.
    var speed=ballistics.MaxBulletSpeed;var startWidth=trail.startWidth;var endWidth=trail.endWidth;var lifetime=trail.time;
    if(RemoteTracerPolicy.ValidAsset(speed,startWidth,endWidth,lifetime)){
     result.MovingTracer=true;result.TracerSpeed=speed;result.TracerStartWidth=startWidth;result.TracerEndWidth=endWidth;result.TracerLifetime=lifetime;
    }
   }
  }
  var muzzle=weapon.fireParticlesEffect;if(!muzzle&&nativeMuzzle)muzzle=nativeMuzzle;
  if(muzzle){
   result.MuzzleParticles=muzzle.GetComponentInChildren<ParticleSystem>(true);
   if(VisualOnly(muzzle,true)){result.MuzzlePrefab=muzzle;result.MuzzlePrefabSafe=true;}
   else foreach(var particles in muzzle.GetComponentsInChildren<ParticleSystem>(true))
    if(particles&&VisualOnly(particles.gameObject,true)){result.MuzzlePrefab=particles.gameObject;result.MuzzlePrefabSafe=true;break;}
  }
  result.AudioTemplate=FindAudio(weapon.fireSound)??(nativeFireAudio?nativeFireAudio:null);
  var entity=weapon.GetComponent<FPSAnimatorEntity>();
  if(entity)result.AimSource=entity.defaultAimPoint;
  result.GrenadeAnimation=weapon.grenadeClip;
  // Exact native fields: several asset names say Empty even in reloadClip.
  result.ReloadAnimation=weapon.reloadClip;result.ReloadEmptyAnimation=weapon.reloadEmptyClip;
  var profile=entity?entity.animatorProfile:null;
  if(profile&&profile.settings!=null)foreach(var settings in profile.settings){
   if(!settings)continue;
   result.Pose??=settings.TryCast<PoseSamplerLayerSettings>();
   result.Ik??=settings.TryCast<IkLayerSettings>();
   result.Ads??=settings.TryCast<AdsLayerSettings>();
  }
  // Incomplete native catalogue entries can acquire their prefab, material or
  // clips later. Retry that one ID at most once per two seconds; ready entries
  // retain their source objects without per-frame rebuilding/searching.
  cache[id]=(result,Complete(result)?float.PositiveInfinity:Time.unscaledTime+2);return result;
 }
 // Lookup only; no catalogue/hierarchy search on a received-shot hot path.
 internal static bool TryCached(int id,out Asset? value){
  value=null;if(!cache.TryGetValue(id,out var ready)||ready.Value==null||!ready.Value.VisualSource||!ready.Value.Weapon||Time.unscaledTime>=ready.RetryAt)return false;
  if(ready.RetryAt==float.PositiveInfinity&&!Complete(ready.Value))return false;
  value=ready.Value;return true;
 }
 // Keep negative/incomplete cache backoff when pending events request assets.
 // Callers charge the single frame resolution budget only for work due now.
 internal static bool NeedsResolve(int id){
  if(id<0||id>100000||!catalogue||catalogue!.Weapons==null)return false;
  return !(cache.TryGetValue(id,out var ready)&&Time.unscaledTime<ready.RetryAt&&
   (ready.Value==null||ready.Value.VisualSource&&ready.Value.Weapon&&
    (ready.RetryAt!=float.PositiveInfinity||Complete(ready.Value))));
 }
 static bool Complete(Asset value)=>value.VisualSource&&value.Weapon&&(value.MuzzlePrefab||value.MuzzleParticles)&&value.AudioTemplate&&value.Pose&&value.Ik&&value.FireClips.Length>0;
 static AudioSource? FindAudio(FireSoundController? sound){
  if(!sound)return null;
  if(sound!.sources!=null)foreach(var audio in sound.sources)if(audio)return audio;
  if(sound.SoundParent)foreach(var audio in sound.SoundParent.GetComponentsInChildren<AudioSource>(true))if(audio)return audio;
  return null;
 }
 internal static bool VisualOnly(GameObject value,bool requireParticles){
  var nodes=value.GetComponentsInChildren<Transform>(true);if(nodes.Length>64)return false;var particles=0;
  foreach(var node in nodes)foreach(var component in node.GetComponents<Component>()){
   if(!component)return false;
   if(component.TryCast<ParticleSystem>()!=null){if(++particles>16)return false;continue;}
   if(component.TryCast<Transform>()!=null||component.TryCast<ParticleSystemRenderer>()!=null||component.TryCast<Light>()!=null||
      component.TryCast<MeshRenderer>()!=null||component.TryCast<MeshFilter>()!=null||component.TryCast<LineRenderer>()!=null||component.TryCast<TrailRenderer>()!=null)continue;
   return false;
  }return !requireParticles||particles>0;
 }
 internal static void Reset(){ActiveWeapon=null;ActiveBallistics=null;HeldWeaponId=-1;catalogue=null;inventory=null;retryAt=0;nativeFireAudio=null;audioOwner=IntPtr.Zero;audioRetry=0;nativeMuzzle=null;muzzleRetry=0;cache.Clear();}
}

public sealed partial class Together
{
 internal int CurrentHeldWeaponId()=>RemoteShotAssets.HeldWeaponId;
}
