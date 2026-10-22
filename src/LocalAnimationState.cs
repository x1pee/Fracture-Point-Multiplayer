using Demo.Scripts.Runtime.Character;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
namespace FractureTogether;

public sealed partial class Together
{
 long localThrowSequence;
 internal long CurrentThrowSequence=>localThrowSequence;
 [HideFromIl2Cpp]
 internal void ResetLocalAnimationState(){localThrowSequence=0;LocalReloadCapture.Reset();}
 [HideFromIl2Cpp]
 internal bool CanCaptureNativeThrow(FPSController controller)=>Connected&&SharedRun&&SameWorld&&!Down&&!PartyWiped&&!Recovering&&player&&controller&&player!.Pointer==controller.Pointer;
 [HideFromIl2Cpp]
 internal void CaptureNativeThrow(FPSController controller){if(CanCaptureNativeThrow(controller)&&localThrowSequence<long.MaxValue)localThrowSequence++;}
}

[HarmonyPatch(typeof(FPSController),nameof(FPSController.OnThrowGrenade))]
static class CaptureAcceptedGrenadeThrow
{
 static void Prefix(FPSController __instance,out bool __state)=>__state=Together.Current?.CanCaptureNativeThrow(__instance)==true&&__instance._actionState==FPSActionState.None;
 static void Postfix(FPSController __instance,bool __state,bool __runOriginal){
  // Native 8B2FEF writes PlayingAnimation only after OnGrenadeThrow accepted
  // and StartCoroutine succeeded. A skipped native body is never a new throw.
  if(__runOriginal&&__state&&__instance._actionState==FPSActionState.PlayingAnimation)Together.Current?.CaptureNativeThrow(__instance);
 }
}
