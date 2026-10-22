using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed class RemoteAvatar:IDisposable
{
 public readonly int Id;
 public Packet? State;
 public OutfitEntry[] Appearance=Array.Empty<OutfitEntry>();
 public float LastSeen;
 public GameObject? Root;
 Transform? aim;
 AvatarVisual? visual;float visualRetry;Vector3 priorPosition;bool hadPosition;
 struct MotionSample {public Vector3 Position;public Quaternion Rotation;public float At;public int Motion;public long Sequence;}
 readonly MotionSample[] motionHistory=new MotionSample[4];int motionCount;Packet? sampledState;
 float motionInterval=.05f,recoveryStartedAt=-1;Vector3 recoveryOffset;
 float hitHeight=-1,hitCenter=float.NaN;CharacterController? ignoredController;bool ignoredControllerEnabled;
 public bool HasCharacterModel=>visual?.Root;
 public Ballistics.LivingEntity? HealthProxy;
 HealthManager? aiHealth;
 CapsuleCollider? remoteCollider;IntPtr ignoredLocalBody;bool collisionWasActive;
 DefaultBallisticObject? ballisticSurface;IntPtr ballisticLocalBody,ballisticAttemptBody;float ballisticRetryAt;
 readonly PlayerTagUi.TextCache tagText=new();
 public Transform? AimTarget=>aim;
 public bool Visible=>Root&&Root!.activeSelf;
 public RemoteAvatar(int id){Id=id;}
 public void Render(bool visible){
  if(aiHealth)aiHealth.dead=!visible||State?.Health<=0;
  if(!visible||State==null){if(hadPosition)visual?.ResetAnimationEvents();collisionWasActive=false;hadPosition=false;motionCount=0;sampledState=null;recoveryOffset=Vector3.zero;if(Root)Root!.SetActive(false);return;}
  if(!Root)Create();
  var outfit=Appearance.Length>0?Appearance:State.Outfit;
  // Wait for the peer's explicit clothing flags. Cloning earlier briefly
  // exposes this computer's default active headgear on the other player.
  if(visual==null&&outfit.Length>0&&Time.unscaledTime>=visualRetry){visualRetry=Time.unscaledTime+2;try{visual=AvatarVisual.Create(Root!.transform,outfit);}catch(Exception e){Plugin.Instance.Log.LogWarning("Character visual copy failed: "+e.ToString());}}
  visual?.ApplyOutfit(outfit);
  visual?.ApplyWeapon(State.WeaponId);
  var p=State.Pose;UpdateMotion();SampleMotion(out var target,out var rotation);
  if(Time.unscaledTime>recoveryStartedAt)recoveryOffset*=Mathf.Exp(-22*Mathf.Clamp(Time.unscaledDeltaTime,0,.1f));
  Root!.transform.position=target+recoveryOffset;Root.SetActive(true);Root.transform.rotation=rotation;
  if(HealthProxy)HealthProxy.gameObject.SetActive(Together.Current?.SharedRun==true&&EnsureBallisticSurface());
  IgnoreLocalBody();
  var nativeHeight=Mathf.Clamp(p[5],.4f,2.2f);var center=p.Length>6?Mathf.Clamp(p[6],-2.2f,2.2f):nativeHeight*.5f;
  var height=Mathf.Max(.65f,nativeHeight);Root.transform.localScale=Vector3.one;
  if(remoteCollider){if(hitHeight!=nativeHeight){remoteCollider!.height=nativeHeight;hitHeight=nativeHeight;}if(hitCenter!=center){remoteCollider!.center=new Vector3(0,center,0);hitCenter=center;}}
  aim!.localPosition=new Vector3(0,center+nativeHeight*.26f,0);aim.localRotation=Quaternion.Slerp(aim.localRotation,Quaternion.Euler(p[4],0,0),1-Mathf.Exp(-22*Time.unscaledDeltaTime));
  var velocity=hadPosition?(Root.transform.position-priorPosition)/Math.Max(.001f,Time.unscaledDeltaTime):Vector3.zero;if(velocity.sqrMagnitude>225)velocity=Vector3.zero;
  visual?.Move(Root.transform.InverseTransformDirection(velocity),height,State.Stance,State.Motion);priorPosition=Root.transform.position;hadPosition=true;
 }
 void UpdateMotion(){
  var state=State!;if(ReferenceEquals(sampledState,state))return;
  var now=Time.unscaledTime;var p=state.Pose;var position=new Vector3(p[0],p[1],p[2]);
  var changedWorld=sampledState!=null&&(sampledState.World!=state.World||sampledState.Scene!=state.Scene||sampledState.Floor!=state.Floor);
  var teleported=hadPosition&&(Root!.transform.position-position).sqrMagnitude>225;
  if(changedWorld)visual?.ResetAnimationEvents();
  if(changedWorld||teleported){motionCount=0;hadPosition=false;recoveryOffset=Vector3.zero;motionInterval=.05f;}
  var at=LastSeen>0&&LastSeen<=now?LastSeen:now;
  if(motionCount>0){
   var last=motionHistory[motionCount-1];var gap=at-last.At;
   // Receive already rejects old sequences. Coalesced snapshots may skip
   // sequence numbers; estimate one send interval rather than that whole gap.
   var steps=state.Sequence>last.Sequence?state.Sequence-last.Sequence:1;
   if(gap>0&&gap<.5f)motionInterval=Mathf.Lerp(motionInterval,Mathf.Clamp(gap/steps,.04f,.075f),.15f);
   if(gap>.5f){recoveryOffset=Root!.transform.position-position;recoveryStartedAt=now;motionCount=0;}
   else if(at<=last.At){motionCount--;at=last.At;}
  }
  if(motionCount==motionHistory.Length){for(var i=1;i<motionCount;i++)motionHistory[i-1]=motionHistory[i];motionCount--;}
  motionHistory[motionCount++]=new MotionSample{Position=position,Rotation=Quaternion.Euler(0,p[3],0),At=at,Motion=state.Motion,Sequence=state.Sequence};sampledState=state;
 }
 void SampleMotion(out Vector3 position,out Quaternion rotation){
  var latest=motionHistory[motionCount-1];position=latest.Position;rotation=latest.Rotation;
  if(motionCount<2)return;
  // One observed send interval (40–75 ms) removes the stop/go pursuit of each
  // 20 Hz packet. It replaces, rather than adds to, the old exponential lag.
  var at=Time.unscaledTime-motionInterval;
  if(at<=motionHistory[0].At){position=motionHistory[0].Position;rotation=motionHistory[0].Rotation;return;}
  for(var i=1;i<motionCount;i++){
   var next=motionHistory[i];if(at>next.At)continue;var previous=motionHistory[i-1];var span=next.At-previous.At;
   var blend=span>.001f?Mathf.Clamp01((at-previous.At)/span):1;
   position=Vector3.Lerp(previous.Position,next.Position,blend);rotation=Quaternion.Slerp(previous.Rotation,next.Rotation,blend);return;
  }
  // Briefly bridge a late packet, then hold. An explicit stop is never
  // extrapolated, and a disconnected peer cannot drift indefinitely.
  if(latest.Motion==0)return;
  var prior=motionHistory[motionCount-2];var elapsed=latest.At-prior.At;if(elapsed<=.001f)return;
  var velocity=Vector3.ClampMagnitude((latest.Position-prior.Position)/elapsed,15);
  position+=velocity*Mathf.Clamp(at-latest.At,0,.06f);
 }
 public bool TryGetShotPose(out Vector3 position,out Vector3 forward){
  position=default;forward=default;
  // A pending model retains the authenticated transmitted shot origin.
  // The perception AimTarget is above the body, not a firearm muzzle.
  return Visible&&visual!=null&&visual.TryGetShotPose(out position,out forward);
 }
 public void NotifyShot(int weaponId,long sequence){
  if(!Visible||State==null||State.WeaponId!=weaponId)return;
  visual?.NotifyShot(weaponId,sequence,State.AimState);
 }
 void Create(){
  collisionWasActive=false;hitHeight=-1;hitCenter=float.NaN;ballisticRetryAt=0;ballisticAttemptBody=IntPtr.Zero;
  Root=new GameObject("FractureTogether.Player."+Id);Object.DontDestroyOnLoad(Root);
  // Native perception calls aimTarget.root.GetComponent<HealthManager>().dead.
  // The base component has no AI or save lifecycle; omitting it throws in native AI.
  aiHealth=Root.AddComponent<HealthManager>();aiHealth.dead=State?.Health<=0;
  // Visuals use the game's own body, outfit and material assets, avoiding the
  // primitive fallback and its guessed shader/material compatibility.
  aim=new GameObject("AimTarget").transform;aim.SetParent(Root.transform,false);aim.localPosition=new Vector3(0,1.4f,0);
  var hitbox=new GameObject("FractureTogether.Hitbox."+Id);hitbox.SetActive(false);hitbox.transform.SetParent(Root.transform,false);
  var local=Object.FindObjectOfType<Demo.Scripts.Runtime.Character.FPSController>();var localCollider=local?local.GetComponentInChildren<Ballistics.LivingEntityCollider>():null;hitbox.layer=localCollider?localCollider.gameObject.layer:0;
  var capsule=hitbox.AddComponent<CapsuleCollider>();remoteCollider=capsule;capsule.center=new Vector3(0,.85f,0);capsule.height=1.7f;capsule.radius=.20f;
  var rigid=hitbox.AddComponent<Rigidbody>();rigid.isKinematic=true;rigid.useGravity=false;
  HealthProxy=hitbox.AddComponent<Ballistics.LivingEntity>();HealthProxy.StartHealth=1000000;HealthProxy.myHealth=1000000;
  var entityCollider=hitbox.AddComponent<Ballistics.LivingEntityCollider>();entityCollider.ParentLivingEntity=HealthProxy;entityCollider.DamageMultiplier=1;
  // Keep this collider inactive until the native player's physical material is
  // available. A default/wall MatID would change bullet penetration semantics.
  EnsureBallisticSurface();
 }
 bool EnsureBallisticSurface(){
  var local=Together.Current?.LocalController;if(!remoteCollider||!local)return false;
  if(ballisticSurface&&ballisticLocalBody==local!.Pointer)return true;
  if(ballisticAttemptBody==local!.Pointer&&Time.unscaledTime<ballisticRetryAt)return false;
  ballisticAttemptBody=local.Pointer;ballisticRetryAt=Time.unscaledTime+1;
  Ballistics.BallisticObject? native=null;
  foreach(var part in local.GetComponentsInChildren<Ballistics.LivingEntityCollider>(true)){
   if(!part||!part.GetComponent<Collider>())continue;
   var candidate=part.GetComponent<Ballistics.BallisticObject>();if(candidate){native=candidate;break;}
  }
  if(!native)return false;
  // Native UpdateBullets first asks the hit transform for BallisticObject,
  // then for LivingEntityCollider. Both components must live on this capsule GO.
  ballisticSurface=remoteCollider!.GetComponent<DefaultBallisticObject>()??remoteCollider.gameObject.AddComponent<DefaultBallisticObject>();
  ballisticSurface!.MatID=native.MatID;ballisticSurface.isStatic=false;ballisticLocalBody=local.Pointer;return true;
 }
 void IgnoreLocalBody(){
  // The local body is replaced on scene changes; the remote avatar survives them.
  var local=Together.Current?.LocalController;
  if(!remoteCollider||!remoteCollider.gameObject.activeInHierarchy){collisionWasActive=false;return;}
  if(!local)return;
  if(ignoredLocalBody!=local!.Pointer){ignoredController=local.GetComponent<CharacterController>();ignoredControllerEnabled=false;}
  var controllerEnabled=ignoredController&&ignoredController!.enabled;var reenabled=controllerEnabled&&!ignoredControllerEnabled;ignoredControllerEnabled=controllerEnabled;
  if(collisionWasActive&&ignoredLocalBody==local.Pointer&&!reenabled)return;
  foreach(var collider in local.GetComponentsInChildren<Collider>())Physics.IgnoreCollision(remoteCollider,collider,true);
  ignoredLocalBody=local.Pointer;collisionWasActive=true;
 }
 public void LateRender(){if(Visible&&State!=null)visual?.LatePose(State.Pose[4],State.AimState,State.ThrowSequence,State.ReloadSequence,State.ReloadKind,State.ReloadElapsed);}
 public void DrawLabel(){
  if(Event.current?.type!=EventType.Repaint||!Visible||State==null)return;
  var root=Root!.transform;
  // Friendly tags intentionally remain visible through level geometry. World,
  // readiness and actor visibility are still controlled by Render's caller.
  if(!PlayerTagUi.TryProject(State,root.position,out var projected))return;
  PlayerTagUi.Draw(State,in projected,tagText);
 }
 public void Dispose(){
  if(Root){
   // Native perception dereferences aimTarget.root without Unity's destroyed-object
   // check. Detach AI targets before removing the remote player's transform.
   var local=Object.FindObjectOfType<Demo.Scripts.Runtime.Character.FPSController>();
   foreach(var controller in Object.FindObjectsOfType<EnemyAI.StateController>())if(controller.aimTarget&&controller.aimTarget.IsChildOf(Root.transform)){
    controller.aimTarget=local?(local.Camera?local.Camera:local.transform):controller.transform;controller.targetHealth=null;controller.noEnemiesAlive=true;
   }
   Object.Destroy(Root);
  }
 }
}

