using BepInEx.Configuration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object=UnityEngine.Object;

namespace FractureTogether;

public sealed partial class Together
{
 public static ConfigEntry<float> SpectatorDistance=null!;
 public static ConfigEntry<bool> SpectatorFirstPerson=null!;
 Camera? spectatorCamera,ownCamera;AudioListener? ownListener;bool ownCameraEnabled,ownListenerEnabled,ownControllerEnabled,ownColliderEnabled;
 Demo.Scripts.Runtime.Character.FPSController? spectatingBody;
 CharacterController? spectatingCollider;
 readonly List<Renderer> hiddenSpectatorRenderers=new();
 readonly List<RemoteAvatar> spectatorTargets=new(64);
 readonly List<SpectatorSurfaceState> spectatorSurfaces=new(128);
 DepthOfField? healthDepthOfField;CanvasGroup? damageCanvas;bool depthOfFieldActive;float damageCanvasAlpha;
 float spectatorBaseFov,spectatorBaseNear,nextSpectatorSurfaceRefresh;
 Camera? spectatorHealthyCamera;IntPtr spectatorHealthyBody;float spectatorHealthyFov;
 bool spectatorActualFirstPerson,spectatorInputArmed;
 readonly SpectatorTapLatch spectatorNextTap=new(),spectatorPreviousTap=new(),spectatorModeTap=new();
 int spectatorSampleFrame=-1,spectatorControlEvents;bool spectatorNextDown,spectatorPreviousDown,spectatorModeDown,spectatorNextHeld,spectatorPreviousHeld,spectatorModeHeld;
 int spectatorVisualTarget=-1,spectatorVisualWeapon=-1;IntPtr spectatorVisualRoot;
 string spectatorWorld="";int spectatorFloor;GUIStyle? spectatorHudStyle;
 public int SpectatedPlayer=-1;public bool Spectating=>spectatorCamera;
 static void InitSpectator(){
  SpectatorDistance=Plugin.Instance.Config.Bind("Spectator","Distance",2.5f,new ConfigDescription("Third-person distance. Left mouse: next teammate; right mouse: previous; Space: first/third person.",new AcceptableValueRange<float>(0,5)));
  SpectatorFirstPerson=Plugin.Instance.Config.Bind("Spectator","FirstPerson",false,"Use the teammate's replicated eye camera. Space switches first/third person while spectating.");
  if(SpectatorDistance.Value<=.05f){SpectatorFirstPerson.Value=true;SpectatorDistance.Value=2.5f;}
 }
 void RestoreSpectatorSurfaces(){
  foreach(var saved in spectatorSurfaces)if(saved.Renderer){saved.Renderer.shadowCastingMode=saved.Shadows;saved.Renderer.enabled=saved.Enabled;}
  spectatorSurfaces.Clear();spectatorVisualTarget=-1;spectatorVisualRoot=IntPtr.Zero;spectatorVisualWeapon=-1;nextSpectatorSurfaceRefresh=0;
 }
 void ResetSpectatorView(){
  RestoreSpectatorSurfaces();spectatorActualFirstPerson=false;
  if(spectatorCamera){spectatorCamera!.fieldOfView=spectatorBaseFov;spectatorCamera.nearClipPlane=spectatorBaseNear;spectatorCamera.ResetProjectionMatrix();}
 }
 void StopSpectator(){
  ResetSpectatorView();
  if(spectatorCamera){spectatorCamera!.enabled=false;spectatorCamera.gameObject.SetActive(false);Object.Destroy(spectatorCamera.gameObject);}spectatorCamera=null;
  if(ownCamera)ownCamera!.enabled=ownCameraEnabled;if(ownListener)ownListener!.enabled=ownListenerEnabled;
  if(spectatingBody)spectatingBody!.enabled=ownControllerEnabled;if(spectatingCollider)spectatingCollider!.enabled=ownColliderEnabled;
  foreach(var renderer in hiddenSpectatorRenderers)if(renderer)renderer.enabled=true;hiddenSpectatorRenderers.Clear();
  if(healthDepthOfField)healthDepthOfField!.active=depthOfFieldActive;if(damageCanvas)damageCanvas!.alpha=damageCanvasAlpha;healthDepthOfField=null;damageCanvas=null;
  ownCamera=null;ownListener=null;spectatingBody=null;spectatingCollider=null;SpectatedPlayer=-1;spectatorInputArmed=false;spectatorTargets.Clear();
 }
 void StartSpectator(){
  if(!player)return;
  // Camera can name a socket rather than the actual camera object. Never copy
  // a menu/scope/unrelated Camera.main while the dead player is being rebuilt.
  ownCamera=SpectatorView.CameraOf(player!);if(!ownCamera)return;
  ownCameraEnabled=ownCamera!.enabled;ownListener=ownCamera.GetComponent<AudioListener>()??player!.GetComponentInChildren<AudioListener>(true);ownListenerEnabled=ownListener&&ownListener!.enabled;
  var health=player!.Health;
  if(health){healthDepthOfField=health!.depthOfField;depthOfFieldActive=healthDepthOfField&&healthDepthOfField!.active;damageCanvas=health.CanvasGroup;damageCanvasAlpha=damageCanvas?damageCanvas!.alpha:0;}
  spectatingBody=player;ownControllerEnabled=player.enabled;spectatingCollider=player.GetComponent<CharacterController>();ownColliderEnabled=spectatingCollider&&spectatingCollider!.enabled;
  player.enabled=false;if(spectatingCollider)spectatingCollider!.enabled=false;
  foreach(var renderer in player.GetComponentsInChildren<Renderer>(true))if(renderer.enabled){hiddenSpectatorRenderers.Add(renderer);renderer.enabled=false;}
  var go=new GameObject("FractureTogether.SpectatorCamera");go.SetActive(false);go.tag="MainCamera";
  spectatorCamera=go.AddComponent<Camera>();spectatorCamera.enabled=false;spectatorCamera.CopyFrom(ownCamera);spectatorCamera.enabled=false;spectatorCamera.targetTexture=null;spectatorCamera.orthographic=false;
  // Death during ADS must not leave third person stuck at the zoomed FOV.
  spectatorBaseFov=Mathf.Clamp(spectatorHealthyBody==player.Pointer&&spectatorHealthyFov>0?spectatorHealthyFov:ownCamera.fieldOfView,10,140);spectatorBaseNear=Mathf.Clamp(ownCamera.nearClipPlane,.01f,.3f);
  spectatorCamera.fieldOfView=spectatorBaseFov;spectatorCamera.nearClipPlane=spectatorBaseNear;spectatorCamera.ResetProjectionMatrix();
  var originalData=ownCamera.GetComponent<UniversalAdditionalCameraData>();var data=go.AddComponent<UniversalAdditionalCameraData>();data.renderType=CameraRenderType.Base;
  if(originalData){data.renderPostProcessing=originalData!.renderPostProcessing;data.volumeLayerMask=originalData.volumeLayerMask;data.antialiasing=originalData.antialiasing;data.antialiasingQuality=originalData.antialiasingQuality;data.renderShadows=originalData.renderShadows;}
  ownCamera.enabled=false;if(ownListener)ownListener!.enabled=false;go.AddComponent<AudioListener>();
  spectatorInputArmed=false;spectatorWorld=world;spectatorFloor=floor;
 }
 [HideFromIl2Cpp]
 void UpdateSpectatorSurfaces(RemoteAvatar target,bool firstPerson){
  if(!firstPerson||!target.Root){if(spectatorSurfaces.Count>0)RestoreSpectatorSurfaces();return;}
  if(spectatorVisualTarget==target.Id&&spectatorVisualRoot==target.Root!.Pointer&&spectatorVisualWeapon==target.State!.WeaponId&&Time.unscaledTime<nextSpectatorSurfaceRefresh)return;
  RestoreSpectatorSurfaces();spectatorVisualTarget=target.Id;spectatorVisualRoot=target.Root!.Pointer;spectatorVisualWeapon=target.State!.WeaponId;nextSpectatorSurfaceRefresh=Time.unscaledTime+.5f;
  // Only the selected remote's visual renderers change. Its physics, health,
  // Animator and colliders remain active. Keep body shadows in first person.
  foreach(var renderer in target.Root.GetComponentsInChildren<Renderer>(true)){
   if(spectatorSurfaces.Count>=512)break;
   spectatorSurfaces.Add(new SpectatorSurfaceState(renderer));
   if(renderer.shadowCastingMode==ShadowCastingMode.Off)renderer.enabled=false;
   else renderer.shadowCastingMode=ShadowCastingMode.ShadowsOnly;
  }
 }
 [HideFromIl2Cpp]
 void SpectatorInputTick(){
  if(!Down||PartyWiped||!Spectating)return;
  // Sample in Update, beside menu hotkeys, before native gameplay consumes
  // short Win32 edges. Render still applies the newest view in LateUpdate.
  spectatorSampleFrame=Time.frameCount;
  spectatorNextDown=SpectatorView.MouseDown(0);spectatorPreviousDown=SpectatorView.MouseDown(1);spectatorModeDown=InputBridge.Down(KeyCode.Space);
  spectatorNextHeld=SpectatorView.MouseHeld(0);spectatorPreviousHeld=SpectatorView.MouseHeld(1);spectatorModeHeld=InputBridge.Held(KeyCode.Space);
 }
 [HideFromIl2Cpp]
 void SpectatorTick(){
  if(!Down||PartyWiped||Network==null||!SharedRun){
   if(spectatorCamera)StopSpectator();
   if(!Down&&!PartyWiped&&Network!=null&&SharedRun&&player){
    if(spectatorHealthyBody!=player!.Pointer||!spectatorHealthyCamera){spectatorHealthyBody=player.Pointer;spectatorHealthyCamera=SpectatorView.CameraOf(player);spectatorHealthyFov=0;}
    if(spectatorHealthyCamera&&player.enabled&&player.Health&&player.Health.Health>0&&(int)player._aimState<2)spectatorHealthyFov=spectatorHealthyCamera!.fieldOfView;
   }
   return;
  }
  if(spectatingBody&&spectatingBody!=player)StopSpectator();
  spectatorTargets.Clear();var now=Time.unscaledTime;
  foreach(var remote in remotes.Values)if(PeerMatches(remote)&&now-remote.LastSeen<=2&&remote.State!.Health>0&&!downPlayers.Contains(remote.Id))spectatorTargets.Add(remote);
  spectatorTargets.Sort(static(a,b)=>a.Id.CompareTo(b.Id));
  // Keeping a camera from an old floor/peer made the view freeze for seconds.
  // A current matching state is required; an unavailable View uses third person.
  if(spectatorTargets.Count==0){if(spectatorCamera)StopSpectator();return;}
  if(!spectatorCamera)StartSpectator();if(!spectatorCamera)return;
  if(spectatorWorld!=world||spectatorFloor!=floor){ResetSpectatorView();SpectatedPlayer=-1;spectatorWorld=world;spectatorFloor=floor;}
  if(healthDepthOfField)healthDepthOfField!.active=false;if(damageCanvas)damageCanvas!.alpha=0;
  var index=0;for(var i=0;i<spectatorTargets.Count;i++)if(spectatorTargets[i].Id==SpectatedPlayer){index=i;break;}
  var sampled=spectatorSampleFrame==Time.frameCount;
  var left=sampled&&spectatorNextHeld;var right=sampled&&spectatorPreviousHeld;var space=sampled&&spectatorModeHeld;
  var inputAllowed=sampled&&InputBridge.Active&&!InputBridge.FocusGained&&!PanelInputBlocked&&!BlockNativeUi&&(!player||!player!.IsPauseMenuOpened);
  if(!inputAllowed){spectatorInputArmed=false;spectatorNextTap.Reset();spectatorPreviousTap.Reset();spectatorModeTap.Reset();}
  else if(!spectatorInputArmed){if(!left&&!right&&!space&&!spectatorNextDown&&!spectatorPreviousDown&&!spectatorModeDown)spectatorInputArmed=true;}
  else{
   var next=spectatorNextTap.Take(spectatorNextDown,left,Time.frameCount);var previous=spectatorPreviousTap.Take(spectatorPreviousDown,right,Time.frameCount);
   if(next)index=(index+1)%spectatorTargets.Count;
   else if(previous)index=(index+spectatorTargets.Count-1)%spectatorTargets.Count;
   if(spectatorModeTap.Take(spectatorModeDown,space,Time.frameCount)){
    SpectatorFirstPerson.Value=!SpectatorFirstPerson.Value;ResetSpectatorView();
    if(spectatorControlEvents++<24)GameplayLog.Record("spectator.mode",new{frame=Time.frameCount,firstPerson=SpectatorFirstPerson.Value});
   }
  }
  var target=spectatorTargets[index];if(target.Id!=SpectatedPlayer){ResetSpectatorView();SpectatedPlayer=target.Id;}
  var state=target.State!;var pose=state.Pose;
  var hasView=SpectatorView.TryRead(state.View,out var eye,out var rotation,out var fov);
  if(!hasView){eye=new Vector3(pose[0],pose[1]+Mathf.Clamp(pose[5]-.15f,.25f,1.8f),pose[2]);rotation=Quaternion.Euler(pose[4],pose[3],0);}
  var firstPerson=SpectatorFirstPerson.Value&&hasView;spectatorActualFirstPerson=firstPerson;
  UpdateSpectatorSurfaces(target,firstPerson);
  var position=eye;
  if(firstPerson){spectatorCamera!.fieldOfView=fov;spectatorCamera.nearClipPlane=Mathf.Min(spectatorBaseNear,.03f);}
  else{
   spectatorCamera!.fieldOfView=spectatorBaseFov;spectatorCamera.nearClipPlane=spectatorBaseNear;
   var distance=SpectatorDistance.Value>.05f?SpectatorDistance.Value:2.5f;
   var desired=eye-rotation*Vector3.forward*distance+Vector3.up*.35f;
   position=SpectatorView.ThirdPersonPosition(eye,desired,target.Root,spectatingBody?spectatingBody!.transform:null);
   rotation=Quaternion.LookRotation(eye+rotation*Vector3.forward*3-position);
  }
  // View is the sender's latest camera pose: no second avatar history buffer,
  // heavy camera damping, root-height guess or inherited ADS projection.
  spectatorCamera!.transform.SetPositionAndRotation(position,rotation);spectatorCamera.enabled=true;spectatorCamera.gameObject.SetActive(true);
 }
 void LateUpdate(){UnitLateTick();ProjectileLateTick();EnemyLateTick();foreach(var remote in remotes.Values)try{remote.LateRender();}catch(Exception error){GameplayLog.Fault("avatar_late_pose",error);}try{ShotLateTick();}catch(Exception error){GameplayLog.Fault("shot_late_fx",error);}try{SpectatorTick();}catch(Exception e){Plugin.Instance.Log.LogError("Spectator camera: "+e);StopSpectator();}}
 void DrawSpectatorHud(){
  if(!Down||PartyWiped||PanelVisualVisible)return;
  spectatorHudStyle??=new GUIStyle(GUI.skin.box){richText=false,wordWrap=true,alignment=TextAnchor.MiddleLeft};
  var name=SpectatedPlayer>=0?remotes.GetValueOrDefault(SpectatedPlayer)?.State?.Name:null;
  var mode=spectatorActualFirstPerson?"FIRST PERSON":"THIRD PERSON";
  var text=name!=null?$"SPECTATING: {name}  |  {mode}\nLMB: next teammate   RMB: previous   Space: first / third person\nRevive next floor: {Rules.ReviveHealthPercent}% HP":"Waiting for a living teammate's current world...\nLMB: next teammate   RMB: previous   Space: first / third person";
  if(name!=null&&SpectatorFirstPerson.Value&&!spectatorActualFirstPerson)text+="\nFirst-person camera unavailable; showing third person.";
  GUI.Box(new Rect(12,91,Mathf.Max(180,Mathf.Min(680,Screen.width-24)),SpectatorFirstPerson.Value&&!spectatorActualFirstPerson?91:73),text,spectatorHudStyle);
 }
}

