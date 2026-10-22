using HarmonyLib;
namespace FractureTogether;

// A disconnected player waits for local storage before leaving the raid.
// This guard exists only during that automatic recovery, with input disabled.
// There is no invulnerability option or configurable gameplay damage bypass.
[HarmonyPatch(typeof(PlayerHealthManager),nameof(PlayerHealthManager.Damage))]
static class TravelAndAllyGrenadeDamage {
 static bool Prefix()=>Together.Current?.Recovering!=true&&(!Together.ProjectileOwner.HasValue||ProjectileRules.HurtsPlayer(Together.ProjectileOwner.Value,Together.Current?.Network?.LocalId??0));
}
[HarmonyPatch(typeof(PlayerHealthManager),nameof(PlayerHealthManager.Die))]
static class TravelDeathProtection {
 static bool Prefix(PlayerHealthManager __instance){if(Together.Current?.Recovering!=true)return true;__instance.died=false;if(__instance.Health<=0)__instance.Health=1;return false;}
}
