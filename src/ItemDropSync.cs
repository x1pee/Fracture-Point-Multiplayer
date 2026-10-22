using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 sealed class DroppedActor {public LootableItem Item=null!;public GameObject Root=null!;public Packet Spawn=null!;public bool Pending,Owned;public float At,PoseAt;public Vector3 LastPosition;public Quaternion LastRotation;public (Rigidbody Body,bool Kinematic,bool Gravity)[] Physics=Array.Empty<(Rigidbody,bool,bool)>();}
 readonly Dictionary<string,DroppedActor> droppedActors=new();
 readonly Dictionary<int,string> droppedKeys=new();
 readonly List<(LootableItem Item,GameObject Root,string Key,int Factor)> newDrops=new();
 long dropSequence;string dropEpoch="";float dropPoseAt,dropCaptureAt;int dropCursor;
 readonly HashSet<int> dropReadyNow=new();float dropReadinessAt;
 readonly HashSet<int> dropReadyPeers=new();readonly Queue<(int Peer,string Key)> dropReadySnapshots=new();float dropSnapshotAt;
 int nativeDropCalls,nativeDropsCaptured;string lastDropSkip="";
 [HideFromIl2Cpp]
 internal object DropDiagnostics()=>new{nativeDropCalls,nativeDropsCaptured,lastDropSkip,pending=newDrops.Count,actors=droppedActors.Select(p=>new{key=p.Key,p.Value.Pending,p.Value.Owned,item=(bool)p.Value.Item,root=(bool)p.Value.Root,type=p.Value.Spawn.Drop}).ToArray()};
 [HideFromIl2Cpp]
 internal void NativeItemDropped(GameObject obj){
  nativeDropCalls++;
  if(Replay||!Connected||!SameWorld||Down||PartyWiped||!obj){lastDropSkip=$"Replay={Replay}, connected={Connected}, sameWorld={SameWorld}, down={Down}, wipe={PartyWiped}, object={(bool)obj}";return;}
  var item=obj.GetComponentInChildren<LootableItem>(true);if(!item){lastDropSkip="No native pickup component on "+obj.name;return;}
  nativeDropsCaptured++;lastDropSkip="";
  var key=$"drop:{Network!.LocalId}:{++dropSequence}";
  droppedKeys[item.GetInstanceID()]=key;
  newDrops.Add((item,obj,key,InventoryDropValue.Next())); // SpawnAmmo/SetMagAmmo finish AFTER SpawnDroppedObject returns.
 }
 [HideFromIl2Cpp]
 string? DroppedIdentity(Component c)=>droppedKeys.GetValueOrDefault(c.GetInstanceID());
 [HideFromIl2Cpp]
 bool PendingDrop(Component c)=>DroppedIdentity(c) is {} key&&(!droppedActors.TryGetValue(key,out var actor)||actor.Pending);
 void DropTick(){
  if(dropEpoch!=world){foreach(var old in droppedActors.Values)if(old.Root)Object.Destroy(old.Root);dropEpoch=world;droppedActors.Clear();droppedKeys.Clear();newDrops.Clear();dropReadyPeers.Clear();dropReadyNow.Clear();dropReadinessAt=0;dropReadySnapshots.Clear();}
  if(!Connected)return;
  if(Time.unscaledTime>=dropCaptureAt)foreach(var pending in newDrops.Take(4).ToArray()){
   dropCaptureAt=Time.unscaledTime+.1f;
   newDrops.Remove(pending);if(!pending.Item||!pending.Root)continue;
   var item=pending.Item;pending.Root.name="FractureTogether.Dropped."+pending.Key;var pos=pending.Root.transform.position;var rot=pending.Root.transform.rotation;
   var packet=Message(Network!.Hosting?"dropspawn":"dropreq");packet.ObjectId=pending.Key;packet.Sequence=dropSequence;
   packet.Pose=new[]{pos.x,pos.y,pos.z,rot.x,rot.y,rot.z,rot.w};
   packet.Drop=new ItemDropFrame{Type=(int)item.ItemType,ItemId=item.ItemId,Ammo=Math.Clamp(item.ammoCount,0,10000),MagazineAmmo=Math.Clamp(item.ammoToFillMag,0,1000),ValueFactor=pending.Factor};
   var actor=new DroppedActor{Item=item,Root=pending.Root,Spawn=packet,Pending=!Network.Hosting,Owned=true,At=Time.unscaledTime,LastPosition=pos,LastRotation=rot};droppedActors[pending.Key]=actor;
   RegisterLoot(item,0);
   Network.Send(packet);
  }
  if(!Network!.Hosting){foreach(var a in droppedActors.Values)if(a.Pending&&a.Item&&Time.unscaledTime-a.At>1){a.At=Time.unscaledTime;Network.Send(a.Spawn);}return;}
  // A joined peer can still be loading a different scene. Replay dynamic pickups
  // only after its state acknowledges this world; the early joined event is insufficient.
  if(droppedActors.Count==0&&newDrops.Count==0&&dropReadySnapshots.Count==0){dropReadyPeers.Clear();dropReadyNow.Clear();return;}
  if(Time.unscaledTime>=dropReadinessAt){dropReadinessAt=Time.unscaledTime+.25f;dropReadyNow.Clear();foreach(var remote in remotes.Values)if(PeerMatches(remote))dropReadyNow.Add(remote.Id);dropReadyPeers.IntersectWith(dropReadyNow);}
  var ready=dropReadyNow;
  foreach(var peer in ready)if(!dropReadyPeers.Contains(peer)){var live=droppedActors.Where(pair=>pair.Value.Item&&pair.Value.Item.gameObject.activeInHierarchy&&!takenLoot.IsTaken(pair.Key)).Select(pair=>pair.Key).ToArray();if(dropReadySnapshots.Count+live.Length>1536)continue;dropReadyPeers.Add(peer);foreach(var key in live)dropReadySnapshots.Enqueue((peer,key));}
  if(Time.unscaledTime>=dropSnapshotAt){dropSnapshotAt=Time.unscaledTime+.1f;for(var n=0;n<4&&dropReadySnapshots.Count>0;n++){var replay=dropReadySnapshots.Dequeue();if(ready.Contains(replay.Peer)&&droppedActors.TryGetValue(replay.Key,out var item)&&item.Item&&item.Item.gameObject.activeInHierarchy&&!takenLoot.IsTaken(replay.Key))Network.SendTo(item.Spawn,replay.Peer);}}
  if(Time.unscaledTime<dropPoseAt)return;dropPoseAt=Time.unscaledTime+.2f;
  var entries=droppedActors.ToArray();var sent=0;
  for(var n=0;n<entries.Length&&sent<24;n++){var pair=entries[dropCursor++%entries.Length];var actor=pair.Value;if(!actor.Item||!actor.Item.gameObject.activeInHierarchy||takenLoot.IsTaken(pair.Key))continue;
   if(!actor.Root)continue;var t=actor.Root.transform;var pos=t.position;var rot=t.rotation;if(Vector3.Distance(pos,actor.LastPosition)<.005f&&Quaternion.Angle(rot,actor.LastRotation)<.5f&&Time.unscaledTime-actor.PoseAt<5)continue;
   actor.PoseAt=Time.unscaledTime;actor.LastPosition=pos;actor.LastRotation=rot;var pose=Message("droppose");pose.ObjectId=pair.Key;pose.Pose=new[]{pos.x,pos.y,pos.z,rot.x,rot.y,rot.z,rot.w};actor.Spawn.Pose=pose.Pose;Network.Send(pose);sent++;
  }
 }
 [HideFromIl2Cpp]
 bool ReceiveDrop(Packet p){
  if(p.Kind=="loottaken"&&PacketWorld(p)&&droppedActors.TryGetValue(p.ObjectId,out var consumedDrop))consumedDrop.Pending=false;
  if(p.Kind is not ("dropreq" or "dropspawn" or "droppose" or "dropreject"))return false;
  if(p.Kind=="dropreject"){
   if(Network?.Hosting==false&&PacketWorld(p)&&droppedActors.TryGetValue(p.ObjectId,out var rejected)&&rejected.Pending&&rejected.Item){
    var inv=Object.FindObjectOfType<Inventory>();var before=InventorySummary(inv);var beforeLoot=LootEconomy.Snapshot(inv);var instance=rejected.Item.GetInstanceID();rejected.Pending=false;
    var refundReplay=Replay;Replay=true;try{rejected.Item.Interact();}finally{Replay=refundReplay;}
    if(InventorySummary(inv)!=before||!rejected.Item||!rejected.Item.gameObject.activeSelf){LootEconomy.RecordAcquired(inv,beforeLoot,rejected.Spawn.Drop?.ValueFactor??1);droppedActors.Remove(p.ObjectId);droppedKeys.Remove(instance);stableLootKeys.Remove(instance);lootObjects.Remove(p.ObjectId);detail="Item returned to your inventory: drop unavailable on host.";}else {rejected.Pending=true;rejected.At=Time.unscaledTime+60;detail="Unable to return dropped item. Keep this scene open; your item is still on the floor.";}
   }return true;
  }
  if(!PacketWorld(p)||Network==null||p.Pose.Length!=7)return true;
  if(p.Kind=="droppose"){
   if(!Network.Hosting&&droppedActors.TryGetValue(p.ObjectId,out var actor)&&actor.Item&&actor.Root&&!actor.Pending){actor.Root.transform.SetPositionAndRotation(new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]),new Quaternion(p.Pose[3],p.Pose[4],p.Pose[5],p.Pose[6]));}
   return true;
  }
  if(p.Drop==null)return true;
  if(p.Kind=="dropreq"){
   if(!Network.Hosting||!p.ObjectId.StartsWith($"drop:{p.PlayerId}:",StringComparison.Ordinal)||!remotes.TryGetValue(p.PlayerId,out var sender)||!PeerMatches(sender)||sender.State!.Health<=0)return true;
   if(droppedActors.TryGetValue(p.ObjectId,out var duplicate)){if(takenLoot.IsTaken(p.ObjectId)){var consumed=Message("loottaken");consumed.ObjectId=p.ObjectId;consumed.Category=0;Network.SendTo(consumed,p.PlayerId);}else Network.SendTo(duplicate.Spawn,p.PlayerId);return true;}
   var who=sender.State.Pose;if(Vector3.Distance(new Vector3(who[0],who[1],who[2]),new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]))>8)return true;
   p.Kind="dropspawn";
  }else if(Network.Hosting)return true;
  if(droppedActors.TryGetValue(p.ObjectId,out var own)){
   own.Pending=false;own.Spawn=p;if(own.Item&&!Network.Hosting)FreezeDropped(own);return true;
  }
  var prefab=droppedActors.Count<512?DroppedPrefab(p.Drop):null;
  if(!prefab){Plugin.Instance.Log.LogWarning("Dropped item catalogue unavailable: "+p.Drop.Type+"/"+p.Drop.ItemId);if(Network.Hosting){var no=Message("dropreject");no.ObjectId=p.ObjectId;Network.SendTo(no,p.PlayerId);}return true;}
  GameObject obj;var previous=Replay;Replay=true;try{obj=Object.FindObjectOfType<Inventory>().SpawnDroppedObject(prefab);}finally{Replay=previous;}
  obj.transform.SetPositionAndRotation(new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]),new Quaternion(p.Pose[3],p.Pose[4],p.Pose[5],p.Pose[6]));
  obj.name="FractureTogether.Dropped."+p.ObjectId;var item=obj.GetComponentInChildren<LootableItem>(true);
  item.countToTask=false;item.SetupAmmo(p.Drop.Ammo);if(item.ItemType is LootableItem.LootType.Mag or LootableItem.LootType.Weapon)item.SetMagAmmo(p.Drop.MagazineAmmo);
  var created=new DroppedActor{Item=item,Root=obj,Spawn=p,LastPosition=obj.transform.position,LastRotation=obj.transform.rotation};droppedKeys[item.GetInstanceID()]=p.ObjectId;droppedActors[p.ObjectId]=created;obj.SetActive(true);
  if(!Network.Hosting)FreezeDropped(created);RegisterLoot(item,0);
  if(Network.Hosting)Network.Send(p);
  return true;
 }
 [HideFromIl2Cpp]
 GameObject? DroppedPrefab(ItemDropFrame drop){
  var inv=Object.FindObjectOfType<Inventory>();GameObject? result=null;
  if(inv)switch((LootableItem.LootType)drop.Type){
   case LootableItem.LootType.Item:foreach(var i in inv.LootData.Loot)if(i.Id==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Weapon:foreach(var i in inv.WeaponData.Weapons)if(i.Id==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Heal:foreach(var i in inv.HealthData.HealthItems)if(i.Id==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Cloth:foreach(var i in inv.ArmorData.Armor)if(i.Id==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Mag:foreach(var i in inv.MagData.Mags)if(i.Index==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Grenade:foreach(var i in inv.GrenadeData.Grenades)if(i.Id==drop.ItemId){result=i.Prefab;break;}break;
   case LootableItem.LootType.Ammo:foreach(var i in inv.AmmoData.Ammo)if(i.Index==drop.ItemId){result=i.Prefab;break;}break;
  }
  if(result){var item=result.GetComponentInChildren<LootableItem>(true);if(item&&(int)item.ItemType==drop.Type&&item.ItemId==drop.ItemId)return result;}
  return Resources.FindObjectsOfTypeAll<LootableItem>().FirstOrDefault(i=>i&&(int)i.ItemType==drop.Type&&i.ItemId==drop.ItemId&&!i.gameObject.scene.IsValid())?.gameObject;
 }
 static void FreezeDropped(DroppedActor actor){if(actor.Physics.Length==0)actor.Physics=actor.Root.GetComponentsInChildren<Rigidbody>(true).Select(b=>(b,b.isKinematic,b.useGravity)).ToArray();foreach(var entry in actor.Physics)if(entry.Body){entry.Body.isKinematic=true;entry.Body.useGravity=false;}}
 void DropReset(){
  var retainHostOriginals=Network?.Hosting!=false;
  foreach(var actor in droppedActors.Values){
   // Published guest drops belong to the host's world. Its pickup notification
   // may still be in flight; retaining a second local copy would duplicate loot.
   if(actor.Owned&&retainHostOriginals){foreach(var entry in actor.Physics)if(entry.Body){entry.Body.isKinematic=entry.Kinematic;entry.Body.useGravity=entry.Gravity;}}
   else if(actor.Root)Object.Destroy(actor.Root);
  }
  newDrops.Clear();droppedActors.Clear();droppedKeys.Clear();dropReadyPeers.Clear();dropReadyNow.Clear();dropReadinessAt=0;dropReadySnapshots.Clear();dropEpoch="";dropCursor=0;
 }
}

[HarmonyPatch(typeof(Inventory),nameof(Inventory.SpawnDroppedObject))]
static class InventoryDropReplication {static void Postfix(GameObject __result)=>Together.Current?.NativeItemDropped(__result);}

[HarmonyPatch(typeof(Inventory),nameof(Inventory.DeleteLoot))]
static class InventoryDropValue {
 [ThreadStatic]static Queue<int>? factors;
 internal static int Next()=>factors?.Count>0?factors.Dequeue():1;
 static void Prefix(Inventory __instance,int index,int count,bool placeObject,out Queue<int>? __state){__state=factors;factors=placeObject?new Queue<int>(LootEconomy.PreviewDropFactors(__instance,index,count)):null;}
 static Exception? Finalizer(Queue<int>? __state,Exception? __exception){factors=__state;return __exception;}
}
