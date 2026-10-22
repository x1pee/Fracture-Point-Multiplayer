using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace FractureTogether;

// Native OnEnable (0x98D6E0) uses only GetLoot(1)[0], then instantiates its
// catalog Prefab without a null check. Authored MagData Index 13 (SP-320)
// has no Prefab, although the Mags pool includes it. Reject the existing
// selection, never redraw it or change the shared ScriptableObject pool.
static class LootSpawnSafety
{
 [ThreadStatic] internal static LootSpawnPoint? Current;
 internal static long InvalidSelections;

 internal static void Validate(SpawnableLoot pool,int maxItems,Il2CppSystem.Collections.Generic.List<LootDrop>? selection)
 {
  var point=Current;
  if(!point||!pool||maxItems!=1||selection==null||selection.Count==0)return;
  var pointPool=point.loot;
  if(!pointPool||pointPool.Pointer!=pool.Pointer)return;
  var drop=selection[0];
  var reason=drop==null?"selected LootDrop is null":MissingPrefab(point,drop);
  if(reason==null)return;

  // GetLoot allocates a fresh result List at 0x98DD57. OnEnable's original
  // Count==0 branch returns normally. No catalog/pool entries are removed.
  selection.Clear();
  InvalidSelections++;
  if(InvalidSelections<=8)
   Plugin.Instance.Log.LogWarning("Skipped invalid native loot selection: scene="+point.gameObject.scene.name+
    ", point="+Together.SpatialKey(point.transform)+", pool="+pool.name+
    ", type="+(drop==null?"null":drop.dropType.ToString())+", index="+(drop==null?"null":drop.ItemIndex.ToString())+
    ", missing="+reason+". Selection consumed without reroll.");
 }

 static string? MissingPrefab(LootSpawnPoint point,LootDrop drop)
 {
  switch(drop.dropType)
  {
   case LootDrop.DropType.Dollars:
   case LootDrop.DropType.Roubles:return point.Wallet?null:"LootSpawnPoint.Wallet";
   case LootDrop.DropType.Grenade:return point.Grenade?null:"LootSpawnPoint.Grenade";
  }
  // This is the same active Inventory lookup used by native OnEnable.
  var inventory=UnityEngine.Object.FindAnyObjectByType<Inventory>();
  if(!inventory)return "active Inventory";
  switch(drop.dropType)
  {
   case LootDrop.DropType.Item:
    return inventory.LootData?Find(inventory.LootData.Loot,drop.ItemIndex,x=>x.Id,x=>x.Prefab,"Inventory.LootData.Loot"):"Inventory.LootData";
   case LootDrop.DropType.Ammo:
    return inventory.AmmoData?Find(inventory.AmmoData.Ammo,drop.ItemIndex,x=>x.Index,x=>x.Prefab,"Inventory.AmmoData.Ammo"):"Inventory.AmmoData";
   case LootDrop.DropType.Heal:
    return inventory.HealthData?Find(inventory.HealthData.HealthItems,drop.ItemIndex,x=>x.Id,x=>x.Prefab,"Inventory.HealthData.HealthItems"):"Inventory.HealthData";
   case LootDrop.DropType.Cloth:
    return inventory.ArmorData?Find(inventory.ArmorData.Armor,drop.ItemIndex,x=>x.Id,x=>x.Prefab,"Inventory.ArmorData.Armor"):"Inventory.ArmorData";
   case LootDrop.DropType.Weapon:
    return inventory.WeaponData?Find(inventory.WeaponData.Weapons,drop.ItemIndex,x=>x.Id,x=>x.Prefab,"Inventory.WeaponData.Weapons"):"Inventory.WeaponData";
   case LootDrop.DropType.Mag:
    return inventory.MagData?Find(inventory.MagData.Mags,drop.ItemIndex,x=>x.Index,x=>x.Prefab,"Inventory.MagData.Mags"):"Inventory.MagData";
   default:return "unsupported LootDrop.DropType "+(int)drop.dropType;
  }
 }

 static string? Find<T>(Il2CppSystem.Collections.Generic.List<T>? entries,int id,Func<T,int> itemId,Func<T,GameObject?> prefab,string field) where T:Il2CppObjectBase
 {
  if(entries==null)return field;
  for(var i=0;i<entries.Count;i++)
  {
   var entry=entries[i];
   // Native List.Find also dereferences entries in order; a null before the
   // requested entry cannot be passed safely to its predicate.
   if(entry==null)return field+"["+i+"]";
   if(itemId(entry)==id)return prefab(entry)?null:field+"[id="+id+"].Prefab";
  }
  return field+"[id="+id+"]";
 }
}

[HarmonyPatch(typeof(LootSpawnPoint),nameof(LootSpawnPoint.OnEnable))]
static class LootSpawnValidationScope
{
 [HarmonyPriority(Priority.First)]
 static void Prefix(LootSpawnPoint __instance,out LootSpawnPoint? __state){__state=LootSpawnSafety.Current;LootSpawnSafety.Current=__instance;}
 [HarmonyPriority(Priority.Last)]
 static Exception? Finalizer(LootSpawnPoint? __state,Exception? __exception){LootSpawnSafety.Current=__state;return __exception;}
}

[HarmonyPatch(typeof(SpawnableLoot),nameof(SpawnableLoot.GetLoot))]
static class LootSpawnSelectionValidation
{
 static void Postfix(SpawnableLoot __instance,int __0,Il2CppSystem.Collections.Generic.List<LootDrop>? __result)=>LootSpawnSafety.Validate(__instance,__0,__result);
}
