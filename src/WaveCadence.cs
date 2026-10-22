using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using System.Reflection;
using UnityEngine;

namespace FractureTogether;

internal static class WaveCadence
{
 internal static int Players {
  get {var c=Together.Current;return c?.Connected==true&&c.Network?.Hosting==true&&c.SharedRun?Math.Clamp(c.RunPlayers,1,4):1;}
 }
}

// RVA 9CA800 is called by both native sequence iterators. Only its argument is
// changed; the authored WaveData delay, counts and difficulty remain untouched.
[HarmonyPatch(typeof(DifficultyStopwatch),"CountdownCoroutine")]
static class PartyWaveCountdown
{
 static void Prefix(ref float duration)=>duration=WaveTiming.Interval(duration,WaveCadence.Players);
}

// SpawnWaveCoroutine's wrapper is inlined in the native sequence. Patch the two
// actual iterator bodies (9CE660 / 9CE010), which allocate a fresh WaitForSeconds
// from wave.spawnInterval on each spawn. Do not repeatedly divide WaveData.
[HarmonyPatch]
static class PartyWaveSpawnInterval
{
 static readonly Dictionary<Type,PropertyInfo> currentProperties=new();
 static IEnumerable<MethodBase> TargetMethods(){
  foreach(var type in typeof(DifficultyStopwatch).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)){
   if(!type.Name.Contains("SpawnWaveCoroutine")&&!type.Name.Contains("SpawnCustomWaveCoroutine"))continue;
   var current=type.GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).FirstOrDefault(p=>p.Name=="__2__current"||p.Name.Contains("2__current"));
   var method=type.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
   if(current==null||method==null)throw new MissingMemberException(type.FullName,"native iterator current/MoveNext");
   currentProperties[type]=current;yield return method;
  }
 }
 static void Postfix(object __instance,bool __result){
  var players=WaveCadence.Players;if(!__result||players<=1)return;
  if(!currentProperties.TryGetValue(__instance.GetType(),out var property))return;
  if(property.GetValue(__instance) is not Il2CppObjectBase current)return;
  var wait=current.TryCast<WaitForSeconds>();if(wait!=null)wait.m_Seconds=WaveTiming.Interval(wait.m_Seconds,players);
 }
}
