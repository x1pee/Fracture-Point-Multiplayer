using HarmonyLib;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
namespace FractureTogether;

static class SpawnRandom
{
 internal sealed class Saved {public UnityEngine.Random.State State;public Il2CppSystem.Random? SpawnerRandom;}
 static readonly Dictionary<string,int> attempts=new();static string epoch="";
 static int Seed(Component c){
  var t=Together.Current!;var e=t.Network!.Seed+"|"+UnityEngine.SceneManagement.SceneManager.GetActiveScene().name+"|"+DungeonSeedPatch.BuiltFloor;
  if(e!=epoch){epoch=e;attempts.Clear();}
  var key=c.GetIl2CppType().FullName+"|"+Together.SpatialKey(c.transform);var index=attempts.GetValueOrDefault(key);attempts[key]=index+1;
  return BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(e+"|"+key+"|"+index)),0)&int.MaxValue;
 }
 internal static Saved? Begin(Component c){if(Together.Current?.Connected!=true)return null;var state=new Saved{State=UnityEngine.Random.state};var seed=Seed(c);UnityEngine.Random.InitState(seed);var spawner=c.TryCast<EnemySpawner>();if(spawner){state.SpawnerRandom=spawner.random;spawner.random=new Il2CppSystem.Random(seed);}return state;}
 internal static Exception? End(Component c,Saved? state,Exception? error){if(state!=null){UnityEngine.Random.state=state.State;var spawner=c.TryCast<EnemySpawner>();if(spawner)spawner.random=state.SpawnerRandom;}return error;}
}
[HarmonyPatch(typeof(LootSpawnPoint),nameof(LootSpawnPoint.OnEnable))]
static class SharedLootRandom {static void Prefix(LootSpawnPoint __instance,out SpawnRandom.Saved? __state)=>__state=SpawnRandom.Begin(__instance);static Exception? Finalizer(LootSpawnPoint __instance,SpawnRandom.Saved? __state,Exception? __exception)=>SpawnRandom.End(__instance,__state,__exception);}
[HarmonyPatch(typeof(EnemySpawner),nameof(EnemySpawner.Spawn))]
static class SharedEnemyRandom {static void Prefix(EnemySpawner __instance,out SpawnRandom.Saved? __state)=>__state=SpawnRandom.Begin(__instance);static Exception? Finalizer(EnemySpawner __instance,SpawnRandom.Saved? __state,Exception? __exception)=>SpawnRandom.End(__instance,__state,__exception);}
