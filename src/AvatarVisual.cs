using UnityEngine;
using UnityEngine.Rendering;
using KINEMATION.KAnimationCore.Runtime.Rig;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Deliberately copies only a player's transforms, renderer data and Animator.
// No native gameplay, outfit persistence, AI, loot or weapon scripts are created.
sealed class AvatarVisual
{
 public GameObject Root=null!;public Animator? Animator;
 readonly Dictionary<string,List<GameObject>> clothing=new(StringComparer.Ordinal);
 OutfitEntry[]? priorOutfit;bool playerLocomotion;float nextAnimatorRetry,weaponRetry;
 RuntimeAnimatorController? boundController,unarmedController,armedController;Transform? weaponMount,shotMuzzle;GameObject? heldVisual;int heldWeaponId=int.MinValue,reportedWeaponId=int.MinValue;
 Transform? bodyRoot,weaponSight;readonly Dictionary<int,Transform> rigBones=new();RemoteWeaponPose? weaponPose;float poseRetry;int reportedPoseId=int.MinValue;
 string reportedPoseStatus="";int poseDiagnostics;bool gripReported;
 long lastThrowSequence,lastShotSequence;bool throwBaseline;float throwStarted=-1;Vector3 lastMoveDirection=Vector3.forward;
 int shotDiagnostics,mechanicsDiagnostics;WeaponMechanics? weaponMechanics;
 readonly ReloadPresentationPolicy reloadPresentation=new();int reloadDiagnostics;
 readonly StanceTransitions stanceTransitions=new();
 static readonly int[] LocomotionStances={UnityEngine.Animator.StringToHash("Locomotion.Standing"),UnityEngine.Animator.StringToHash("Locomotion.Crouching"),UnityEngine.Animator.StringToHash("Locomotion.Prone")};
 static readonly int FullBodyWeight=UnityEngine.Animator.StringToHash("FullBodyWeight"),CrouchWeight=UnityEngine.Animator.StringToHash("CrouchWeight"),ProneWeight=UnityEngine.Animator.StringToHash("ProneWeight"),OverlayType=UnityEngine.Animator.StringToHash("OverlayType"),MoveX=UnityEngine.Animator.StringToHash("MoveX"),MoveY=UnityEngine.Animator.StringToHash("MoveY"),Velocity=UnityEngine.Animator.StringToHash("Velocity"),Sprinting=UnityEngine.Animator.StringToHash("Sprinting"),SprintPoseWeight=UnityEngine.Animator.StringToHash("SprintPoseWeight"),Moving=UnityEngine.Animator.StringToHash("Moving"),Crouching=UnityEngine.Animator.StringToHash("Crouching"),Proning=UnityEngine.Animator.StringToHash("Proning"),InAir=UnityEngine.Animator.StringToHash("InAir");
 public string SourceName="";
 public static AvatarVisual? Create(Transform parent,OutfitEntry[] outfit){
  var source=AvatarAppearance.Source();if(!source)return null;
  var result=new AvatarVisual{SourceName=source!.name};var map=new Dictionary<IntPtr,Transform>();var rendererMap=new Dictionary<IntPtr,Renderer>();var skinned=0;
  var holder=new GameObject("CoopCharacterVisual");holder.SetActive(false);holder.transform.SetParent(parent,false);result.Root=holder;
  try{
   var sourceRoot=source.transform;var excluded=new Dictionary<IntPtr,bool>();
   bool FirstPersonOnly(Transform value){
    if(!value||value.Pointer==sourceRoot.Pointer)return false;
    if(excluded.TryGetValue(value.Pointer,out var known))return known;
    var blocked=(bool)value.GetComponent<Camera>()||value.GetComponent<Demo.Scripts.Runtime.Item.FPSItem>()||value.GetComponent<Ballistics.Weapon>()||FirstPersonOnly(value.parent);
    excluded[value.Pointer]=blocked;return blocked;
   }
   var rendererCandidates=new List<Renderer>();var required=new HashSet<IntPtr>();
   void Keep(Transform? value){while(value&&value!.Pointer!=sourceRoot.Pointer){if(!required.Add(value.Pointer))break;value=value.parent;}}
   foreach(var renderer in source.GetComponentsInChildren<Renderer>(true)){
    if(FirstPersonOnly(renderer.transform)||renderer.TryCast<MeshRenderer>()==null&&renderer.TryCast<SkinnedMeshRenderer>()==null)continue;rendererCandidates.Add(renderer);Keep(renderer.transform);
    if(renderer.TryCast<SkinnedMeshRenderer>() is {} skin){Keep(skin.rootBone);foreach(var bone in skin.bones)Keep(bone);}
   }
   var controller=source.GetComponent<Demo.Scripts.Runtime.Character.FPSController>();var nativeMount=controller?controller!._weaponBone:null;Keep(nativeMount);
   var nativeRig=source.GetComponentInChildren<KRigComponent>(true);var rigHierarchy=nativeRig?nativeRig!.hierarchy:null;
   if(rigHierarchy!=null)for(var i=0;i<rigHierarchy.Count&&i<512;i++)Keep(rigHierarchy[i]);
   var native=source.GetComponent<Animator>()??source.GetComponentInChildren<Animator>(true);if(native)Keep(native.transform);
   Transform Copy(Transform original,Transform owner,bool top=false){
    var go=new GameObject(original.name);go.transform.SetParent(owner,false);
    go.transform.localPosition=top?Vector3.zero:original.localPosition;go.transform.localRotation=top?Quaternion.identity:original.localRotation;go.transform.localScale=top?original.lossyScale:original.localScale;
    map[original.Pointer]=go.transform;
    for(var i=0;i<original.childCount;i++){
     var child=original.GetChild(i);
     // Do not manufacture thousands of empty view-model/effect nodes. Keep
     // every accepted skin's bone ancestors and the authored weapon anchor.
     if(!FirstPersonOnly(child)||required.Contains(child.Pointer))Copy(child,go.transform);
    }
    go.SetActive(top||original.gameObject.activeSelf);return go.transform;
   }
   result.bodyRoot=Copy(sourceRoot,holder.transform,true);
   if(rigHierarchy!=null)for(var i=0;i<rigHierarchy.Count&&i<512;i++){var bone=rigHierarchy[i];if(bone&&map.TryGetValue(bone.Pointer,out var mapped))result.rigBones[i]=mapped;}
   if(nativeMount&&map.TryGetValue(nativeMount!.Pointer,out var mount))result.weaponMount=mount;
   if(controller&&controller!.settings)result.unarmedController=controller.settings.unarmedController;
   // An absent FPSItem override means the base rifle controller, not the
   // unarmed override's Magic pose. Unwrap only cached authored assets.
   var baseController=result.unarmedController?result.unarmedController:native?native!.runtimeAnimatorController:null;
   for(var i=0;i<4&&baseController;i++){
    var replace=baseController!.TryCast<AnimatorOverrideController>();if(!replace||!replace!.runtimeAnimatorController)break;baseController=replace.runtimeAnimatorController;
   }
   result.armedController=baseController;
   foreach(var item in source.GetComponentsInChildren<ClothingObject>(true)){
    if(string.IsNullOrEmpty(item.Name)||!map.TryGetValue(item.transform.Pointer,out var target))continue;
    if(!result.clothing.TryGetValue(item.Name,out var objects))result.clothing[item.Name]=objects=new();objects.Add(target.gameObject);
   }
   foreach(var original in rendererCandidates){
    if(!map.TryGetValue(original.transform.Pointer,out var target))continue;
    Renderer? copy=null;
    if(original.TryCast<SkinnedMeshRenderer>() is { } skin&&skin){
     if(!skin.sharedMesh)continue;var sourceBones=skin.bones;var bones=new Transform[sourceBones.Length];var valid=true;
     for(var i=0;i<bones.Length;i++){var bone=sourceBones[i];if(!bone||!map.TryGetValue(bone.Pointer,out var mapped)){valid=false;break;}bones[i]=mapped;}
     if(!valid)continue;
     var renderer=target.gameObject.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=skin.sharedMesh;renderer.bones=bones;renderer.localBounds=skin.localBounds;
     if(skin.rootBone&&map.TryGetValue(skin.rootBone.Pointer,out var root))renderer.rootBone=root;
     copy=renderer;skinned++;
    }else if(original.TryCast<MeshRenderer>() is { } mesh&&mesh){
     var filter=original.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)continue;
     target.gameObject.AddComponent<MeshFilter>().sharedMesh=filter.sharedMesh;copy=target.gameObject.AddComponent<MeshRenderer>();
    }
    if(copy){rendererMap[original.Pointer]=copy;copy.sharedMaterials=original.sharedMaterials;copy.enabled=true;copy.shadowCastingMode=original.shadowCastingMode;copy.receiveShadows=original.receiveShadows;}
   }
   if(skinned==0)throw new InvalidOperationException("Native player has no complete character skin");
   foreach(var original in source.GetComponentsInChildren<LODGroup>(true)){
    if(!map.TryGetValue(original.transform.Pointer,out var target))continue;var levels=new List<LOD>();
    foreach(var lod in original.GetLODs()){var renderers=new List<Renderer>();foreach(var renderer in lod.renderers)if(renderer&&rendererMap.TryGetValue(renderer.Pointer,out var copy))renderers.Add(copy);levels.Add(new LOD{screenRelativeTransitionHeight=lod.screenRelativeTransitionHeight,renderers=renderers.ToArray(),fadeTransitionWidth=lod.fadeTransitionWidth});}
    var group=target.gameObject.AddComponent<LODGroup>();group.SetLODs(levels.ToArray());group.fadeMode=original.fadeMode;group.animateCrossFading=original.animateCrossFading;group.RecalculateBounds();
   }
   if(native&&map.TryGetValue(native.transform.Pointer,out var animated)){
    var animator=animated.gameObject.AddComponent<Animator>();animator.avatar=native.avatar;animator.runtimeAnimatorController=native.runtimeAnimatorController;animator.applyRootMotion=false;animator.fireEvents=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;result.Animator=animator;
    result.BindLocomotion(native.runtimeAnimatorController);
   }
   result.ApplyOutfit(outfit);holder.SetActive(true);
   // The local FPS rig contains CHR3_Head/Headset3 shadow-only shells. Turning
   // those into ordinary surfaces placed white faces over a selected mask.
   GameplayLog.Record("avatar.model",new{source=source.name,renderers=rendererMap.Count,skinned,shadowOnly=rendererMap.Values.Count(r=>r.shadowCastingMode==ShadowCastingMode.ShadowsOnly),rigBones=result.rigBones.Count,mount=result.weaponMount?result.weaponMount!.name:"missing",shadowSurfaces=rendererMap.Values.Where(r=>r.shadowCastingMode==ShadowCastingMode.ShadowsOnly).Take(8).Select(r=>new{name=r.name,active=r.gameObject.activeInHierarchy,materials=r.sharedMaterials.Take(4).Select(m=>new{name=m?m.name:"missing",shader=m&&m.shader?m.shader.name:"missing"}).ToArray()}).ToArray()});
   if(result.Animator){result.Animator!.Rebind();result.Animator.Update(0);result.BindLocomotion(result.boundController);}
   return result;
  }catch{Object.Destroy(holder);throw;}
 }
 void BindLocomotion(RuntimeAnimatorController? controller){
  // Asset audit: FPSGenericPlayer in home uses FPSAnimator_Generic (sharedassets0
  // 3187), not EnemyAnimator. These exact names/types are in its serialized values.
  boundController=controller;stanceTransitions.Reset(false);var basis=controller;
  for(var i=0;i<4&&basis;i++){var replace=basis!.TryCast<AnimatorOverrideController>();if(!replace||!replace!.runtimeAnimatorController)break;basis=replace.runtimeAnimatorController;}
  playerLocomotion=basis&&basis!.name.StartsWith("FPSAnimator_",StringComparison.Ordinal);
  if(playerLocomotion&&Animator){Animator!.SetFloat(FullBodyWeight,1);Animator.SetFloat(CrouchWeight,0);Animator.SetFloat(ProneWeight,0);Animator.SetFloat(OverlayType,0);Animator.SetBool(Crouching,false);Animator.SetBool(Proning,false);Animator.SetBool(InAir,false);}
 }
 void UseController(RuntimeAnimatorController? controller){
  if(!Animator||!controller||boundController&&boundController!.Pointer==controller!.Pointer)return;
  Animator!.runtimeAnimatorController=controller;
  // Evaluate the new controller before writing parameters: the first active
  // frame after a hierarchy copy can otherwise retain the source's old pose.
  Animator.Rebind();Animator.Update(0);BindLocomotion(controller);
 }
 public void ApplyWeapon(int id){
  if(id!=heldWeaponId){
   reloadPresentation.Cancel();
   if(heldVisual){heldVisual!.SetActive(false);Object.Destroy(heldVisual);}heldVisual=null;shotMuzzle=null;weaponSight=null;weaponMechanics=null;weaponPose?.Dispose();weaponPose=null;poseRetry=0;throwStarted=-1;heldWeaponId=id;weaponRetry=0;gripReported=false;reportedPoseStatus="";
   if(id<0)UseController(unarmedController);
  }
  if(id<0||heldVisual||Time.unscaledTime<weaponRetry)return;
  weaponRetry=Time.unscaledTime+2;
  if(!weaponMount)return;
  var asset=RemoteShotAssets.Resolve(id);var source=asset?.VisualSource;if(!source)return;
  UseController(asset!.Weapon&&asset.Weapon!.overrideController?asset.Weapon.overrideController:armedController);
  var holder=new GameObject("CoopHeldWeapon."+id);holder.SetActive(false);holder.transform.SetParent(weaponMount,false);
  try{
   var sourceRoot=source!;var map=new Dictionary<IntPtr,Transform>();var required=new HashSet<IntPtr>();var meshes=new List<Renderer>();
   void Keep(Transform? value){
    if(!value||value!.Pointer!=sourceRoot.Pointer&&!value.IsChildOf(sourceRoot))return;
    while(value&&value!.Pointer!=sourceRoot.Pointer){if(!required.Add(value.Pointer))break;value=value.parent;}
   }
   foreach(var original in sourceRoot.GetComponentsInChildren<Renderer>(true)){
    if(original.TryCast<MeshRenderer>()==null&&original.TryCast<SkinnedMeshRenderer>()==null)continue;
    meshes.Add(original);Keep(original.transform);
    if(original.TryCast<SkinnedMeshRenderer>() is {} skin){Keep(skin.rootBone);foreach(var bone in skin.bones)Keep(bone);}
   }
   Keep(asset!.MuzzleSource);
   Keep(asset.AimSource);
   WeaponMechanics.Description? mechanics=null;
   try{mechanics=WeaponMechanics.Describe(sourceRoot);}catch(Exception error){if(mechanicsDiagnostics++<8)GameplayLog.Fault("avatar.weapon_mechanics_describe",error);}
   if(mechanics!=null){Keep(mechanics.Source.transform);foreach(var target in mechanics.Targets)Keep(target);}
   Transform Copy(Transform original,Transform owner,bool top=false){
    var go=new GameObject(original.name);go.transform.SetParent(owner,false);
    // FPSController.InitializeWeapons zeros the instantiated item after its
    // native socket parent is assigned (8AFE2B/8AFEC2). Prefab root offsets
    // are editor placement, not an equipped grip offset; children keep theirs.
    go.transform.localPosition=top?Vector3.zero:original.localPosition;go.transform.localRotation=top?Quaternion.identity:original.localRotation;go.transform.localScale=original.localScale;
    map[original.Pointer]=go.transform;
    for(var i=0;i<original.childCount;i++){var child=original.GetChild(i);if(required.Contains(child.Pointer))Copy(child,go.transform);}
    go.SetActive(top||original.gameObject.activeSelf);return go.transform;
   }
   Copy(sourceRoot,holder.transform,true);var copied=0;
   foreach(var original in meshes){
    if(!map.TryGetValue(original.transform.Pointer,out var target))continue;Renderer? copy=null;
    if(original.TryCast<SkinnedMeshRenderer>() is {} skin){
     if(!skin.sharedMesh)continue;var sourceBones=skin.bones;var bones=new Transform[sourceBones.Length];var valid=true;
     for(var i=0;i<bones.Length;i++){var bone=sourceBones[i];if(!bone||!map.TryGetValue(bone.Pointer,out var mapped)){valid=false;break;}bones[i]=mapped;}
     if(!valid)continue;
     var renderer=target.gameObject.AddComponent<SkinnedMeshRenderer>();renderer.sharedMesh=skin.sharedMesh;renderer.bones=bones;renderer.localBounds=skin.localBounds;
     if(skin.rootBone&&map.TryGetValue(skin.rootBone.Pointer,out var root))renderer.rootBone=root;copy=renderer;
    }else{
     var filter=original.GetComponent<MeshFilter>();if(!filter||!filter.sharedMesh)continue;
     target.gameObject.AddComponent<MeshFilter>().sharedMesh=filter.sharedMesh;copy=target.gameObject.AddComponent<MeshRenderer>();
    }
    copy.sharedMaterials=original.sharedMaterials;copy.enabled=original.enabled;copy.shadowCastingMode=ShadowCastingMode.On;copy.receiveShadows=true;copied++;
   }
   if(copied==0){Object.Destroy(holder);return;}
   if(asset.MuzzleSource&&map.TryGetValue(asset.MuzzleSource!.Pointer,out var muzzle))shotMuzzle=muzzle;
   if(asset.AimSource&&map.TryGetValue(asset.AimSource!.Pointer,out var sight))weaponSight=sight;
   heldVisual=holder;holder.SetActive(true);
   if(mechanics!=null)try{weaponMechanics=WeaponMechanics.Bind(mechanics,map,holder.transform);}catch(Exception error){GameplayLog.Fault("avatar.weapon_mechanics_bind",error);}
  }catch(Exception e){Object.Destroy(holder);heldVisual=null;shotMuzzle=null;weaponMechanics=null;if(reportedWeaponId!=id){reportedWeaponId=id;Plugin.Instance.Log.LogWarning("Remote weapon visual is not ready: "+e.Message);}}
 }
 public bool TryGetShotPose(out Vector3 position,out Vector3 forward){
  position=default;forward=default;if(!shotMuzzle||!heldVisual||!heldVisual!.activeInHierarchy)return false;
  position=shotMuzzle!.position;forward=shotMuzzle.forward;return true;
 }
 public void LatePose(float pitch,int aimState=0,long throwSequence=0,long reloadSequence=0,int reloadKind=0,float reloadElapsed=0){
  var reloadChange=reloadPresentation.Observe(reloadSequence,reloadKind,reloadElapsed,heldWeaponId,Time.unscaledTime);
  if(reloadChange==ReloadChange.Started){throwStarted=-1;weaponPose?.ResetShot();weaponPose?.ResetReload();weaponMechanics?.Reset();}
  if(reloadChange!=ReloadChange.None&&reloadDiagnostics++<12)GameplayLog.Record("avatar.reload_remote",new{sequence=reloadSequence,kind=reloadPresentation.Kind,elapsed=reloadPresentation.Elapsed(Time.unscaledTime),weaponId=heldWeaponId,change=reloadChange.ToString()});
  // A joining peer's first sequence is a baseline, not an old throw replay.
  if(!throwBaseline||throwSequence<lastThrowSequence){throwBaseline=true;lastThrowSequence=throwSequence;throwStarted=-1;}
  else if(reloadPresentation.TakeThrow(throwSequence,ref lastThrowSequence)){throwStarted=Time.unscaledTime;reloadPresentation.Cancel();weaponPose?.ResetShot();weaponPose?.ResetReload();weaponMechanics?.Reset();}
  if(!heldVisual||heldWeaponId<0||!bodyRoot||!weaponMount)return;
  if(weaponPose==null&&Time.unscaledTime>=poseRetry){
   poseRetry=Time.unscaledTime+2;var asset=RemoteShotAssets.Resolve(heldWeaponId);
   if(asset!=null)try{
    weaponPose=RemoteWeaponPose.Create(bodyRoot!,weaponMount!,weaponSight,rigBones,asset,heldWeaponId,out var status);
    if(status!=reportedPoseStatus&&poseDiagnostics<16){reportedPoseStatus=status;poseDiagnostics++;GameplayLog.Record("avatar.grip_binding",new{weaponId=heldWeaponId,status,rigBones=rigBones.Count,equipped=weaponMount!.name,authored=asset.Pose?asset.Pose!.weaponBone.name:"missing",clip=asset.Pose&&asset.Pose!.poseToSample&&asset.Pose.poseToSample.clip?asset.Pose.poseToSample.clip.name:"missing"});}
   }
   catch(Exception error){if(reportedPoseId!=heldWeaponId){reportedPoseId=heldWeaponId;GameplayLog.Fault("avatar.weapon_pose",error);}}
  }
  var reloadTime=reloadPresentation.Elapsed(Time.unscaledTime);
  weaponPose?.Apply(pitch,aimState,throwStarted<0?-1:Time.unscaledTime-throwStarted,reloadPresentation.Kind,reloadTime);
  weaponMechanics?.SyncReload(reloadPresentation.Sequence,reloadPresentation.Kind,reloadTime);
  weaponMechanics?.Tick(Time.unscaledDeltaTime);
  if(weaponPose!=null&&!gripReported&&poseDiagnostics<16){gripReported=true;poseDiagnostics++;GameplayLog.Record("avatar.grip",weaponPose.Diagnostics(heldWeaponId));}
 }
 public void NotifyShot(int weaponId,long sequence,int aimState){
  if(weaponId!=heldWeaponId||!heldVisual||!heldVisual!.activeInHierarchy||!RemoteFireMotionPolicy.Accept(sequence,lastShotSequence,Time.unscaledTime))return;
  lastShotSequence=sequence;
  // A valid shot never builds a rig or replays later if its visual is pending.
  var applied=!reloadPresentation.Active&&weaponPose?.NotifyShot(sequence,aimState)==true;
  var mechanical=!reloadPresentation.Active&&weaponMechanics?.Notify()==true;
  if(shotDiagnostics<8){shotDiagnostics++;GameplayLog.Record("avatar.fire_motion",new{weaponId,sequence,applied,mechanical});}
 }
 public void ResetAnimationEvents(){throwBaseline=false;lastThrowSequence=0;throwStarted=-1;reloadPresentation.Reset();weaponPose?.ResetShot();weaponPose?.ResetReload();weaponMechanics?.Reset();stanceTransitions.Reset(true);}
 public void ApplyOutfit(OutfitEntry[] outfit){
  if(ReferenceEquals(priorOutfit,outfit))return;
  if(priorOutfit!=null&&priorOutfit.Length==outfit.Length){var same=true;for(var i=0;i<outfit.Length;i++)if(priorOutfit[i].Name!=outfit[i].Name||priorOutfit[i].Enabled!=outfit[i].Enabled){same=false;break;}if(same){priorOutfit=outfit;return;}}
  priorOutfit=outfit;
  if(outfit.Length==0)return;
  foreach(var objects in clothing.Values)foreach(var go in objects)go.SetActive(false);
  foreach(var item in outfit)if(clothing.TryGetValue(item.Name,out var objects))foreach(var go in objects)go.SetActive(item.Enabled);
 }
 public void Move(Vector3 localVelocity,float height,int stance=-1,int motion=-1){
  if(!Animator)return;
  if(!boundController&&Time.unscaledTime>=nextAnimatorRetry){nextAnimatorRetry=Time.unscaledTime+2;var source=AvatarAppearance.Source();var native=source?source!.GetComponent<Animator>():null;if(native&&native.runtimeAnimatorController)UseController(native.runtimeAnimatorController);}
  if(!playerLocomotion)return;
  // A jump/fall is not walking speed. Prefer actual native pose state over a
  // guessed capsule-height threshold; the fallback is retained for older QA.
  localVelocity.y=0;var speed=localVelocity.magnitude;
  var moving=motion>=0?motion==1||motion==2||motion==4:speed>.08f;
  if(speed>.08f)lastMoveDirection=localVelocity/speed;
  // The native controller writes normalized input axes and their magnitude,
  // not world metres/second (FPSMovement.UpdateAnimatorParams 8B845C–8B84DC).
  // Explicit native Idle/InAir also prevents packet interpolation jitter from
  // alternating idle/walk. Keep the last direction between walking samples.
  var direction=moving?lastMoveDirection:Vector3.zero;
  var prone=stance>=0?stance==2:height<.9f;var crouch=stance>=0?stance==1:!prone&&height<1.45f;
  var sprint=motion>=0?(motion==2?1f:0f):Mathf.Clamp01((speed-3)/2);
  void Set(int parameter,float value)=>Animator!.SetFloat(parameter,value,.12f,Time.unscaledDeltaTime);
  Set(MoveX,direction.x);Set(MoveY,direction.z);Set(Velocity,direction.magnitude);Set(Sprinting,sprint);Set(SprintPoseWeight,sprint);Set(CrouchWeight,crouch?1:0);Set(ProneWeight,prone?1:0);
  Animator!.SetBool(Moving,moving);Animator.SetBool(Crouching,crouch);Animator.SetBool(Proning,prone);Animator.SetBool(InAir,motion==3);
  // The pinned native graph has no bool transition into Prone. Select only
  // on an explicit stance edge; keep ordinary jump/slide and blend updates.
  if(stanceTransitions.Take(stance,motion,out var prior))Animator.CrossFadeInFixedTime(LocomotionStances[stance],stance==2||prior==2?.15f:.25f,0,0,0);
 }

 // Pinned asset evidence: sharedassets0 WeaponAnimator#3188 has no SMBs,
 // transitions or gameplay bindings; all eight inspected overrides retain it.
 // Never use a character controller, a guessed Fire clip or an unknown profile.
 sealed class WeaponMechanics {
  internal sealed class Action {
   internal readonly int Hash;internal readonly float Seconds;internal readonly Transform[] Targets;
   internal Action(string state,float seconds,Transform[] targets){Hash=UnityEngine.Animator.StringToHash(state);Seconds=seconds;Targets=targets;}
  }
  internal sealed class Description {
   internal readonly Animator Source;internal readonly RuntimeAnimatorController Controller;internal readonly Transform[] Targets;
   internal readonly Action? Fire,Reload,Empty;
   internal Description(Animator source,RuntimeAnimatorController controller,Action? fire,Action? reload,Action? empty){Source=source;Controller=controller;Fire=fire;Reload=reload;Empty=empty;Targets=new[]{fire,reload,empty}.Where(a=>a!=null).SelectMany(a=>a!.Targets).GroupBy(t=>t.Pointer).Select(g=>g.First()).ToArray();}
  }
  readonly struct Rest {
   readonly Transform target;readonly Vector3 position,scale;readonly Quaternion rotation;
   internal Rest(Transform value){target=value;position=value.localPosition;rotation=value.localRotation;scale=value.localScale;}
   internal void Restore(){if(target){target.localPosition=position;target.localRotation=rotation;target.localScale=scale;}}
  }
  readonly Animator animator;readonly Rest[] rest;readonly Description description;Action? action;float elapsed;long reloadGeneration=-1;int activeKind;bool active,faulted,fresh;
  WeaponMechanics(Animator value,Rest[] snapshot,Description data){animator=value;rest=snapshot;description=data;}
  internal static Description? Describe(Transform sourceRoot){
   var source=sourceRoot.GetComponentInChildren<Animator>();if(!source||source!.transform.Pointer!=sourceRoot.Pointer&&!source.transform.IsChildOf(sourceRoot))return null;
   var controller=source.runtimeAnimatorController;var replace=controller?controller!.TryCast<AnimatorOverrideController>():null;
   if(!replace||!replace!.runtimeAnimatorController||replace.runtimeAnimatorController.name!="WeaponAnimator"||replace.runtimeAnimatorController.TryCast<AnimatorOverrideController>())return null;
   var clips=controller!.animationClips;if(clips==null||clips.Length<1||clips.Length>16)return null;
   Action? Clip(string state,string clipName,float seconds,params string[] paths){
    var found=false;foreach(var clip in clips)if(clip&&clip.name==clipName&&float.IsFinite(clip.length)&&Mathf.Abs(clip.length-seconds)<.001f){found=true;break;}if(!found)return null;
    var targets=new Transform[paths.Length];for(var i=0;i<paths.Length;i++){var target=source.transform.Find(paths[i]);if(!target||!target!.IsChildOf(sourceRoot))return null;targets[i]=target;}return new Action(state,seconds,targets);
   }
   Action? fire=null,reload=null,empty=null;
   switch(controller.name){
    case "AK12_Animator":
     fire=Clip("Fire","W_Fire",.1f,"AK12Rig/Root/Charge");
     reload=Clip("Reload","W_AK12_ReloadEmpty",4.5f,"AK12Rig/Root/Mag","AK12Rig/Root/Charge","AK12Rig/Root/Safety");
     empty=Clip("ReloadEmpty","W_AK12_ReloadEmpty",4.5f,"AK12Rig/Root/Mag","AK12Rig/Root/Charge","AK12Rig/Root/Safety");break;
    case "AK12_Animator OLD":
     fire=Clip("Fire","W_Fire old ak",.1f,"AK12Rig/Root/Charge","AK12Rig/GameObject");
     reload=Clip("Reload","W_AK12_ReloadEmpty",4.5f,"AK12Rig/Root/Mag","AK12Rig/Root/Charge","AK12Rig/Root/Safety");
     empty=Clip("ReloadEmpty","W_AK12_ReloadEmpty",4.5f,"AK12Rig/Root/Mag","AK12Rig/Root/Charge","AK12Rig/Root/Safety");break;
    case "Mk18_Animator":
     fire=Clip("Fire","W_Fire",.1f,"AK12Rig/Root/Charge");
     reload=Clip("Reload","A_W_Mk18_Reload_Empty",2.0166669f,"Mk18/Root/Bolt","Mk18/Root/BoltRelease","Mk18/Root/Mag");
     empty=Clip("ReloadEmpty","A_W_Mk18_Reload_Empty",2.0166669f,"Mk18/Root/Bolt","Mk18/Root/BoltRelease","Mk18/Root/Mag");break;
    case "MP5_Animator 1":
     fire=Clip("Fire","W_Fire",.1f,"AK12Rig/Root/Charge");
     // Pinned weaponReload contains a non-Transform binding and unresolved
     // target. Keep native body reload, but never partially play that weapon clip.
     break;
    case "W_Mk23_Animator":
     fire=Clip("Fire","W_Mk23Mod0_BasePose",2f/15,"Mk23Mod0Rig/Root","Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider");
     empty=Clip("ReloadEmpty","W_Mk23Mod0_ReloadEmpty",3.0666668f,"Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider");break;
    case "TEST":
     fire=Clip("Fire","W_Mk23Mod0_BasePose",2f/15,"Mk23Mod0Rig/Root","Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider");break;
    case "w_glock":
     fire=Clip("Fire","W_Mk23Mod0_BasePose",2f/15,"Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider","Mk23Mod0Rig/Root/Barrel");
     empty=Clip("ReloadEmpty","W_Mk23Mod0_ReloadEmpty",3.0666668f,"Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider","Mk23Mod0Rig/Root/Barrel");break;
    case "w_micro":
     fire=Clip("Fire","W_micro_BasePose",2f/15,"Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider","Mk23Mod0Rig/Root/Barrel");
     empty=Clip("ReloadEmpty","W_micro_ReloadEmpty",3.0666668f,"Mk23Mod0Rig/Root/Mag","Mk23Mod0Rig/Root/Slider","Mk23Mod0Rig/Root/Barrel");break;
    default:return null;
   }
   // Tactical pistol clips have unresolved pinned bindings: no guessed paths.
   return fire==null&&reload==null&&empty==null?null:new Description(source,controller,fire,reload,empty);
  }
  internal static WeaponMechanics? Bind(Description description,Dictionary<IntPtr,Transform> map,Transform holder){
   if(!map.TryGetValue(description.Source.transform.Pointer,out var copied)||!copied||!copied.IsChildOf(holder))return null;
   foreach(var target in description.Targets)if(!map.TryGetValue(target.Pointer,out var value)||!value||!value.IsChildOf(copied))return null;
   var values=new List<Rest>();foreach(var value in map.Values)if(value&&(value.Pointer==copied.Pointer||value.IsChildOf(copied))){if(values.Count>=256)return null;values.Add(new Rest(value));}if(values.Count==0)return null;
   var animator=copied.gameObject.AddComponent<Animator>();var result=new WeaponMechanics(animator,values.ToArray(),description);
   try{animator.enabled=false;animator.fireEvents=false;animator.applyRootMotion=false;animator.runtimeAnimatorController=description.Controller;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;animator.Rebind();result.Reset();return result;}
   catch{Object.Destroy(animator);result.Reset();throw;}
  }
  internal bool Notify(){
   if(faulted||!animator||activeKind!=0||description.Fire==null)return false;Reset();
   try{action=description.Fire;animator.Play(action.Hash,0,0);elapsed=0;active=true;return true;}catch(Exception error){Fail(error);return false;}
  }
  internal bool SyncReload(long generation,int kind,float time){
   if(faulted||!animator)return false;
   if(kind==0){if(activeKind!=0)Reset();return false;}
   var selected=kind==2?description.Empty:description.Reload;
   if(selected==null||!float.IsFinite(time)||time<0||time>=selected.Seconds){if(activeKind!=0)Reset();return false;}
   try{
    if(generation!=reloadGeneration||kind!=activeKind||!active){Reset();reloadGeneration=generation;activeKind=kind;action=selected;elapsed=time;animator.Play(selected.Hash,0,time/selected.Seconds);animator.Update(0);active=true;fresh=true;}
    else if(time>elapsed+.25f){elapsed=time;animator.Play(selected.Hash,0,time/selected.Seconds);animator.Update(0);fresh=true;}
    return true;
   }catch(Exception error){Fail(error);return false;}
  }
  internal void Tick(float dt){
   if(!active||faulted||!animator||action==null)return;if(fresh){fresh=false;return;}
   if(!float.IsFinite(dt)||dt<0){Reset();return;}
   try{var step=Mathf.Min(dt,action.Seconds-elapsed);animator.Update(step);elapsed+=step;if(elapsed>=action.Seconds)Reset();}catch(Exception error){Fail(error);}
  }
  internal void Reset(){active=false;fresh=false;elapsed=0;activeKind=0;action=null;foreach(var value in rest)value.Restore();}
  void Fail(Exception error){faulted=true;try{Reset();}catch{}GameplayLog.Fault("avatar.weapon_mechanics",error);}
 }


 // Managed policy only: cached edge selection, no Unity/native calls.
 sealed class StanceTransitions {
  int prior=-1;bool forceStanding;
  internal void Reset(bool forceNextStanding){prior=-1;forceStanding=forceNextStanding;}
  internal bool Take(int stance,int motion,out int previous){
   previous=prior;
   if(stance<0||stance>2||motion==3||motion==4||stance==prior)return false;
   prior=stance;var force=forceStanding;forceStanding=false;
   // A newly rebound controller already starts in its authored Standing state.
   // Visibility/world reset must also recover an old Prone/exit transition.
   return previous>=0||stance!=0||force;
  }
 }
}
