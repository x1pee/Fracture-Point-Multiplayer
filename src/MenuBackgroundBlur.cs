using BepInEx.Configuration;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace FractureTogether;

// Scene-only blur: native overlay canvases and IMGUI are drawn after this pass.
// No existing Volume/Profile is edited and no captured texture is retained.
static class MenuBackgroundBlur
{
 const float ClosedAmount=.001f;
 static ConfigEntry<bool>? enabled;
 static GameObject? owner;
 static Volume? volume;
 static VolumeProfile? profile;
 static DepthOfField? depth;
 static ClampedFloatParameter? radiusParameter;
 static Camera? camera;
 static UniversalAdditionalCameraData? cameraData;
 static VolumeStack? createdManualStack;
 static IntPtr pipelinePointer;
 static int sceneHandle,appliedMask;
 static bool bound,effectActive,originalPostProcessing,postProcessingModified,manualVolumes;
 static bool stopped=true,capabilitiesKnown,capabilitiesSupported,warningReported;
 static float appliedRadius=-1,retryAt;
 internal static bool Active=>effectActive;
 internal static Camera? ActiveCamera=>camera;
 internal static float Radius=>effectActive?appliedRadius:0;

 internal static void Initialize(ConfigEntry<bool> option)
 {
  Shutdown();
  enabled=option??throw new ArgumentNullException(nameof(option));
  stopped=false;warningReported=false;retryAt=0;
 }

 internal static void Tick(float amount)
 {
  // Closed steady state touches no camera, renderer, Volume or scene API.
  if(stopped)return;
  if(enabled?.Value!=true||!float.IsFinite(amount)||amount<=ClosedAmount){DeactivateSafely();return;}
  if(Time.unscaledTime<retryAt)return;
  try{Apply(Mathf.Clamp01(amount));}
  catch(Exception error){ReleaseOwned();retryAt=Time.unscaledTime+2;Warn(error);}
 }

 static void Apply(float amount)
 {
  var pipeline=GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();
  if(!pipeline){DeactivateSafely();return;}
  if(!capabilitiesKnown){capabilitiesSupported=SystemInfo.graphicsShaderLevel>=35&&SystemInfo.supportedRenderTargetCount>1;capabilitiesKnown=true;}
  if(!capabilitiesSupported){DeactivateSafely();return;}
  var nextCamera=Camera.main;
  if(!nextCamera||!nextCamera.enabled||!nextCamera.gameObject.activeInHierarchy){DeactivateSafely();return;}
  var nextScene=SceneManager.GetActiveScene().handle;
  if(!bound||!camera||!cameraData||camera.Pointer!=nextCamera.Pointer||pipelinePointer!=pipeline.Pointer||sceneHandle!=nextScene){
   var nextData=nextCamera.GetComponent<UniversalAdditionalCameraData>();
   // StartSpectator copies the old camera's renderPostProcessing flag. If that
   // flag was temporarily forced by us, its known copy also needs restoration.
   var copiedTemporaryPost=bound&&camera&&camera.Pointer!=nextCamera.Pointer&&postProcessingModified&&!originalPostProcessing&&nextData&&nextData.renderPostProcessing&&Together.Current?.Spectating==true&&nextCamera.gameObject.name=="FractureTogether.SpectatorCamera";
   Deactivate();
   if(!nextData)return;
   camera=nextCamera;cameraData=nextData;bound=true;sceneHandle=nextScene;pipelinePointer=pipeline.Pointer;
   originalPostProcessing=copiedTemporaryPost?false:nextData.renderPostProcessing;
   postProcessingModified=copiedTemporaryPost;manualVolumes=!nextData.requiresVolumeFrameworkUpdate;
   appliedMask=0;appliedRadius=-1;
  }
  var mask=cameraData!.volumeLayerMask.value;
  if(mask==0){Deactivate();return;} // Never expand a native camera's mask.
  EnsureOwned();
  var changed=false;
  if(appliedMask!=mask){
   if(effectActive){owner!.SetActive(false);effectActive=false;}
   var layer=0;while(layer<31&&(mask&(1<<layer))==0)layer++;
   owner!.layer=layer;appliedMask=mask;changed=true;
  }
  if(!cameraData.renderPostProcessing){
   if(!postProcessingModified)originalPostProcessing=false;
   postProcessingModified=true;cameraData.renderPostProcessing=true;changed=true;
  }
  var manual=!cameraData.requiresVolumeFrameworkUpdate;
  if(manual!=manualVolumes){manualVolumes=manual;changed=true;}
  // A Volume weight fade alone steps the mode enum and blends against a
  // nonzero default Gaussian radius. Instead fade our own radius at weight 1.
  var radius=amount*amount*(3-2*amount);
  if(appliedRadius!=radius){radiusParameter!.value=radius;appliedRadius=radius;changed=true;}
  if(!effectActive){depth!.active=true;volume!.weight=1;owner!.SetActive(true);effectActive=true;changed=true;}
  if(changed)RefreshManualStack();
 }

