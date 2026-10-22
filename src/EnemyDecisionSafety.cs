using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace FractureTogether;

// Decision caches physics results for 0.2 seconds. A collider can be destroyed
// during that interval; native MyHandleBodys dereferences every cached entry.
[HarmonyPatch(typeof(LookDecision),"MyHandleBodys")]
static class EnemyBodyDetectionSafety
{
 static readonly Il2CppReferenceArray<Collider> Empty=new(Array.Empty<Collider>());
 internal static long FilteredColliders,InvalidControllers;

 static bool Prefix(StateController __0,ref bool __1,ref Il2CppReferenceArray<Collider> __2,ref bool __result)
 {
  if(!__0){InvalidControllers++;__result=false;return false;}
  if(!__1)return true;
  if(__2==null||__2.Length==0){__1=false;__2=Empty;return true;}
  var valid=0;for(var i=0;i<__2.Length;i++)if(__2[i])valid++;
  if(valid==__2.Length)return true;
  FilteredColliders+=__2.Length-valid;
  if(valid==0){__1=false;__2=Empty;return true;}
  var live=new Collider[valid];var next=0;for(var i=0;i<__2.Length;i++){var collider=__2[i];if(collider)live[next++]=collider;}
  __2=new Il2CppReferenceArray<Collider>(live);return true;
 }
}
