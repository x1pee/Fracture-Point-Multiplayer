using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 sealed class DispensedSoda {
  public GameObject Object=null!;public Transform Root=null!;public string LootKey="";
  public Vector3 SentPosition;public Quaternion SentRotation;public bool Sent;public float PoseAt;
 }
 readonly Dictionary<string,Packet> vendingOrders=new(),vendingReceipts=new();
 readonly Dictionary<string,DispensedSoda> sodaObjects=new();
 readonly Dictionary<string,Packet> sodaSpawns=new(),pendingSodaSpawns=new();
 readonly List<string> staleSodas=new(),sodaPoseKeys=new(),pendingSodaKeys=new();int sodaPoseCursor,pendingSodaCursor;
 readonly Dictionary<string,VendingMachine> vendingMachines=new();
 readonly Dictionary<int,(VendingMachine Machine,string Key)> vendingMachineKeys=new();
 readonly Dictionary<IntPtr,bool> vendingPrefabs=new();
 readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> nearbyVendingColliders=new(128);
 readonly HashSet<int> sodaReadyPeers=new(),sodaReadyNow=new();
 readonly Queue<(int Peer,string Key)> sodaReadySnapshots=new();
 readonly PeerTimeline vendingRequests=new();
 string vendingEpoch="";long vendingSequence,pendingVendingSequence;
 float vendingSendAt,vendingGrantAt;string pendingVending="";
 static string SodaKey(string id)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("soda|"+id)));
 [HideFromIl2Cpp]
 internal void VendingReset(bool hosting){
  // Shared guest cans belong to the host's world after publication. Keeping a
  // second offline copy (even after an uncertain ACK) would duplicate a pickup.
  // Host/native originals keep their current pose and native physics.
  foreach(var soda in sodaObjects.Values)if(!hosting&&soda.Object){
   var heal=soda.Object.GetComponentInChildren<FastHealItem>(true);if(heal)stableLootKeys.Remove(heal.GetInstanceID());
   lootObjects.Remove(soda.LootKey);soda.Object.SetActive(false);Object.Destroy(soda.Object);
  }
  vendingEpoch="";vendingOrders.Clear();vendingReceipts.Clear();sodaObjects.Clear();sodaSpawns.Clear();pendingSodaSpawns.Clear();
  sodaPoseKeys.Clear();pendingSodaKeys.Clear();sodaPoseCursor=pendingSodaCursor=0;
  vendingMachines.Clear();vendingMachineKeys.Clear();vendingPrefabs.Clear();
  sodaReadyPeers.Clear();sodaReadyNow.Clear();sodaReadySnapshots.Clear();pendingVending="";pendingVendingSequence=0;vendingSendAt=vendingGrantAt=0;
 }
 void EnsureVendingEpoch(){
  if(Network==null)return;
  var epoch=$"{Network.Seed}:{scene}:{floor}:{world}";if(vendingEpoch==epoch)return;
  VendingReset(Network.Hosting);vendingEpoch=epoch;
 }
 [HideFromIl2Cpp]
 internal void RegisterVendingMachine(VendingMachine machine){
  EnsureVendingEpoch();if(!machine||machine.gameObject.scene.name!=scene)return;
  var instance=machine.GetInstanceID();
  if(vendingMachineKeys.TryGetValue(instance,out var known)&&known.Machine&&known.Machine.Pointer==machine.Pointer)return;
  var key=SpatialKey(machine.transform);vendingMachineKeys[instance]=(machine,key);vendingMachines[key]=machine;
 }
 bool SupportedVending(VendingMachine machine){
  if(!machine||!machine.sodaPrefab)return false;
  var prefab=machine.sodaPrefab;if(vendingPrefabs.TryGetValue(prefab.Pointer,out var supported))return supported;
  // Native Interact requires a Rigidbody on the prefab ROOT, not a child.
  supported=prefab.GetComponentInChildren<FastHealItem>()&&prefab.GetComponent<Rigidbody>();vendingPrefabs[prefab.Pointer]=supported;return supported;
 }
 VendingMachine? FindMachine(string id,Vector3? nearby=null){
  if(vendingMachines.TryGetValue(id,out var cached)&&cached&&cached.gameObject.activeInHierarchy&&cached.gameObject.scene.name==scene)return cached;
  if(nearby==null||!float.IsFinite(nearby.Value.x)||!float.IsFinite(nearby.Value.y)||!float.IsFinite(nearby.Value.z))return null;
  // The bounded world census usually populated the cache already. A first
  // click ahead of that census gets one bounded local query, never a scene find.
  var count=Physics.OverlapSphereNonAlloc(nearby.Value,8,nearbyVendingColliders,~0,QueryTriggerInteraction.Collide);
  try{for(var i=0;i<count&&i<nearbyVendingColliders.Length;i++){
   var collider=nearbyVendingColliders[i];if(!collider||collider.gameObject.scene.name!=scene)continue;
   var machine=collider.GetComponentInParent<VendingMachine>();if(!machine||!machine.gameObject.activeInHierarchy)continue;
   RegisterVendingMachine(machine);if(vendingMachines.TryGetValue(id,out cached)&&cached)return cached;
  }}finally{for(var i=0;i<nearbyVendingColliders.Length;i++)nearbyVendingColliders[i]=null!;}
  return null;
 }
 static float[] SodaPose(Transform t){var p=t.position;var q=t.rotation;return new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w};}
 static bool ValidSodaPose(Packet p)=>p.Pose is {Length:7}&&float.IsFinite(p.Pose[0])&&float.IsFinite(p.Pose[1])&&float.IsFinite(p.Pose[2])&&Math.Abs(p.Pose[0])<=1000000&&Math.Abs(p.Pose[1])<=1000000&&Math.Abs(p.Pose[2])<=1000000&&UnitFrame.Rotation(p.Pose,3);
 static void SetSodaPose(DispensedSoda soda,Packet p)=>soda.Root.SetPositionAndRotation(new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]),new Quaternion(p.Pose[3],p.Pose[4],p.Pose[5],p.Pose[6]));
 DispensedSoda RegisterSoda(string id,GameObject soda,FastHealItem? heal=null){
  if(!heal)heal=soda.GetComponentInChildren<FastHealItem>();if(!heal)throw new IOException("Vending prefab has no FastHealItem");
  var key=SodaKey(id);stableLootKeys[heal!.GetInstanceID()]=key;lootObjects[key]=heal;
  var actor=new DispensedSoda{Object=soda,Root=soda.transform,LootKey=key};if(!sodaObjects.ContainsKey(id))sodaPoseKeys.Add(id);sodaObjects[id]=actor;
  foreach(var rb in soda.GetComponentsInChildren<Rigidbody>()){
   if(Network?.Hosting==false){rb.velocity=Vector3.zero;rb.angularVelocity=Vector3.zero;rb.isKinematic=true;rb.useGravity=false;}
   else if(!rb.isKinematic)rb.collisionDetectionMode=CollisionDetectionMode.ContinuousDynamic;
  }
  if(takenLoot.IsTaken(key))HideTaken(key);return actor;
 }
 bool LiveSoda(DispensedSoda soda)=>soda.Object&&soda.Object.activeInHierarchy&&!takenLoot.IsTaken(soda.LootKey);
 [HideFromIl2Cpp]
 public bool BuySoda(VendingMachine machine){
  if(returningAfterDisconnect)return false;
  if(Replay||!Connected)return true;
  EnsureVendingEpoch();
  if(!SameWorld||Down||PartyWiped||(elevator&&elevator.isMoving)||pendingVending!=""){detail="Vending machine is waiting for the current action to finish.";return false;}
  if(!SupportedVending(machine)){detail="Unsupported vending prefab; purchase blocked to protect your money.";return false;}
  RegisterVendingMachine(machine);
  var p=Message("vendreq");p.ObjectId=vendingMachineKeys[machine.GetInstanceID()].Key;p.Sequence=++vendingSequence;p.PlayerId=Network!.LocalId;
  pendingVending=p.ObjectId;pendingVendingSequence=p.Sequence;
  if(Network.Hosting)HandleVendingRequest(p);else Network.Send(p);return false;
 }
 void HandleVendingRequest(Packet p){
  if(!PacketWorld(p)||p.Sequence<=0)return;EnsureVendingEpoch();if(!vendingRequests.Accept(p.PlayerId,p.Sequence))return;
  var alive=p.PlayerId==Network!.LocalId?!Down:remotes.TryGetValue(p.PlayerId,out var peer)&&PeerMatches(peer)&&peer.State!.Health>0&&!downPlayers.Contains(p.PlayerId);
  var position=p.PlayerId==Network.LocalId&&player?player.transform.position:remotes.TryGetValue(p.PlayerId,out var remote)&&remote.State!=null?new Vector3(remote.State.Pose[0],remote.State.Pose[1],remote.State.Pose[2]):Vector3.positiveInfinity;
  var machine=alive?FindMachine(p.ObjectId,position):null;
  var valid=SameWorld&&machine&&SupportedVending(machine!)&&alive&&Vector3.Distance(position,machine!.transform.position)<7&&vendingOrders.Count<64&&sodaSpawns.Count+vendingOrders.Count<128&&!PartyWiped&&!(elevator&&elevator.isMoving);
  var grant=Message("vendgrant");grant.TargetId=p.PlayerId;grant.Sequence=p.Sequence;grant.MachineId=p.ObjectId;grant.ObjectId=SodaKey($"{Network.Seed}:{world}:{p.PlayerId}:{p.Sequence}");grant.Success=valid;
  if(valid)vendingOrders[grant.ObjectId]=grant;
  if(p.PlayerId==Network.LocalId)ExecuteVendingGrant(grant);else Network.SendTo(grant,p.PlayerId);
 }
 void ExecuteVendingGrant(Packet p){
  if(!PacketWorld(p)||p.TargetId!=Network!.LocalId)return;EnsureVendingEpoch();
  if(vendingReceipts.TryGetValue(p.ObjectId,out var receipt)){if(Network.Hosting)HandleVendingPaid(receipt);else Network.Send(receipt);return;}
  // A grant may debit only the exact purchase initiated by this local player.
  if(pendingVending!=p.MachineId||pendingVendingSequence!=p.Sequence)return;
  pendingVending="";pendingVendingSequence=0;
  if(!p.Success){detail="Purchase refused: loading, distance or vending capacity.";return;}
  var paid=Message("vendpaid");paid.ObjectId=p.ObjectId;paid.MachineId=p.MachineId;paid.Sequence=p.Sequence;paid.PlayerId=Network.LocalId;
  // Cache even a failed receipt BEFORE native code: a repeated grant cannot debit twice.
  vendingReceipts[p.ObjectId]=paid;
  var machine=FindMachine(p.MachineId);
  if(machine&&SameWorld&&!Down&&!PartyWiped&&!(elevator&&elevator.isMoving)){
   var parent=machine!.transform;var before=parent.childCount;
   var previous=Replay;Replay=true;try{machine.Interact();}catch(Exception e){Plugin.Instance.Log.LogError(e);}finally{Replay=previous;}
   // Native Interact appends one prefab child. Inspect only newly appended roots,
   // without two recursive arrays and a HashSet of every previously dispensed can.
   var after=parent.childCount;
   for(var i=before;i<after&&i<before+8;i++){
    var root=parent.GetChild(i);var heal=root.GetComponentInChildren<FastHealItem>();if(!heal)continue;
    RegisterSoda(p.ObjectId,root.gameObject,heal);paid.Pose=SodaPose(root);paid.Success=true;break;
   }
  }
  if(Network.Hosting)HandleVendingPaid(paid);else Network.Send(paid);
 }
 void HandleVendingPaid(Packet p){
  if(!PacketWorld(p))return;EnsureVendingEpoch();
  if(!vendingOrders.TryGetValue(p.ObjectId,out var order)||order.TargetId!=p.PlayerId||order.Sequence!=p.Sequence||order.MachineId!=p.MachineId)return;
  if(!p.Success){vendingOrders.Remove(p.ObjectId);return;}
  var machine=FindMachine(order.MachineId);if(!machine||!SupportedVending(machine)||!ValidSodaPose(p))return;
  if(!sodaObjects.TryGetValue(p.ObjectId,out var actor)||!actor.Object){
   // Native VendingMachine.Interact RVA 9E9492/9E953E/9E9616:
   // parented clone, local (.2,.55,.3), forward * force with Impulse.
   // Only the host recreates physics, without executing another player's debit.
   var soda=Object.Instantiate(machine.sodaPrefab,machine.transform);soda.transform.localPosition=new Vector3(.2f,.55f,.3f);
   actor=RegisterSoda(p.ObjectId,soda);
   var body=soda.GetComponent<Rigidbody>();body.AddForce(machine.transform.forward*machine.force,ForceMode.Impulse);
  }
  var spawn=Message("vendspawn");spawn.ObjectId=p.ObjectId;spawn.MachineId=order.MachineId;spawn.Pose=SodaPose(actor.Root);spawn.TargetId=p.PlayerId;spawn.Sequence=order.Sequence;
  sodaSpawns[p.ObjectId]=spawn;vendingOrders.Remove(p.ObjectId);Network!.Send(spawn);
 }
 bool SpawnSoda(Packet p,bool localLookup=true){
  if(!ValidSodaPose(p))return true;
  if(sodaObjects.TryGetValue(p.ObjectId,out var existing)&&existing.Object){SetSodaPose(existing,p);return true;}
  var machine=FindMachine(p.MachineId,localLookup?new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]):null);if(!machine||!SupportedVending(machine))return false;
  if(takenLoot.IsTaken(SodaKey(p.ObjectId)))return true;
  var soda=Object.Instantiate(machine.sodaPrefab,machine.transform);var actor=RegisterSoda(p.ObjectId,soda);SetSodaPose(actor,p);return true;
 }
 bool ReceiveVending(Packet p){
  if(p.Kind is not ("vendreq" or "vendpaid" or "vendgrant" or "vendspawn" or "vendpose"))return false;
  EnsureVendingEpoch();
  if(p.Kind=="vendreq"&&Network?.Hosting==true){HandleVendingRequest(p);return true;}
  if(p.Kind=="vendpaid"&&Network?.Hosting==true){HandleVendingPaid(p);return true;}
  if(p.Kind=="vendgrant"&&Network?.Hosting==false){ExecuteVendingGrant(p);return true;}
  if(p.Kind=="vendspawn"&&Network?.Hosting==false){
   if(PacketWorld(p)&&ValidSodaPose(p)){
    if(SpawnSoda(p)){pendingSodaSpawns.Remove(p.ObjectId);pendingSodaKeys.Remove(p.ObjectId);}
    else if(pendingSodaSpawns.Count<128||pendingSodaSpawns.ContainsKey(p.ObjectId)){
     if(!pendingSodaSpawns.ContainsKey(p.ObjectId))pendingSodaKeys.Add(p.ObjectId);pendingSodaSpawns[p.ObjectId]=p;
    }
   }return true;
  }
  if(p.Kind=="vendpose"&&Network?.Hosting==false){
   if(PacketWorld(p)&&ValidSodaPose(p)){
    if(sodaObjects.TryGetValue(p.ObjectId,out var soda)&&LiveSoda(soda))SetSodaPose(soda,p);
    else if(pendingSodaSpawns.TryGetValue(p.ObjectId,out var pending))pending.Pose=p.Pose;
   }return true;
  }
  return true;
 }
 void VendingTick(){
  if(Network==null)return;EnsureVendingEpoch();
  if(!Connected||Time.unscaledTime<vendingSendAt)return;vendingSendAt=Time.unscaledTime+.25f;
  if(!Network.Hosting){
   // A spawn can precede the bounded machine census. Preserve it and its latest
   // pose until that exact machine binds; pose-only updates cannot create a can.
   for(var n=0;n<4&&pendingSodaKeys.Count>0;n++){
    if(pendingSodaCursor>=pendingSodaKeys.Count)pendingSodaCursor=0;var key=pendingSodaKeys[pendingSodaCursor];
    if(SpawnSoda(pendingSodaSpawns[key],false)){pendingSodaSpawns.Remove(key);pendingSodaKeys.RemoveAt(pendingSodaCursor);}
    else pendingSodaCursor++;
   }return;
  }
  // Consumed objects release live capacity. Receipts/takenLoot still preserve
  // idempotency; the 128-can limit must not count all purchases ever this world.
  staleSodas.Clear();foreach(var entry in sodaObjects)if(!LiveSoda(entry.Value))staleSodas.Add(entry.Key);
  foreach(var key in staleSodas){sodaObjects.Remove(key);sodaSpawns.Remove(key);sodaPoseKeys.Remove(key);}
  sodaReadyNow.Clear();foreach(var peer in remotes.Values)if(PeerMatches(peer))sodaReadyNow.Add(peer.Id);sodaReadyPeers.IntersectWith(sodaReadyNow);
  foreach(var peer in sodaReadyNow){
   if(sodaReadyPeers.Contains(peer)||sodaReadySnapshots.Count+sodaSpawns.Count>512)continue;
   sodaReadyPeers.Add(peer);foreach(var entry in sodaSpawns)sodaReadySnapshots.Enqueue((peer,entry.Key));
  }
  for(var n=0;n<8&&sodaReadySnapshots.Count>0;n++){
   var snapshot=sodaReadySnapshots.Dequeue();if(!remotes.TryGetValue(snapshot.Peer,out var peer)||!PeerMatches(peer)||!sodaSpawns.TryGetValue(snapshot.Key,out var original)||!sodaObjects.TryGetValue(snapshot.Key,out var soda)||!LiveSoda(soda))continue;
   var spawn=Message("vendspawn");spawn.ObjectId=snapshot.Key;spawn.MachineId=original.MachineId;spawn.TargetId=original.TargetId;spawn.Sequence=original.Sequence;spawn.Pose=SodaPose(soda.Root);Network.SendTo(spawn,snapshot.Peer);
  }
  if(Time.unscaledTime>=vendingGrantAt){vendingGrantAt=Time.unscaledTime+1;foreach(var order in vendingOrders.Values)if(order.TargetId!=Network.LocalId)Network.SendTo(order,order.TargetId);}
  // Bound serialization bursts as well as discovery; rotate fairly through
  // moving cans instead of allocating/sending 128 packets on a single frame.
  var sent=0;
  for(var n=0;n<sodaPoseKeys.Count&&sent<24;n++){
   if(sodaPoseCursor>=sodaPoseKeys.Count)sodaPoseCursor=0;var key=sodaPoseKeys[sodaPoseCursor++];
   var soda=sodaObjects[key];var position=soda.Root.position;var rotation=soda.Root.rotation;
   if(soda.Sent&&(position-soda.SentPosition).sqrMagnitude<.000025f&&Quaternion.Angle(rotation,soda.SentRotation)<.5f&&Time.unscaledTime-soda.PoseAt<5)continue;
   soda.Sent=true;soda.SentPosition=position;soda.SentRotation=rotation;soda.PoseAt=Time.unscaledTime;
   var p=Message("vendpose");p.ObjectId=key;p.Pose=new[]{position.x,position.y,position.z,rotation.x,rotation.y,rotation.z,rotation.w};Network.Send(p);sent++;
  }
 }
}
[HarmonyPatch(typeof(VendingMachine),nameof(VendingMachine.Interact))]
static class SharedVendingMachine {static bool Prefix(VendingMachine __instance)=>Together.Current?.BuySoda(__instance)??true;}