 static void EnsureOwned()
 {
  if(owner&&volume&&profile&&depth&&radiusParameter!=null)return;
  // A partial/externally destroyed owned set is never reused.
  DestroyOwnedObjects();
  owner=new GameObject("FractureTogether.MenuBackgroundBlur");
  owner.SetActive(false);owner.hideFlags=HideFlags.HideAndDontSave;
  Object.DontDestroyOnLoad(owner);
  profile=ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).TryCast<VolumeProfile>();
  if(!profile)throw new InvalidOperationException("Could not create the menu VolumeProfile.");
  profile.name="FractureTogether.MenuBackgroundBlur.Profile";profile.hideFlags=HideFlags.HideAndDontSave;
  depth=profile.Add(Il2CppType.Of<DepthOfField>(),false).TryCast<DepthOfField>();
  if(!depth)throw new InvalidOperationException("Native Gaussian depth of field is unavailable.");
  depth.hideFlags=HideFlags.HideAndDontSave;depth.active=false;
  depth.mode.value=DepthOfFieldMode.Gaussian;depth.mode.overrideState=true;
  depth.gaussianStart.value=0;depth.gaussianStart.overrideState=true;
  depth.gaussianEnd.value=.1f;depth.gaussianEnd.overrideState=true;
  radiusParameter=depth.gaussianMaxRadius;
  // The native constructor clamps this parameter to [0.5,1.5]. Only our own
  // instance permits zero, allowing a smooth low-radius transition.
  radiusParameter.min=0;radiusParameter.max=1;radiusParameter.value=0;radiusParameter.overrideState=true;
  depth.highQualitySampling.value=false;depth.highQualitySampling.overrideState=true;
  volume=owner.AddComponent<Volume>();volume.isGlobal=true;volume.blendDistance=0;
  volume.priority=float.MaxValue;volume.weight=0;volume.sharedProfile=profile;
  effectActive=false;appliedMask=0;appliedRadius=-1;
 }

 static void RefreshManualStack()
 {
  if(!camera||!cameraData||cameraData.requiresVolumeFrameworkUpdate)return;
  var before=cameraData.volumeStack;
  try{UnityEngine.Rendering.Universal.CameraExtensions.UpdateVolumeStack(camera,cameraData);}
  finally{
   if(before==null&&createdManualStack==null&&cameraData)createdManualStack=cameraData.volumeStack;
  }
 }

 static void ReleaseCreatedManualStack()
 {
  var created=createdManualStack;createdManualStack=null;
  if(created==null)return;
  var current=cameraData?cameraData.volumeStack:null;
  if(camera&&cameraData&&current!=null&&current.Pointer==created.Pointer)
   UnityEngine.Rendering.Universal.CameraExtensions.DestroyVolumeStack(camera,cameraData);
  else if(created.isValid)created.Dispose();
 }

 static void RestorePostProcessing()
 {
  // Compare with the value we applied; preserve a different native/user value.
  if(postProcessingModified&&cameraData&&cameraData.renderPostProcessing)
   cameraData.renderPostProcessing=originalPostProcessing;
 }

 static void Deactivate()
 {
  if(!bound&&!effectActive)return;
  var hadEffect=effectActive;
  try{
   if(volume)volume.weight=0;
   if(depth)depth.active=false;
   if(owner&&owner.activeSelf)owner.SetActive(false);
   effectActive=false;
   if(hadEffect)RefreshManualStack();
  }finally{
   try{RestorePostProcessing();}
   finally{
    try{ReleaseCreatedManualStack();}
    finally{camera=null;cameraData=null;bound=false;effectActive=false;postProcessingModified=false;pipelinePointer=IntPtr.Zero;appliedMask=0;appliedRadius=-1;}
   }
  }
 }

 static void DeactivateSafely()
 {
  if(!bound&&!effectActive)return;
  try{Deactivate();}
  catch(Exception error){ReleaseOwned();Warn(error);}
 }

 static void DestroyOwnedObjects()
 {
  try{if(volume){volume.weight=0;volume.sharedProfile=null;}}
  catch(Exception error){Warn(error);}
  // Profile.OnDisable releases its listed parameters, but does not destroy
  // its ScriptableObjects. Clear the owned list and destroy each exactly once.
  try{if(profile)profile.components.Clear();}
  catch(Exception error){Warn(error);}
  Destroy(depth);Destroy(profile);Destroy(owner);
  owner=null;volume=null;profile=null;depth=null;radiusParameter=null;effectActive=false;
 }

 static void Destroy(Object? value)
 {
  try{if(value)Object.Destroy(value);}
  catch(Exception error){Warn(error);}
 }

 static void ReleaseOwned()
 {
  try{Deactivate();}
  catch(Exception error){Warn(error);}
  DestroyOwnedObjects();
 }

 static void Warn(Exception error)
 {
  if(warningReported)return;warningReported=true;
  try{Plugin.Instance.Log.LogWarning("Menu background blur unavailable; its changes were released: "+error.Message);}
  catch{ }
 }

 internal static void Shutdown()
 {
  stopped=true;ReleaseOwned();enabled=null;retryAt=0;
 }
}
