using HarmonyLib;
using UnityEngine;
namespace FractureTogether;

// BulletHandler uses Physics.Raycast's global trigger option. Authored gameplay
// volumes have no BallisticObject and are not physical bullet surfaces.
[HarmonyPatch(typeof(Ballistics.BulletHandler),"UpdateBullets")]
static class NativeBallisticQueries
{
 [HarmonyPriority(Priority.First)]
 static bool Prefix(Ballistics.BulletHandler __instance,out bool __state){__state=Physics.queriesHitTriggers;Physics.queriesHitTriggers=false;return NativeBulletSafety.Prepare(__instance);}
 [HarmonyPriority(Priority.Last)]
 static Exception? Finalizer(bool __state,Exception? __exception){Physics.queriesHitTriggers=__state;return __exception;}
}
