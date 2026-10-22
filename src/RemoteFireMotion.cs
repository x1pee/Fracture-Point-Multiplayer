using KINEMATION.FPSAnimationFramework.Runtime.Recoil;
using UnityEngine;
namespace FractureTogether;

// Read/copy the catalogue's authored recoil once. Native Weapon.OnFire plays
// RecoilAnimation (8AE321), rather than its unused fireClip field. This private
// visual approximation uses a deterministic independent sample of those ranges
// and semi-shot curves; it does not create/call a native weapon/recoil component.
sealed class RemoteFireMotion
{
 // Shared only through RemoteShotAssets.Asset, never by a recycled native pointer.
 // This profile contains copied values; no avatar state or mutable native curves.
 internal sealed class Profile {
  internal readonly float Rate,Length;
  readonly float[] curves;
  internal readonly Vector2 Pitch,KickRight,KickUp,Kickback;internal readonly Vector4 Yaw,Roll;
  internal readonly Vector3 AimRotation,AimLocation,HipPivot,AimPivot;
  internal Profile(RecoilAnimData data,float duration,float[] samples){
   Rate=data.playRate;Length=duration;curves=samples;
   Pitch=data.pitch;Yaw=data.yaw;Roll=data.roll;KickRight=data.kickRight;KickUp=data.kickUp;Kickback=data.kickback;
   AimRotation=Safe(data.aimRot,4);AimLocation=Safe(data.aimLoc,4);
   HipPivot=Safe(data.hipPivotOffset,1);AimPivot=Safe(data.aimPivotOffset,1);
  }
  internal float Value(int axis,float time)=>RemoteFireMotionPolicy.Curve(curves,axis,time,Length);
 }
 readonly int weaponId;readonly RemoteShotAssets.Asset asset;Profile? profile;
 Vector3 rotationTarget,locationTarget;float started=-1;long sequence;
 RemoteFireMotion(int id,RemoteShotAssets.Asset source){weaponId=id;asset=source;profile=ResolveProfile(source);}
 internal static RemoteFireMotion? Create(RemoteShotAssets.Asset asset,int id)=>new RemoteFireMotion(id,asset);
 static Profile? ResolveProfile(RemoteShotAssets.Asset asset){
  if(asset.FireMotionProfile!=null)return asset.FireMotionProfile;
  var now=Time.unscaledTime;if(now<asset.FireMotionRetryAt)return null;
  // A missing/invalid native reference must not poison this Asset forever.
  // One shared retry per two seconds; successful steady frames do no lookup.
  asset.FireMotionRetryAt=now+2;
  try{return asset.FireMotionProfile=BakeProfile(asset);}
  catch(Exception error){GameplayLog.Fault("avatar.fire_motion",error);return null;}
 }
 static Profile? BakeProfile(RemoteShotAssets.Asset asset){
  var firearm=asset.Weapon;var data=firearm?firearm!.recoilData:null;if(!data)return null;
  var rate=data!.playRate;if(!float.IsFinite(rate)||rate<=0||rate>20)return null;
  var authored=data.recoilCurves;var rotation=authored.semiRotCurve;var location=authored.semiLocCurve;
  if(!rotation.IsValid()||!location.IsValid())return null;
  var length=Mathf.Max(rotation.GetCurveLength(),location.GetCurveLength());
  if(!float.IsFinite(length)||length<=0||length>RemoteFireMotionPolicy.MaximumSeconds||length/rate>RemoteFireMotionPolicy.MaximumSeconds)return null;
  const int stride=RemoteFireMotionPolicy.CurveSteps+1;var values=new float[6*stride];
  for(var i=0;i<=RemoteFireMotionPolicy.CurveSteps;i++){
   var time=length*i/RemoteFireMotionPolicy.CurveSteps;var r=rotation.GetValue(time);var l=location.GetValue(time);
   if(!Finite(r)||!Finite(l))return null;
   values[i]=RemoteFireMotionPolicy.Bound(r.x,4);values[stride+i]=RemoteFireMotionPolicy.Bound(r.y,4);values[2*stride+i]=RemoteFireMotionPolicy.Bound(r.z,4);
   values[3*stride+i]=RemoteFireMotionPolicy.Bound(l.x,4);values[4*stride+i]=RemoteFireMotionPolicy.Bound(l.y,4);values[5*stride+i]=RemoteFireMotionPolicy.Bound(l.z,4);
  }
  // Authored semi curves return to zero. Reject a foreign/non-recovering
  // curve instead of retaining an offset or manufacturing a recovery tail.
  for(var axis=0;axis<6;axis++)if(Mathf.Abs(values[axis*stride+RemoteFireMotionPolicy.CurveSteps])>.001f)return null;
  return new Profile(data,length,values);
 }
 internal bool Notify(long next,int aimState){
  // Accepted-shot callbacks only read the shared managed result, never bake.
  profile??=asset.FireMotionProfile;var data=profile;if(data==null)return false;
  var now=Time.unscaledTime;if(!RemoteFireMotionPolicy.Accept(next,sequence,now))return false;
  sequence=next;started=now;
  float Sample(Vector2 range,int lane,float maximum)=>RemoteFireMotionPolicy.Range(range.x,range.y,RemoteFireMotionPolicy.Unit(next,weaponId,lane),maximum);
  float Signed(Vector4 range,int lane){
   var pair=RemoteFireMotionPolicy.Unit(next,weaponId,lane)<.5f?new Vector2(range.x,range.y):new Vector2(range.z,range.w);
   return Sample(pair,lane+1,RemoteFireMotionPolicy.MaximumRotation);
  }
  rotationTarget=new Vector3(Sample(data.Pitch,0,90),Signed(data.Yaw,1),Signed(data.Roll,3));
  locationTarget=new Vector3(Sample(data.KickRight,5,1),Sample(data.KickUp,6,1),Sample(data.Kickback,7,1));
  // Native CalculateTargetData E339B7..E33A56 snapshots isAiming per shot.
  if(aimState>=2){rotationTarget=Safe(Vector3.Scale(rotationTarget,data.AimRotation),90);locationTarget=Safe(Vector3.Scale(locationTarget,data.AimLocation),1);}
  return true;
 }
 internal void Apply(ref Vector3 position,ref Quaternion rotation,float aimingWeight){
  // An already-created private rig can recover from temporarily missing data.
  // The field-only backoff is shared by avatars using this catalogue Asset.
  profile??=ResolveProfile(asset);var data=profile;if(data==null||started<0)return;
  if(!RemoteFireMotionPolicy.TimeAt(Time.unscaledTime-started,data.Rate,data.Length,out var time)){started=-1;return;}
  float Value(int axis)=>data.Value(axis,time);
  var angles=Safe(Vector3.Scale(rotationTarget,new Vector3(Value(0),Value(1),Value(2))),90);
  var translation=Safe(Vector3.Scale(locationTarget,new Vector3(Value(3),Value(4),Value(5))),1);
  var delta=Quaternion.Euler(angles);var pivot=Vector3.Lerp(data.HipPivot,data.AimPivot,Mathf.Clamp01(RemoteFireMotionPolicy.Bound(aimingWeight,1)));
  // Native AdditiveLayer E50522..E50645: move by (R*pivot-pivot)+OutLoc
  // in the current weapon space, then rotate that same IK weapon anchor.
  var offset=Vector3.ClampMagnitude(delta*pivot-pivot+translation,RemoteFireMotionPolicy.MaximumTranslation);
  position+=rotation*offset;rotation*=delta;
 }
 internal void Reset(){started=-1;rotationTarget=locationTarget=Vector3.zero;}
 static bool Finite(Vector3 value)=>float.IsFinite(value.x)&&float.IsFinite(value.y)&&float.IsFinite(value.z);
 static Vector3 Safe(Vector3 value,float limit)=>new(RemoteFireMotionPolicy.Bound(value.x,limit),RemoteFireMotionPolicy.Bound(value.y,limit),RemoteFireMotionPolicy.Bound(value.z,limit));
}
