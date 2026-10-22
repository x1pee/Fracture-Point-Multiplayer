using Il2CppInterop.Runtime.Attributes;
using KINEMATION.FPSAnimationFramework.Runtime.Camera;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 [HideFromIl2Cpp]
 internal void ApplyProjectileFeedback(Grenade actor){
  if(!SharedRun||Network?.Hosting!=false||!actor||!player||Down||PartyWiped)return;
  // Native Grenade.Explode computes concussion independently of ownership and
  // HP damage. Guest explosion replay must preserve that sensory path as well.
  var distance=Vector3.Distance(actor.transform.position,player.transform.position);
  var effect=1-Mathf.InverseLerp(actor.minDistance,actor.maxDistance,distance);
  if(float.IsFinite(effect)&&effect>0)Object.FindObjectOfType<ConcussionManager>()?.AddEffect(effect);
  if(actor.FPSCameraShake){var camera=Object.FindObjectOfType<FPSCameraController>();if(camera){var shake=actor.GetShakeByDistance(player.transform);if(shake)camera.PlayCameraShake(shake);}}
 }
}
