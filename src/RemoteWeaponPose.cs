using KINEMATION.KAnimationCore.Runtime.Rig;
using KINEMATION.FPSAnimationFramework.Runtime.Playables;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

// An isolated sampler owns Transforms and one disabled Avatar-only Animator.
// Native clips never sample a player, shared rig, camera or gameplay object.
sealed class RemoteWeaponPose
{
 readonly Transform owner,spine,weapon,sampleRoot,sampleSpine,sampleWeapon;
 readonly Transform? adsTarget;readonly FPSAnimationAsset? grenade,reload,reloadEmpty;
 readonly Vector3 weaponPosition,sightPosition;readonly Quaternion weaponRotation,sightRotation;
 readonly Quaternion authoredSpineFrame;
 readonly float pistolHipAdvance;readonly string pistolHipSource;
 static readonly string[] CanonicalHeadPath={"Head","Neck","UpperChest","Chest","Spine","Hips","Skeleton"};
 readonly Arm right,left;readonly Finger[] fingers;readonly float aimingSpeed;
 readonly SamplePose[] sampleBaseline;readonly RemoteFireMotion? fire;
 float aimWeight,pistolAdvanceBlend,lastPistolAdvance;bool sampleDirty;
 int previousReloadKind;float previousReloadElapsed,previousReloadWeight,reloadFadeStarted,reloadFadeSample,reloadFadeWeight;FPSAnimationAsset? reloadFadeAsset;
 readonly struct SamplePose {
  readonly Transform bone;readonly Vector3 position,scale;readonly Quaternion rotation;
  internal SamplePose(Transform value){bone=value;position=value.localPosition;rotation=value.localRotation;scale=value.localScale;}
  internal void Restore(){if(bone){bone.localPosition=position;bone.localRotation=rotation;bone.localScale=scale;}}
 }
 readonly struct Finger {
  readonly Transform bone,sample;readonly Quaternion rotation;
  internal Finger(Transform value,Transform sampled){bone=value;sample=sampled;rotation=sample.localRotation;}
  internal void Apply(float weight){if(bone)bone.localRotation=weight>0&&sample?Quaternion.Slerp(rotation,sample.localRotation,weight):rotation;}
 }
 sealed class Arm {
  readonly Transform root,mid,tip,sampleTip,sampleMid;
  readonly Vector3 target,hint;readonly Quaternion rotation;
  internal Arm(Transform hand,Transform sampled,Transform sampledWeapon){
   tip=hand;mid=hand.parent;root=mid.parent;sampleTip=sampled;sampleMid=sampled.parent;
   target=sampledWeapon.InverseTransformPoint(sampleTip.position);hint=sampledWeapon.InverseTransformPoint(sampleMid.position);
   rotation=Quaternion.Inverse(sampledWeapon.rotation)*sampleTip.rotation;
  }
  Vector3 LocalTarget(Transform sampledWeapon,float weight)=>weight>0?Vector3.Lerp(target,sampledWeapon.InverseTransformPoint(sampleTip.position),weight):target;
  internal float MaximumAdvance(Vector3 position,Quaternion rotation,Vector3 scale,Transform sampledWeapon,float weight,Vector3 direction,float requested){
   if(!root||!mid||!tip||!sampleTip||!sampledWeapon||!Finite(position)||!Finite(rotation)||!Finite(scale)||scale.x<=0||scale.y<=0||scale.z<=0||!Finite(direction)||!float.IsFinite(weight)||weight<0||weight>1||!float.IsFinite(requested)||requested<=0)return 0;
   var directionLength=direction.sqrMagnitude;if(!float.IsFinite(directionLength)||Mathf.Abs(directionLength-1)>.001f)return 0;
   // The exact same blended local hand target is consumed by Apply below.
   var localTarget=LocalTarget(sampledWeapon,weight);if(!Finite(localTarget))return 0;
   var delta=position+rotation*Vector3.Scale(localTarget,scale)-root.position;
   var upper=(mid.position-root.position).magnitude;var lower=(tip.position-mid.position).magnitude;
   if(!Finite(delta)||!float.IsFinite(upper)||!float.IsFinite(lower)||upper<.001f||lower<.001f)return 0;
   var minimum=Mathf.Abs(upper-lower)+.0001f;var maximum=upper+lower-.0001f;var distance=delta.sqrMagnitude;
   if(!float.IsFinite(distance)||maximum<=minimum||distance<minimum*minimum||distance>maximum*maximum)return 0;
   var along=Vector3.Dot(delta,direction);if(!float.IsFinite(along)||along<0)return 0;
   // Advancing only away from the shoulder also preserves minimum reach.
   var discriminant=along*along+maximum*maximum-distance;
   if(!float.IsFinite(discriminant)||discriminant<0)return 0;
   var limit=-along+Mathf.Sqrt(discriminant);return float.IsFinite(limit)?Mathf.Min(requested,Mathf.Max(0,limit)):0;
  }
  internal void Apply(Transform weapon,Transform sampledWeapon,float weight){
   if(!root||!mid||!tip)return;
   var localTarget=LocalTarget(sampledWeapon,weight);
   var localHint=weight>0?Vector3.Lerp(hint,sampledWeapon.InverseTransformPoint(sampleMid.position),weight):hint;
   var localRotation=weight>0?Quaternion.Slerp(rotation,Quaternion.Inverse(sampledWeapon.rotation)*sampleTip.rotation,weight):rotation;
   var destination=weapon.TransformPoint(localTarget);var elbowHint=weapon.TransformPoint(localHint);var origin=root.position;
   var upper=(mid.position-origin).magnitude;var lower=(tip.position-mid.position).magnitude;
   var delta=destination-origin;var distance=delta.magnitude;if(upper<.001f||lower<.001f||distance<.001f)return;
   var direction=delta/distance;distance=Mathf.Clamp(distance,Mathf.Abs(upper-lower)+.0001f,upper+lower-.0001f);
   var along=(upper*upper-lower*lower+distance*distance)/(2*distance);
   var bend=elbowHint-origin;var normal=bend-direction*Vector3.Dot(bend,direction);
   if(normal.sqrMagnitude<.000001f){bend=mid.position-origin;normal=bend-direction*Vector3.Dot(bend,direction);}
   if(normal.sqrMagnitude<.000001f)return;
   var elbow=origin+direction*along+normal.normalized*Mathf.Sqrt(Mathf.Max(0,upper*upper-along*along));
   root.rotation=Quaternion.FromToRotation(mid.position-origin,elbow-origin)*root.rotation;
   mid.rotation=Quaternion.FromToRotation(tip.position-mid.position,destination-mid.position)*mid.rotation;
   tip.rotation=weapon.rotation*localRotation;
  }
  internal float Error(Transform weapon)=>tip?Vector3.Distance(tip.position,weapon.TransformPoint(target)):-1;
  internal float Reach=>root&&mid&&tip?Vector3.Distance(root.position,mid.position)+Vector3.Distance(mid.position,tip.position):0;
 }
 RemoteWeaponPose(Transform body,Transform pivot,Transform mount,Transform sampled,Transform sampledSpine,Transform sampledMount,
  Transform? aimingTarget,Transform? sight,RemoteShotAssets.Asset asset,Arm rightArm,Arm leftArm,Finger[] fingerPose,SamplePose[] baseline,int weaponId,float authoredHipAdvance,string hipSource){
  owner=body;spine=pivot;weapon=mount;sampleRoot=sampled;sampleSpine=sampledSpine;sampleWeapon=sampledMount;
  // Native PoseSampler caches component and spine poses separately. A prone
  // locomotion spine can rotate~90deg; that rotation must not turn the authored
  // forward gun grip down through the floor. Keep its sampled orientation in
  // component space while following the LIVE spine's position (no height offset).
  authoredSpineFrame=Quaternion.Inverse(sampleRoot.rotation)*sampleSpine.rotation;
  right=rightArm;left=leftArm;fingers=fingerPose;adsTarget=sight?aimingTarget:null;grenade=asset.GrenadeAnimation;
  // Pinned reload assets are non-additive with no masks and the same authored
  // KRig as the grip. Unknown/additive/masked profiles fail closed, no guessed
  // retargeting or sampling of a live player/native gameplay weapon.
  FPSAnimationAsset? ReloadAsset(FPSAnimationAsset? value){
   var basis=asset.Pose?asset.Pose!.poseToSample:null;if(!value||!value!.clip||!value.rigAsset||!basis||!basis!.rigAsset||value.rigAsset.Pointer!=basis.rigAsset.Pointer||value.isAdditive||value.mask||value.overrideMask)return null;
   var b=value.blendTime;return float.IsFinite(value.clip.length)&&value.clip.length>0&&value.clip.length<=ReloadPresentationPolicy.MaxElapsed&&float.IsFinite(b.blendInTime)&&b.blendInTime>=0&&float.IsFinite(b.blendOutTime)&&b.blendOutTime>=0&&float.IsFinite(b.rateScale)&&b.rateScale>0&&float.IsFinite(b.startTime)&&b.startTime>=0&&float.IsFinite(b.endTime)&&b.endTime>=0?value:null;
  }
  reload=ReloadAsset(asset.ReloadAnimation);reloadEmpty=ReloadAsset(asset.ReloadEmptyAnimation);
  sampleBaseline=baseline;
  try{fire=RemoteFireMotion.Create(asset,weaponId);}catch(Exception error){GameplayLog.Fault("avatar.fire_motion",error);}
  aimingSpeed=asset.Ads?Mathf.Max(.01f,asset.Ads!.aimingSpeed):10;
  pistolHipAdvance=float.IsFinite(authoredHipAdvance)&&authoredHipAdvance>0?authoredHipAdvance:0;
  pistolHipSource=hipSource;
  weaponPosition=sampleSpine.InverseTransformPoint(sampleWeapon.position);weaponRotation=Quaternion.Inverse(sampleSpine.rotation)*sampleWeapon.rotation;
  // The copied sight includes the prefab root's authored scale and transforms.
  sightPosition=sight?mount.InverseTransformPoint(sight!.position):Vector3.zero;
  sightRotation=sight?Quaternion.Inverse(mount.rotation)*sight!.rotation:Quaternion.identity;
 }
 internal static RemoteWeaponPose? Create(Transform body,Transform mount,Transform? sight,IReadOnlyDictionary<int,Transform> rig,RemoteShotAssets.Asset asset,int weaponId,out string status){
  status="missing_profile";
  var pose=asset.Pose;var ik=asset.Ik;var animation=pose?pose!.poseToSample:null;var clip=animation?animation!.clip:null;
  if(!pose||!ik||!clip)return null;
  status="missing_avatar";
  var sourceAnimator=body.GetComponent<Animator>();var sourceAvatar=sourceAnimator?sourceAnimator!.avatar:null;
  if(!sourceAvatar||!sourceAvatar!.isValid)return null;
  Transform? Bone(KRigElement element){
   if(rig.TryGetValue(element.index,out var bone)&&bone)return bone;
   // Native KRigComponent.GetRigTransform falls back to its name map when
   // an authored index is negative/outside the hierarchy (208894C).
   foreach(var candidate in rig.Values)if(candidate&&candidate.name==element.name)return candidate;return null;
  }
  var pivot=Bone(pose!.spineRoot);var handRight=Bone(ik!.rightHand);var handLeft=Bone(ik.leftHand);var authoredMount=Bone(pose.weaponBone);
  status="missing_bone";
  if(!pivot||!handRight||!handLeft||!handRight!.parent||!handRight.parent.parent||!handLeft!.parent||!handLeft.parent.parent||!authoredMount)return null;
  // The controller equips under IK WeaponBone (index -1/name lookup -> 48),
  // whereas this profile samples WeaponBone (84). They are deliberately
  // distinct: the native PoseSampler drives the former from the latter.
  var required=new HashSet<IntPtr>();
  foreach(var bone in rig.Values){var value=bone;while(value&&value.Pointer!=body.Pointer){if(!required.Add(value.Pointer))break;value=value.parent;}}
  status="rig_limit";if(required.Count>1024)return null;
  var map=new Dictionary<IntPtr,Transform>();GameObject? sampler=null;
  try{
   Transform Copy(Transform original,Transform? parent){
    var go=new GameObject(original.name);if(parent)go.transform.SetParent(parent,false);
    go.transform.localPosition=original.localPosition;go.transform.localRotation=original.localRotation;go.transform.localScale=original.localScale;map[original.Pointer]=go.transform;
    if(sampler==null){sampler=go;go.SetActive(false);}
    for(var i=0;i<original.childCount;i++){var child=original.GetChild(i);if(required.Contains(child.Pointer))Copy(child,go.transform);}return go.transform;
   }
   var sampled=Copy(body,body.parent);var samplePivot=map[pivot!.Pointer];var sampleMount=map[authoredMount!.Pointer];
   // Native A/B: this nonlegacy Human clip binds only with the exact Avatar
   // Animator on an active private root. No controller, events or update pump.
   var sampleAnimator=sampled.gameObject.AddComponent<Animator>();sampleAnimator.enabled=false;
   sampleAnimator.avatar=sourceAvatar;sampleAnimator.fireEvents=false;sampleAnimator.applyRootMotion=false;sampleAnimator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
   sampled.gameObject.SetActive(true);
   // Native SamplePose E409AE/E40A68 neutralizes the pelvis parent's local
   // rotation when it differs from the animator component root before E40D8F
   // samples the authored clip. Do this only on
   // our private sampler, so creating/swapping a gun while prone
   // cannot bake the locomotion root rotation into the reference grip.
   var pelvis=Bone(pose.pelvis);if(pelvis&&pelvis!.parent&&pelvis.parent.Pointer!=body.Pointer&&map.TryGetValue(pelvis.parent.Pointer,out var rootParent))rootParent.localRotation=Quaternion.identity;
   // Same exact pinned native sampling call as PoseSampler.SamplePose E40D8F,
   // on <=1024 private nodes with matching animation paths and exact Avatar.
   clip!.SampleAnimation(sampled.gameObject,0);
   if(pose.overwriteWeaponBone){sampleMount.position=sampled.TransformPoint(pose.defaultWeaponPose.position);sampleMount.rotation=sampled.rotation*pose.defaultWeaponPose.rotation;}
   var fingerPose=new List<Finger>();var rigIds=new HashSet<IntPtr>();foreach(var bone in rig.Values)if(bone)rigIds.Add(bone.Pointer);
   foreach(var hand in new[]{handRight!,handLeft!})foreach(var finger in hand.GetComponentsInChildren<Transform>(true))
    if(finger.Pointer!=hand.Pointer&&rigIds.Contains(finger.Pointer)&&map.TryGetValue(finger.Pointer,out var fingerSample))fingerPose.Add(new Finger(finger,fingerSample));
   var aimTarget=asset.Ads?Bone(asset.Ads!.aimTargetBone):null;
   var rightArm=new Arm(handRight!,map[handRight!.Pointer],sampleMount);var leftArm=new Arm(handLeft!,map[handLeft!.Pointer],sampleMount);
   var hipAdvance=CanonicalPistolAdvance(sampled,sampleMount,mount,aimTarget,sight,map,asset,Mathf.Min(rightArm.Reach,leftArm.Reach),out var hipSource);
   var baseline=new SamplePose[map.Count];var sampleIndex=0;foreach(var value in map.Values)baseline[sampleIndex++]=new SamplePose(value);
   status="ready";
   return new RemoteWeaponPose(body,pivot,mount,sampled,samplePivot,sampleMount,aimTarget,sight,asset,
    rightArm,leftArm,fingerPose.ToArray(),baseline,weaponId,hipAdvance,hipSource);
  }catch{if(sampler)Object.Destroy(sampler);throw;}
 }
 static float CanonicalPistolAdvance(Transform sampled,Transform sampleMount,Transform mount,Transform? aimTarget,Transform? sight,IReadOnlyDictionary<IntPtr,Transform> map,RemoteShotAssets.Asset asset,float reach,out string source){
  source="unsupported_build";
  if(Plugin.Instance?.Build?.StartsWith("E1247B9A7FC5A9B80C223F65D779FF8C0D755353B02E5A8C1E9BAAC4A7C3312B:",StringComparison.Ordinal)!=true)return 0;
  source="non_pistol_profile";
  var animation=asset.Pose?asset.Pose!.poseToSample:null;var controller=asset.Weapon?asset.Weapon!.overrideController:null;
  // This is the authored animator family, including TT/Deagle/micro-UZI;
  // semantic weapon IDs/categories are not a reliable pistol-profile gate.
  if(!controller||controller!.name!="FPSAnimator_Pistol_Generic"||!animation||animation!.name!="AA_PistolPose"||!animation.clip||animation.clip.name!="C_Mk23Mod0_BasePose")return 0;
  source="missing_canonical_anchor";
  if(!asset.Ads||!aimTarget||!sight||asset.Ads!.aimTargetBone.isVirtual||asset.Ads.aimTargetBone.name!=aimTarget!.name||!map.TryGetValue(aimTarget.Pointer,out var socket)||!socket)return 0;
  // Resolve the actual ADS KRigElement first. Never assume numeric45/46.
  var camera=socket.parent;var head=camera?camera.parent:null;
  if(socket.name!="CameraSocket"||!camera||camera!.name!="Camera"||!head)return 0;
  var ancestor=head;foreach(var name in CanonicalHeadPath){if(!ancestor||ancestor!.name!=name)return 0;ancestor=ancestor.parent;}
  if(!ancestor||ancestor!.Pointer!=sampled.Pointer)return 0;
  source="unsupported_scale";
  if(!UnitScale(sampled.lossyScale)||!UnitScale(sampleMount.lossyScale)||!UnitScale(mount.lossyScale))return 0;
  var cameraBefore=new SamplePose(camera);var socketBefore=new SamplePose(socket);
  // E124 level1 saved authored transforms32102/31516. Only this PRIVATE
  // sampler is changed; live Camera/Socket and the existing ADS endpoint stay
  // untouched. These unbound leaves are not copied from the current stance.
  camera.localPosition=new Vector3(.007499999832361937f,.11599999666213989f,.024000000208616257f);
  camera.localRotation=new Quaternion(-.7071067690849304f,-1.3854672278683887e-14f,1.218217184301238e-15f,.7071068286895752f);camera.localScale=Vector3.one;
  socket.localPosition=Vector3.zero;socket.localRotation=Quaternion.identity;socket.localScale=Vector3.one;
  var sightPosition=mount.InverseTransformPoint(sight!.position);var sightRotation=Quaternion.Inverse(mount.rotation)*sight.rotation;
  var adsRotation=sampled.rotation*Quaternion.Inverse(sightRotation);
  var adsPosition=socket.position-adsRotation*Vector3.Scale(sightPosition,mount.lossyScale);
  var advance=Vector3.Dot(adsPosition-sampleMount.position,sampled.forward);
  source="invalid_canonical_distance";
  if(!Finite(sightPosition)||!Finite(sightRotation)||!Finite(adsPosition)||!float.IsFinite(advance)||!float.IsFinite(reach)||advance<=0||reach<=0||advance>reach){cameraBefore.Restore();socketBefore.Restore();return 0;}
  source="native_pistol_ads_forward";return advance;
 }
 internal void Apply(float pitch,int aimState,float throwElapsed,int reloadKind=0,float reloadElapsed=0){
  if(!owner||!spine||!weapon||!sampleRoot)return;
  // Clip sampling can omit bindings. Restore the private authored grip before
  // every throw sample and once when it ends; no prior clip can leak into it.
  if(sampleDirty){foreach(var value in sampleBaseline)value.Restore();sampleDirty=false;}
  bool SampleAction(FPSAnimationAsset? action,float elapsed){
   var clip=action?action!.clip:null;if(!clip||elapsed<0||!float.IsFinite(elapsed))return false;
   var blend=action!.blendTime;var rate=float.IsFinite(blend.rateScale)&&blend.rateScale>0?blend.rateScale:1;
   var start=Mathf.Clamp(blend.startTime,0,clip!.length);var finish=blend.endTime>start?Mathf.Min(blend.endTime,clip.length):clip.length;var time=start+elapsed*rate;
   if(time>finish)return false;clip.SampleAnimation(sampleRoot.gameObject,time);sampleDirty=true;return true;
  }
  var throwing=SampleAction(grenade,throwElapsed);var actionWeight=throwing?1f:0f;var reloading=reloadKind is 1 or 2;var action=reloading?(reloadKind==2?reloadEmpty:reload):null;
  if(throwing){ResetReload();}
  else if(reloading){
   reloadFadeAsset=null;previousReloadKind=reloadKind;previousReloadElapsed=reloadElapsed;previousReloadWeight=0;
   if(SampleAction(action,reloadElapsed)){
    var blend=action!.blendTime;var rate=blend.rateScale>0?blend.rateScale:1;var clip=action.clip;var start=Mathf.Clamp(blend.startTime,0,clip.length);var finish=blend.endTime>start?Mathf.Min(blend.endTime,clip.length):clip.length;
    actionWeight=previousReloadWeight=ReloadPresentationPolicy.BlendWeight(reloadElapsed,Mathf.Max(0,(finish-start)/rate-reloadElapsed),blend.blendInTime,blend.blendOutTime);
   }
  }else{
   if(previousReloadKind!=0){reloadFadeAsset=previousReloadKind==2?reloadEmpty:reload;reloadFadeSample=previousReloadElapsed;reloadFadeWeight=previousReloadWeight;reloadFadeStarted=Time.unscaledTime;previousReloadKind=0;}
   if(reloadFadeAsset){var elapsed=Mathf.Max(0,Time.unscaledTime-reloadFadeStarted);var duration=reloadFadeAsset!.blendTime.blendOutTime;
    if(duration>0&&elapsed<duration&&SampleAction(reloadFadeAsset,reloadFadeSample+elapsed))actionWeight=reloadFadeWeight*ReloadPresentationPolicy.BlendWeight(1,duration-elapsed,0,duration);else reloadFadeAsset=null;
   }
  }
  var animated=actionWeight>0;
  var localPosition=animated?Vector3.Lerp(weaponPosition,sampleSpine.InverseTransformPoint(sampleWeapon.position),actionWeight):weaponPosition;
  var localRotation=animated?Quaternion.Slerp(weaponRotation,Quaternion.Inverse(sampleSpine.rotation)*sampleWeapon.rotation,actionWeight):weaponRotation;
  var aim=Quaternion.AngleAxis(Mathf.DeltaAngle(0,pitch),owner.right);
  var basis=owner.rotation*authoredSpineFrame;
  var position=spine.position+aim*(basis*Vector3.Scale(localPosition,spine.lossyScale));var rotation=aim*basis*localRotation;
  var desiredAim=!throwing&&!reloading&&aimState>=2&&adsTarget?1f:0f;aimWeight=Mathf.Lerp(aimWeight,desiredAim,1-Mathf.Exp(-aimingSpeed*Time.unscaledDeltaTime));
  if(adsTarget&&aimWeight>.001f){
   // Aim through the weapon's actual defaultAimPoint at its authored Camera/
   // CameraSocket rig target, facing the sender's yaw/pitch. No camera writes.
   var targetPosition=spine.position+aim*(adsTarget!.position-spine.position);
   var targetRotation=aim*owner.rotation*Quaternion.Inverse(sightRotation);
   position=Vector3.Lerp(position,targetPosition-targetRotation*sightPosition,aimWeight);rotation=Quaternion.Slerp(rotation,targetRotation,aimWeight);
  }
  if(throwing||reloading)fire?.Reset();else fire?.Apply(ref position,ref rotation,aimWeight);
  lastPistolAdvance=0;
  if(pistolHipAdvance>0){
   // Readiness fades using the native aiming speed. The active action retains
   // its exact blended hand targets while this additional translation fades.
   var response=1-Mathf.Exp(-aimingSpeed*Time.unscaledDeltaTime);
   pistolAdvanceBlend=Mathf.Lerp(pistolAdvanceBlend,throwing||reloading?0:1,response);
   var requested=pistolHipAdvance*pistolAdvanceBlend*(1-Mathf.Clamp01(aimWeight))*(1-Mathf.Clamp01(actionWeight));
   var direction=aim*owner.forward;var length=direction.sqrMagnitude;
   if(float.IsFinite(requested)&&requested>0&&float.IsFinite(length)&&length>.000001f){
    direction/=Mathf.Sqrt(length);var scale=weapon.lossyScale;
    if(UnitScale(scale)){
     var advance=Mathf.Min(right.MaximumAdvance(position,rotation,scale,sampleWeapon,actionWeight,direction,requested),left.MaximumAdvance(position,rotation,scale,sampleWeapon,actionWeight,direction,requested));
     if(advance>0){position+=direction*advance;lastPistolAdvance=advance;}
    }
   }
  }
  weapon.position=position;weapon.rotation=rotation;
  right.Apply(weapon,sampleWeapon,actionWeight);left.Apply(weapon,sampleWeapon,actionWeight);foreach(var finger in fingers)finger.Apply(actionWeight);
 }
 internal bool NotifyShot(long sequence,int aimState)=>fire?.Notify(sequence,aimState)==true;
 static bool Finite(Vector3 value)=>float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z);
 static bool Finite(Quaternion value)=>float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z)&&float.IsFinite(value.w);
 static bool UnitScale(Vector3 value)=>Finite(value)&&Mathf.Abs(value.x-1)<.001f&&Mathf.Abs(value.y-1)<.001f&&Mathf.Abs(value.z-1)<.001f;
 internal void ResetShot()=>fire?.Reset();
 internal void ResetReload(){previousReloadKind=0;previousReloadElapsed=previousReloadWeight=0;reloadFadeAsset=null;reloadFadeStarted=reloadFadeSample=reloadFadeWeight=0;}
 internal void Dispose(){if(sampleRoot)Object.Destroy(sampleRoot.gameObject);}
 internal object Diagnostics(int weaponId)=>new{weaponId,equipped=weapon.name,authored=sampleWeapon.name,spine=spine.name,rightError=right.Error(weapon),leftError=left.Error(weapon),rightReach=right.Reach,leftReach=left.Reach,position=new[]{weaponPosition.x,weaponPosition.y,weaponPosition.z},authoredSpineFrame=new[]{authoredSpineFrame.x,authoredSpineFrame.y,authoredSpineFrame.z,authoredSpineFrame.w},fingers=fingers.Length,reloadSupported=(bool)reload,reloadEmptySupported=(bool)reloadEmpty,reloadClip=reload?reload!.clip.name:null,reloadEmptyClip=reloadEmpty?reloadEmpty!.clip.name:null,pistolHipSource,pistolHipAdvance,lastPistolAdvance};
}
