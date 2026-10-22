using HarmonyLib;
using System.Text.Json;
namespace FractureTogether;

// A premium belongs to locally acquired loot, not to the host's money or catalogue.
// Existing solo loot retains its ordinary price. Premiums survive leaving the party.
public static class LootEconomy
{
 public sealed class Lot {public int Count{get;set;} public int Factor{get;set;}}
 public sealed class Data {public Dictionary<int,List<Lot>> Inventory{get;set;}=new();public Dictionary<int,List<Lot>> Stash{get;set;}=new();}
 static Data data=new();static string file="";
 public static int TransferDepth;
 public static void Load(){
  file=Path.Combine(Plugin.Instance.Profile.DirectoryPath,"LootPremiums.json");
  if(File.Exists(file))data=JsonSerializer.Deserialize<Data>(File.ReadAllText(file))??throw new IOException("Invalid loot premium ledger");
  foreach(var lots in data.Inventory.Values.Concat(data.Stash.Values))if(lots.Any(l=>l.Count<=0||l.Factor<2||l.Factor>6))throw new IOException("Invalid loot premium entry");
 }
 static void Save(){
  var temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(data));
  if(File.Exists(file))File.Replace(temp,file,file+".previous",true);else File.Move(temp,file);
 }
 public static Dictionary<int,int> Snapshot(Inventory inv){var result=new Dictionary<int,int>();if(inv)foreach(var kv in inv.LootItems)result[kv.Key.Id]=kv.Value;return result;}
 public static int[] PreviewDropFactors(Inventory inv,int id,int count){
  count=Math.Clamp(count,0,10000);var result=Enumerable.Repeat(1,count).ToArray();var total=Snapshot(inv).GetValueOrDefault(id);
  if(!data.Inventory.TryGetValue(id,out var lots))return result;var index=Math.Min(count,Math.Max(0,total-lots.Sum(l=>l.Count)));
  foreach(var lot in lots)for(var n=0;n<lot.Count&&index<count;n++)result[index++]=lot.Factor;return result;
 }
 public static void RecordAcquired(Inventory inv,Dictionary<int,int> before,int factor){
  var changed=Trim(data.Inventory,before);if(factor<=1){if(changed)Save();return;}
  foreach(var kv in Snapshot(inv)){var delta=kv.Value-before.GetValueOrDefault(kv.Key);if(delta<=0)continue;Add(data.Inventory,kv.Key,delta,factor);changed=true;}
  if(changed)Save();
 }
 static bool Trim(Dictionary<int,List<Lot>> ledger,Dictionary<int,int> actual){
  var changed=false;foreach(var id in ledger.Keys.ToArray()){
   var lots=ledger[id];var excess=Math.Max(0,lots.Sum(l=>l.Count)-Math.Max(0,actual.GetValueOrDefault(id)));
   while(excess>0&&lots.Count>0){var lot=lots[^1];var take=Math.Min(excess,lot.Count);lot.Count-=take;excess-=take;changed=true;if(lot.Count==0)lots.RemoveAt(lots.Count-1);}
   if(lots.Count==0)ledger.Remove(id);
  }return changed;
 }
 static void Add(Dictionary<int,List<Lot>> ledger,int id,int count,int factor){
  if(count<=0||factor<=1)return;if(!ledger.TryGetValue(id,out var list)){list=new();ledger[id]=list;}
  var previous=list.LastOrDefault();if(previous?.Factor==factor)previous.Count+=count;else list.Add(new Lot{Count=count,Factor=factor});
 }
 static List<Lot> Take(Dictionary<int,List<Lot>> ledger,int id,int count,int totalBefore){
  var output=new List<Lot>();if(!ledger.TryGetValue(id,out var lots))return output;
  var premiumCount=lots.Sum(l=>l.Count);var normal=Math.Max(0,totalBefore-premiumCount);var remaining=Math.Max(0,count-normal);
  while(remaining>0&&lots.Count>0){var lot=lots[0];var n=Math.Min(lot.Count,remaining);output.Add(new Lot{Count=n,Factor=lot.Factor});lot.Count-=n;remaining-=n;if(lot.Count==0)lots.RemoveAt(0);}
  if(lots.Count==0)ledger.Remove(id);return output;
 }
 public static void Removed(Dictionary<int,int> before,Dictionary<int,int> after){
  var changed=Trim(data.Inventory,before);foreach(var kv in before){var n=kv.Value-after.GetValueOrDefault(kv.Key);if(n>0){Take(data.Inventory,kv.Key,n,kv.Value);changed=true;}}
  if(changed)Save();
 }
 public static int AdjustPrice(int ordinary,Inventory inv){
  if(!inv||ordinary<=0)return ordinary;double basis=0,extra=0;
  foreach(var kv in inv.LootItems){basis+=(double)kv.Key.price*kv.Value;if(!data.Inventory.TryGetValue(kv.Key.Id,out var lots))continue;
   var available=kv.Value;foreach(var lot in lots){var n=Math.Min(available,lot.Count);extra+=(double)kv.Key.price*n*(lot.Factor-1);available-=n;if(available<=0)break;}}
  return basis<=0?ordinary:(int)Math.Clamp(Math.Round(ordinary*(1+extra/basis)),0,int.MaxValue);
 }
 public static void Transfer(SaveBag bag,LootItem item,int before,int after,int stashBefore){
  var diff=after-before;if(diff==0)return;
  var from=diff<0?data.Inventory:data.Stash;var to=diff<0?data.Stash:data.Inventory;
  foreach(var lot in Take(from,item.Id,Math.Abs(diff),diff<0?before:stashBefore))Add(to,item.Id,lot.Count,lot.Factor);
  Save();
 }
}