readonly struct SpectatorSurfaceState
{
 internal readonly Renderer Renderer;internal readonly ShadowCastingMode Shadows;internal readonly bool Enabled;
 internal SpectatorSurfaceState(Renderer value){Renderer=value;Shadows=value.shadowCastingMode;Enabled=value.enabled;}
}

static class SpectatorView
{
 internal static Camera? CameraOf(Demo.Scripts.Runtime.Character.FPSController player){
  var anchor=player.Camera;
  if(anchor){var exact=anchor!.GetComponent<Camera>();if(exact)return exact;var child=anchor.GetComponentInChildren<Camera>(true);if(child)return child;}
  var main=Camera.main;return main&&main!.transform.IsChildOf(player.transform)?main:null;
 }
 internal static bool TryRead(float[]? view,out Vector3 position,out Quaternion rotation,out float fov){
  position=default;rotation=Quaternion.identity;fov=0;if(view==null||view.Length!=8)return false;
  foreach(var value in view)if(!float.IsFinite(value))return false;
  if(view[7]<10||view[7]>140)return false;
  var norm=view[3]*view[3]+view[4]*view[4]+view[5]*view[5]+view[6]*view[6];if(norm<.25f||norm>2.25f)return false;
  var inverse=1/Mathf.Sqrt(norm);position=new Vector3(view[0],view[1],view[2]);rotation=new Quaternion(view[3]*inverse,view[4]*inverse,view[5]*inverse,view[6]*inverse);fov=view[7];return true;
 }
 internal static bool MouseHeld(int button){
  var mouse=Mouse.current;var held=button==0?mouse?.leftButton.isPressed==true:mouse?.rightButton.isPressed==true;
  if(InputBridge.Legacy)try{held|=Input.GetMouseButton(button);}catch(InvalidOperationException){InputBridge.Legacy=false;}return held;
 }
 internal static bool MouseDown(int button){
  var mouse=Mouse.current;var down=button==0?mouse?.leftButton.wasPressedThisFrame==true:mouse?.rightButton.wasPressedThisFrame==true;
  if(InputBridge.Legacy)try{down|=Input.GetMouseButtonDown(button);}catch(InvalidOperationException){InputBridge.Legacy=false;}return down;
 }
 internal static Vector3 ThirdPersonPosition(Vector3 eye,Vector3 desired,GameObject? selected,Transform? local){
  var delta=desired-eye;var distance=delta.magnitude;if(distance<.01f)return desired;var direction=delta/distance;var from=eye;var travelled=0f;
  for(var attempt=0;attempt<8;attempt++){
   if(!Physics.Raycast(from,direction,out var hit,distance-travelled,~0,QueryTriggerInteraction.Ignore))return desired;
   var collider=hit.collider;var transform=collider?collider.transform:null;
   if(transform&&(selected&&transform!.IsChildOf(selected!.transform)||local&&transform!.IsChildOf(local!))){
    // Skip only this actor's bounds, not its whole physics layer. The remaining
    // ray still checks world walls; no global physics setting is changed.
    var bounds=collider!.bounds;var min=bounds.min;var max=bounds.max;var exit=float.PositiveInfinity;
    for(var axis=0;axis<3;axis++){var component=direction[axis];if(Mathf.Abs(component)<.00001f)continue;var limit=component>0?max[axis]:min[axis];exit=Mathf.Min(exit,(limit-from[axis])/component);}
    var step=Mathf.Max(hit.distance+.02f,exit+.02f);if(!float.IsFinite(step)||step<=0)return eye;
    travelled+=step;if(travelled>=distance)return desired;from=eye+direction*travelled;continue;
   }
   return eye+direction*Mathf.Max(.08f,travelled+hit.distance-.15f);
  }
  return eye+direction*Mathf.Max(.08f,travelled-.15f);
 }
}
