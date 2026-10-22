using HarmonyLib;
using System.Text.Json;
using UnityEngine;
using Object=UnityEngine.Object;
using NativeTask=Il2CppSystem.Threading.Tasks.Task;
namespace FractureTogether;

// Process-local only: no inventory/profile data enters a network packet.
internal static class PersonalNativeSaves
{
 static readonly Dictionary<(IntPtr,string),NativeTask> active=new();
 // Completed failures must remain observable: a stash write can fail before
 // travel begins. Only an explicit retry for this same owner/method replaces it.
 internal static NativeTask[] Pending(){lock(active)return active.Values.DistinctBy(t=>t.Pointer).Where(t=>!t.IsCompleted||t.IsFaulted||t.IsCanceled).ToArray();}
 internal static bool Reuse(IntPtr owner,string method,ref NativeTask result){lock(active){if(active.TryGetValue((owner,method),out var t)&&!t.IsCompleted){result=t;return true;}return false;}}
 internal static void Track(IntPtr owner,string method,NativeTask task){if(task!=null)lock(active){foreach(var key in active.Where(x=>x.Value.IsCompleted&&!x.Value.IsFaulted&&!x.Value.IsCanceled).Select(x=>x.Key).ToArray())active.Remove(key);active[(owner,method)]=task;}}
}
[HarmonyPatch(typeof(SavesManager),nameof(SavesManager.SaveAsync))]
static class PersonalSaveTaskPatch {
 static bool Prefix(SavesManager __instance,ref NativeTask __result)=>!PersonalNativeSaves.Reuse(__instance.Pointer,"inventory",ref __result);
 static void Postfix(SavesManager __instance,NativeTask __result)=>PersonalNativeSaves.Track(__instance.Pointer,"inventory",__result);
}
[HarmonyPatch(typeof(SavesManager),nameof(SavesManager.SaveQuestProgress))]
static class PersonalQuestTaskPatch {
 static bool Prefix(SavesManager __instance,ref NativeTask __result)=>!PersonalNativeSaves.Reuse(__instance.Pointer,"quests",ref __result);
 static void Postfix(SavesManager __instance,NativeTask __result)=>PersonalNativeSaves.Track(__instance.Pointer,"quests",__result);
}
[HarmonyPatch(typeof(SaveBag),nameof(SaveBag.SaveProgressAsync))]
static class PersonalStashTaskPatch {
 static bool Prefix(SaveBag __instance,ref NativeTask __result)=>!PersonalNativeSaves.Reuse(__instance.Pointer,"stash",ref __result);
 static void Postfix(SaveBag __instance,NativeTask __result)=>PersonalNativeSaves.Track(__instance.Pointer,"stash",__result);
}
public sealed partial class Together
{
 sealed class PersonalTransition {
  internal int Index;internal bool Replay,Immediate,Writing;internal float Started;
  internal SceneLoadingManager Loader=null!;internal SavesManager Saves=null!;internal Inventory Inventory=null!;
  internal NativeTask[] Tasks=Array.Empty<NativeTask>();internal string Snapshot="",Scene="";
 }
 PersonalTransition? personalTransition;PersonalTransition? personalPermit;
 sealed class PersonalReviveState {
  internal IntPtr Player;internal PlayerHealthManager Health=null!;internal int HP,Floor,LocalId,Spectated;internal bool Died,RestoreDeath,WasSpectating,WasMarked;
  internal string Scene="",World="";internal Vector3? Anchor;internal UnityEngine.InputSystem.PlayerInput? Input,DeathInput;
 }
 PersonalReviveState? personalRevive;
 void FinishPersonalRevive(bool success){
  var saved=personalRevive;personalRevive=null;if(saved==null)return;
  // Never roll a new floor/player or native team death back into an old down state.
  if(PartyWiped||PlayingNativeDeath||!player||player.Pointer!=saved.Player||floor!=saved.Floor||world!=saved.World||UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!=saved.Scene)return;
  if(success){ReleasePersonalInput(saved.Input,saved.RestoreDeath);return;}
  StopSpectator();Down=true;
  if(saved.Health){saved.Health.Health=saved.HP;saved.Health.died=saved.Died;}
  deathInput=saved.DeathInput;restoreDeathInput=saved.RestoreDeath;reviveAnchor=saved.Anchor;
  if(saved.Input)saved.Input.DeactivateInput();
  if(Network!=null&&Network.LocalId==saved.LocalId){if(saved.WasMarked)downPlayers.Add(saved.LocalId);else downPlayers.Remove(saved.LocalId);}
  if(saved.WasSpectating&&Network!=null&&SharedRun){try{StartSpectator();SpectatedPlayer=saved.Spectated;SpectatorTick();}catch(Exception e){Plugin.Instance.Log.LogError("Could not restore spectator after cancelled travel: "+e.Message);}}
 }

