using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

public sealed partial class Together
{
 Camera? transmittedCamera;IntPtr transmittedCameraOwner;
 [HideFromIl2Cpp]
 float[] CaptureLocalView(){
  if(!player||Down||PartyWiped)return Array.Empty<float>();
  if(transmittedCameraOwner!=player!.Pointer||!transmittedCamera){
   transmittedCameraOwner=player.Pointer;var socket=player.Camera;
   transmittedCamera=socket?(socket.GetComponent<Camera>()??socket.GetComponentInChildren<Camera>()):null;
   if(!transmittedCamera)transmittedCamera=Camera.main;
  }
  if(!transmittedCamera||transmittedCamera==spectatorCamera)return Array.Empty<float>();
  var position=transmittedCamera!.transform.position;var rotation=transmittedCamera.transform.rotation;
  return new[]{position.x,position.y,position.z,rotation.x,rotation.y,rotation.z,rotation.w,Mathf.Clamp(transmittedCamera.fieldOfView,10,140)};
 }
}
