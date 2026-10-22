using Ballistics;
using EnemyAI;
using HarmonyLib;
using UnityEngine;

namespace FractureTogether;

// Identify the human boss by serialized identity, never by a shared soldier model name.
public static class BossHealthScaling
{
 static readonly Dictionary<int,EnemyHealth> enemies=new();
 static readonly Dictionary<int,LivingEntity> entities=new();
 [ThreadStatic] internal static bool Reconciling;
 [ThreadStatic] internal static IntPtr ReconciledHealth;
 public static bool IsVictor(string? name)=>string.Equals(name?.Trim(),"VICTOR",StringComparison.OrdinalIgnoreCase);
 public static int Multiplier(int players)=>Math.Clamp(players,1,4);
 static int PartyMultiplier()=>Together.Current is {Connected:true} current?Multiplier(current.RunPlayers):1;
 public static void Apply(EnemyHealth health){
  var entity=health.GetComponent<LivingEntity>();
  if(!entity||!entity.Boss||!IsVictor(entity.BossName))return;
  var factor=PartyMultiplier();if(factor==1)return;
  foreach(var id in enemies.Where(kv=>!kv.Value).Select(kv=>kv.Key).ToArray())enemies.Remove(id);
  if(!enemies.TryAdd(health.GetInstanceID(),health))return;
  health.health*=factor;
  // Awake copies health to totalHealth after this prefix.
  Plugin.Instance.Log.LogInfo($"Victor health: players={factor}, HP={health.health}");
 }
 public static void Apply(LivingEntity entity){
  if(!entity.Boss||!entity.EnemyHealth||!IsVictor(entity.BossName))return;
  var factor=PartyMultiplier();if(factor==1)return;
  foreach(var id in entities.Where(kv=>!kv.Value).Select(kv=>kv.Key).ToArray())entities.Remove(id);
  if(!entities.TryAdd(entity.GetInstanceID(),entity))return;
  // Native Awake initializes myHealth; OnEnable then initializes the boss HUD maximum.
  entity.StartHealth*=factor;
 }
 public static void Reconcile(EnemyHealth health){
  if(Reconciling||!health||health.totalHealth<=0||!float.IsFinite(health.health)||!float.IsFinite(health.totalHealth))return;
  var entity=health.GetComponent<LivingEntity>();
  if(!entity||!entity.Boss||!IsVictor(entity.BossName)||!float.IsFinite(entity.StartHealth)||entity.StartHealth<=0)return;
  // These are separate authored health domains (for example 800 and 840).
  // Preserve both maxima and mirror only the authoritative remaining fraction.
  var target=entity.StartHealth*(health.dead?0:Math.Clamp(health.health/health.totalHealth,0,1));
  var bar=entity._healthBar;if(!bar)return; // Native OnEnable must establish its HUD first.
  if(Math.Abs(entity.myHealth-target)<.001f&&Math.Abs(bar.currentHealth-target)<.001f)return;
  var applying=Together.ApplyingEnemyState;Reconciling=true;ReconciledHealth=health.Pointer;Together.ApplyingEnemyState=true;
  try{
   entity.Health=target;
   // OnHealthChanged updates the native HUD, but its TakeDamage callback is
   // suppressed only inside this scope. Correct any old stale bar absolutely.
   bar.SetHealthInstant(target);
  }finally{Together.ApplyingEnemyState=applying;ReconciledHealth=IntPtr.Zero;Reconciling=false;}
 }
}
[HarmonyPatch(typeof(EnemyHealth),"Awake")]
static class VictorEnemyHealthPatch {static void Prefix(EnemyHealth __instance)=>BossHealthScaling.Apply(__instance);}
[HarmonyPatch(typeof(LivingEntity),"Awake")]
static class VictorBossBarPatch {static void Prefix(LivingEntity __instance)=>BossHealthScaling.Apply(__instance);}