 internal bool PersonalSavePending=>personalTransition!=null;
 // Return false to defer; call before changing run state or broadcasting the scene.
 internal bool PersonalSceneReady(int index,SceneLoadingManager loader,bool replay,bool immediate=false){
  if(personalPermit is {} permit&&permit.Index==index&&permit.Loader.Pointer==loader.Pointer&&permit.Replay==replay&&permit.Immediate==immediate){personalPermit=null;return true;}
  if(PartyWiped||PlayingNativeDeath||quitting)return true;
  if(personalTransition!=null){
   // The host's travel authorization may arrive while this client's native
   // ReturnToHub is already saving. Preserve it when coalescing that request.
   if(replay&&personalTransition.Index==index&&personalTransition.Loader.Pointer==loader.Pointer)personalTransition.Replay=true;
   return false;
  }
  if(index is not (0 or 1 or 2)||!player)return true;
  try{
   PreparePersonalTraveller(index);
   var saves=Object.FindObjectOfType<SavesManager>();
   if(!saves||!saves.inventory)throw new IOException("Personal inventory save manager unavailable.");
   CheckPersonalPath(saves);
   personalTransition=new PersonalTransition{Index=index,Loader=loader,Replay=replay,Immediate=immediate,Saves=saves,Inventory=saves.inventory,Tasks=PersonalNativeSaves.Pending(),Started=Time.realtimeSinceStartup,Scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name};
   GameplayLog.Record("save_started",new{destination=index,scene,floor,down=Down});
   detail="Saving and checking your personal inventory before travel...";
  }catch(Exception e){PersonalSaveFailed(e);}
  return false;
 }
 void PreparePersonalTraveller(int index){
  if(!Down)return;
  if(index is not (0 or 2))throw new IOException("You are down; wait for team revival before starting another raid.");
  // A surviving party returning home preserves the downed member's inventory.
  // ReviveLocal is the existing PartyDeath helper; never call it for a party wipe.
  if(PartyWiped||PlayingNativeDeath)throw new IOException("Native party death is in progress.");
  var health=Object.FindObjectOfType<PlayerHealthManager>();if(!health||!player)throw new IOException("Downed player is unavailable for safe travel.");
  if(personalRevive==null)personalRevive=new PersonalReviveState{Player=player.Pointer,Health=health,HP=health.Health,Died=health.died,Floor=floor,World=world,Scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,LocalId=Network?.LocalId??-1,WasMarked=Network!=null&&downPlayers.Contains(Network.LocalId),WasSpectating=Spectating,Spectated=SpectatedPlayer,RestoreDeath=restoreDeathInput,DeathInput=deathInput,Input=player.GetComponent<UnityEngine.InputSystem.PlayerInput>(),Anchor=reviveAnchor};
  ReviveLocal();
  // This revival exists only to produce a valid home save, not to resume combat.
  if(personalRevive.Input)personalRevive.Input.DeactivateInput();
  if(Down||!health||health.died||health.Health<=0)throw new IOException("Could not revive your player safely before saving.");
  if(Network!=null)downPlayers.Remove(Network.LocalId);
 }
 static string PersonalRoot(){var profile=Plugin.Instance.Profile;if(!profile.Healthy)throw new IOException("Personal profile storage is unhealthy.");var root=Path.GetFullPath(profile.DirectoryPath);if(!string.Equals(root,Path.GetFullPath(Application.persistentDataPath),StringComparison.OrdinalIgnoreCase))throw new IOException("Personal save path redirection mismatch.");return root;}
 static void CheckPersonalPath(SavesManager saves){
  var root=PersonalRoot();if(saves.useCustomFile)throw new IOException("Custom save presets cannot be verified for travel.");var path=Path.GetFullPath(saves.path??"");
  if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFileName(path),"SavedData.json",StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(path),root,StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing a save outside this personal profile.");
  for(var dir=new DirectoryInfo(Path.GetDirectoryName(path)!);dir!=null;dir=dir.Parent)if((dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked personal save directories are not supported.");
  if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked personal save file is not supported.");
  foreach(var bag in Object.FindObjectsOfType<SaveBag>()){
   var stash=Path.GetFullPath(bag.path??"");if(!stash.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFileName(stash),"StashData.json",StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetDirectoryName(stash),root,StringComparison.OrdinalIgnoreCase))throw new IOException("Stash save path is outside this personal profile.");
   if(File.Exists(stash)&&(File.GetAttributes(stash)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked personal stash file is not supported.");
  }
 }
 sealed record PersonalInventoryView(float Dollars,float Roubles,float Bank,bool NVG,string Loot,string Ammo,string Health,string Grenades,string[] Mags,int[] Armor,int Helmet,int Vest,int[] Weapons);
 static void CountItem(SortedDictionary<int,int> items,int id,int count){if(count<0)throw new IOException("Negative inventory item count.");if(count>0)items[id]=checked(items.GetValueOrDefault(id)+count);}
 static string Counts(SortedDictionary<int,int> items)=>JsonSerializer.Serialize(items);
 static string MagStamp(int index,int ammo,int maximum,int size,string? weapon,float caliber,int price,int rarity)=>JsonSerializer.Serialize(new{index,ammo,maximum,size,weapon,caliber,price,rarity});
 static PersonalInventoryView InventoryView(SavesManager saves){
  var inv=saves.inventory;var loot=new SortedDictionary<int,int>();var ammo=new SortedDictionary<int,int>();var health=new SortedDictionary<int,int>();var grenades=new SortedDictionary<int,int>();
  foreach(var kv in inv.LootItems)CountItem(loot,kv.Key.Id,kv.Value);
  foreach(var kv in inv.AmmoStored)CountItem(ammo,kv.Key.Index,kv.Value);
  foreach(var kv in inv.HealthItems)CountItem(health,kv.Key.Id,kv.Value);
  foreach(var kv in inv.GrenadeItems)CountItem(grenades,kv.Key.Id,kv.Value);
  var mags=new List<string>();foreach(var m in inv.MagStored)mags.Add(MagStamp(m.Index,m.AmmoCount,m.MaxAmmoCount,(int)m.MagSize,m.WeaponName,m.Caliber,m.price,(int)m.Rarity));
  var armor=new List<int>();var helmet=-1;var vest=-1;var ordinal=0;foreach(var a in inv.ArmorItems){armor.Add(a.Id);if(inv.HelmetEquipped!=null&&a.Pointer==inv.HelmetEquipped.Pointer)helmet=ordinal;if(inv.VestEquipped!=null&&a.Pointer==inv.VestEquipped.Pointer)vest=ordinal;ordinal++;}
  // SaveAsync uses the settings prefab list (not instantiated runtime weapons),
  // matches WeaponData.Weapons by Weapon component identity, then stores item.Id.
  var weapons=new List<int>();foreach(var prefab in saves.FPSControllerSettings.weaponPrefabs){
   var weapon=prefab.GetComponent<Demo.Scripts.Runtime.Item.Weapon>();if(!weapon)throw new IOException("Saved weapon prefab has no Weapon component.");
   var found=false;foreach(var item in inv.WeaponData.Weapons)if(item.Weapon&&item.Weapon.Pointer==weapon.Pointer){weapons.Add(item.Id);found=true;break;}
   if(!found)throw new IOException("Saved weapon is missing from the personal weapon catalogue.");
  }
  return new(inv.DollarsCount,inv.RoublesCount,inv.DollarsInBank,inv.hasNVG,Counts(loot),Counts(ammo),Counts(health),Counts(grenades),mags.ToArray(),armor.ToArray(),helmet,vest,weapons.ToArray());
 }
 static string InventoryStamp(SavesManager saves)=>JsonSerializer.Serialize(InventoryView(saves));
 static string SavedCounts(JsonElement saved,string property){var items=new SortedDictionary<int,int>();foreach(var entry in saved.GetProperty(property).EnumerateArray())CountItem(items,entry.GetProperty("itemId").GetInt32(),entry.GetProperty("count").GetInt32());return Counts(items);}
 static string[] SavedMags(JsonElement saved)=>saved.GetProperty("MagStored").EnumerateArray().Select(m=>MagStamp(m.GetProperty("Index").GetInt32(),m.GetProperty("AmmoCount").GetInt32(),m.GetProperty("MaxAmmoCount").GetInt32(),m.GetProperty("MagSize").GetInt32(),m.GetProperty("WeaponName").GetString(),m.GetProperty("Caliber").GetSingle(),m.GetProperty("price").GetInt32(),m.GetProperty("Rarity").GetInt32())).ToArray();
 static int[] SavedIds(JsonElement saved,string name)=>saved.GetProperty(name).EnumerateArray().Select(x=>x.GetInt32()).ToArray();
 static void BackupPersonalSmallFiles(){
  var root=PersonalRoot();var dest=Path.Combine(root+".transition-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N"));
  // Explicit small save set: no recursive scan, localization files or original profiles.
  var files=new[]{"SavedData.json","StashData.json","QuestData.json","QuestProgress.json","QuestsData.json","PersonalPlayerPrefs.json","LootPremiums.json"};
  var parent=Path.GetDirectoryName(dest)!;Directory.CreateDirectory(parent);if((File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("Linked personal backup folder is not supported.");Directory.CreateDirectory(dest);
  foreach(var name in files){var source=Path.Combine(root,name);if(!File.Exists(source))continue;var info=new FileInfo(source);if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length>16*1024*1024)throw new IOException("Unexpected personal save file: "+name);File.Copy(source,Path.Combine(dest,name),false);}
 }
 static void VerifyPersonalInventory(SavesManager saves,Inventory inv,string snapshot){
  CheckPersonalPath(saves);if(InventoryStamp(saves)!=snapshot)throw new IOException("Inventory changed during saving; retry travel to save its latest state.");
  using var json=JsonDocument.Parse(saves.GetSavedJson());var saved=json.RootElement;
  var expected=InventoryView(saves);
  var actual=new PersonalInventoryView(saved.GetProperty("DollarsInInventory").GetSingle(),saved.GetProperty("RoublesInInventory").GetSingle(),inv.DollarsInBank,saved.GetProperty("hasNVG").GetBoolean(),SavedCounts(saved,"LootItems"),SavedCounts(saved,"AmmoStored"),SavedCounts(saved,"HealthItems"),SavedCounts(saved,"GrenadeItems"),SavedMags(saved),SavedIds(saved,"ArmorIds"),saved.GetProperty("HelmetEquippedId").GetInt32(),saved.GetProperty("VestEquippedId").GetInt32(),SavedIds(saved,"WeaponsIndecies"));
  // Exact canonical comparison preserves duplicate magazines, armor and weapons,
  // their order, and the equipped armor ordinals used by the native loader.
  if(JsonSerializer.Serialize(expected)!=JsonSerializer.Serialize(actual))throw new IOException("Saved inventory, equipment or money does not match your current inventory.");
  Plugin.Instance.Profile.Flush();if(!Plugin.Instance.Profile.Healthy)throw new IOException("Personal preferences failed to save.");
  using var prefs=JsonDocument.Parse(File.ReadAllText(Path.Combine(PersonalRoot(),"PersonalPlayerPrefs.json")));
  var bank=inv.DollarsInBank;if(prefs.RootElement.TryGetProperty("Bank",out var value)){if(!value.TryGetProperty("Float",out var amount)||amount.ValueKind!=JsonValueKind.Number||!MoneyEqual(amount.GetSingle(),bank))throw new IOException("Saved personal bank balance differs.");}else if(bank!=0)throw new IOException("Saved personal bank balance is missing.");
 }
 static bool MoneyEqual(float a,float b)=>float.IsFinite(a)&&float.IsFinite(b)&&Math.Abs(a-b)<=.001f;
 void PersonalSaveFailed(Exception e){GameplayLog.Fault("personal_save",e);personalTransition=null;personalPermit=null;FinishPersonalRevive(false);CancelDisconnectRecovery();detail="Personal save could not be verified. Travel cancelled; your inventory stays in memory. "+e.Message;Plugin.Instance.Log.LogError(detail);}
 internal void PersonalSaveTick(){
  var p=personalTransition;if(p==null)return;
  try{
   if(PartyWiped||PlayingNativeDeath){personalTransition=null;personalRevive=null;CancelDisconnectRecovery();return;}
   PreparePersonalTraveller(p.Index);
   if(!p.Loader||!p.Saves||!p.Inventory||p.Scene!=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)throw new IOException("The current scene changed while saving.");
   CheckPersonalPath(p.Saves);
    if(p.Tasks.Any(t=>t==null||t.IsFaulted||t.IsCanceled))throw new IOException("A native inventory, stash or quest save failed or was cancelled. Keep this scene open and retry the failed save before travelling. A failed save whose owner no longer exists remains blocked until restart.");
   if(p.Tasks.Any(t=>!t.IsCompleted)){if(Time.realtimeSinceStartup-p.Started>20)detail="Waiting for your personal save. Keep the game open.";return;}
   if(!p.Writing){
    // A native save may have started while the previously observed set completed.
    var pending=PersonalNativeSaves.Pending();if(pending.Length>0){p.Tasks=pending;return;}
    BackupPersonalSmallFiles();Plugin.Instance.Profile.Flush();CheckPersonalPath(p.Saves);p.Snapshot=InventoryStamp(p.Saves);p.Writing=true;
    p.Tasks=new[]{p.Saves.SaveAsync()};return;
   }
   var outstanding=PersonalNativeSaves.Pending();if(outstanding.Length>0){p.Tasks=outstanding;return;}
   VerifyPersonalInventory(p.Saves,p.Inventory,p.Snapshot);
   GameplayLog.Record("save_verified",new{destination=p.Index,scene,floor,seconds=Time.realtimeSinceStartup-p.Started});
   if(p.Loader._isLoading)throw new IOException("Another scene transition started during saving.");
   personalTransition=null;personalPermit=p;var previousReplay=Replay;Replay=p.Replay;
   try{if(p.Immediate)p.Loader.LoadSceneImmediately(p.Index);else p.Loader.LoadScene(p.Index);if((p.Loader&&p.Loader._isLoading)||UnityEngine.SceneManagement.SceneManager.GetActiveScene().name!=p.Scene){FinishPersonalRevive(true);detail="Personal inventory saved.";}else throw new IOException("The scene request was not accepted; travel cancelled.");}
   finally{Replay=previousReplay;personalPermit=null;CancelDisconnectRecovery();}
  }catch(Exception e){PersonalSaveFailed(e);}
 }
}