[HarmonyPatch(typeof(LootSeller),"GetInventoryLootPrice")]
static class PremiumPricePatch {static void Postfix(LootSeller __instance,ref int __result)=>__result=LootEconomy.AdjustPrice(__result,__instance.Inventory);}
[HarmonyPatch(typeof(LootSeller),nameof(LootSeller.Interact))]
static class PremiumSalePatch
{
 static void Prefix(LootSeller __instance,out Dictionary<int,int> __state){__state=LootEconomy.Snapshot(__instance.Inventory);LootEconomy.TransferDepth++;}
 static Exception? Finalizer(LootSeller __instance,Dictionary<int,int> __state,Exception? __exception){
  LootEconomy.TransferDepth=Math.Max(0,LootEconomy.TransferDepth-1);
  // DeleteLoot may run inside Interact. Reconcile once, including a partially completed native sale.
  if(__state!=null)LootEconomy.Removed(__state,LootEconomy.Snapshot(__instance.Inventory));return __exception;
 }
}
[HarmonyPatch(typeof(Inventory),nameof(Inventory.DeleteLoot))]
static class PremiumRemovalPatch
{
 static void Prefix(Inventory __instance,out Dictionary<int,int>? __state)=>__state=LootEconomy.TransferDepth==0?LootEconomy.Snapshot(__instance):null;
 static void Postfix(Inventory __instance,Dictionary<int,int>? __state){if(__state!=null)LootEconomy.Removed(__state,LootEconomy.Snapshot(__instance));}
}
[HarmonyPatch]
static class PremiumStashPatch
{
 sealed class Move {public int Before,Stash;public LootItem Item=null!;}
 static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>typeof(SaveBag).GetMethods().Where(m=>m.Name is "MoveLootToStash" or "MoveLootToInventory");
 static void Prefix(SaveBag __instance,LootItem __0,out Move __state){
  __state=new Move{Item=__0,Before=LootEconomy.Snapshot(__instance.inventory).GetValueOrDefault(__0.Id)};
  if(__instance.saveData!=null)foreach(var entry in __instance.saveData.LootItems)if(entry.itemId==__0.Id)__state.Stash+=entry.count;
  LootEconomy.TransferDepth++;
 }
 static Exception? Finalizer(SaveBag __instance,Move __state,Exception? __exception){
  LootEconomy.TransferDepth=Math.Max(0,LootEconomy.TransferDepth-1);
  if(__state!=null)LootEconomy.Transfer(__instance,__state.Item,__state.Before,LootEconomy.Snapshot(__instance.inventory).GetValueOrDefault(__state.Item.Id),__state.Stash);
  return __exception;
 }
}
