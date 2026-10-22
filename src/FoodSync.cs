using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;
namespace FractureTogether;

// A cafe creates one host-owned plate. Payment and native healing stay personal.
public sealed partial class Together
{
 sealed class FoodActor {
  public string Id="",Vendor="";public GameObject Root=null!;public FastHealItem Heal=null!;
  public int HealId;public IntPtr HealPointer;public bool Published;public long Claim;
  public Vector3 SentPosition;public Quaternion SentRotation;public float PoseAt;public Packet? Spawn;
 }
 sealed class FoodOrder {
  public string Requested="";public Packet Grant=null!;public bool Closed;public float RetryAt;
 }
 sealed class FoodPending {public Packet Request=null!;public float RetryAt;}
 sealed class FoodState {public long Sequence;public bool Taken;public string Vendor="";public Packet? Snapshot;}
 readonly Dictionary<string,CafeRegeneration> foodVendors=new(StringComparer.Ordinal);
 readonly Dictionary<string,FoodActor> foodActors=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,string> foodHealIds=new();
 readonly Dictionary<string,string> foodVendorSlots=new(StringComparer.Ordinal);
 readonly Dictionary<(int Peer,long Sequence),FoodOrder> foodOrders=new();
 readonly Dictionary<int,long> foodBusyPeers=new();readonly Dictionary<int,float> foodBuyerAt=new();
 readonly Dictionary<int,float> foodRequestAt=new(),foodDeniedReplyAt=new();readonly Dictionary<int,Packet> foodDenials=new();
 readonly Dictionary<int,long> foodRequestSequences=new();
 readonly Dictionary<(int Category,string Id,long Sequence),Packet> foodReceipts=new();
 readonly Dictionary<string,FoodState> foodStates=new(StringComparer.Ordinal);
 readonly List<string> foodStateHistory=new();readonly Dictionary<int,int> foodReplayCursors=new();readonly HashSet<int> foodReplayAgain=new();int foodReplayPeerCursor;
 readonly Dictionary<string,Packet> foodPendingSpawns=new(StringComparer.Ordinal),foodPendingPoses=new(StringComparer.Ordinal);
 readonly Queue<string> foodSpawnQueue=new();
 readonly Dictionary<(int Peer,string Id),Packet> foodPeerStates=new();readonly Queue<(int Peer,string Id)> foodStateQueue=new();
 readonly HashSet<int> foodReadyPeers=new();
 FoodPending? foodPendingBuy,foodPendingTake;
 string foodEpoch="";long foodLocalSequence,foodHostSequence;float foodBuyAt,foodDebitAt,foodPoseAt,foodSpawnAt;int foodPoseCursor;

 [HideFromIl2Cpp]
 internal void RegisterFoodVendor(CafeRegeneration cafe){
  try{
   if(!cafe||!cafe.gameObject.scene.IsValid())return;
   var cafeScene=cafe.gameObject.scene.name;
   foreach(var entry in foodVendors.ToArray())try{if(!entry.Value||entry.Value.gameObject.scene.name!=cafeScene)foodVendors.Remove(entry.Key);}catch(Exception error){foodVendors.Remove(entry.Key);GameplayLog.Fault("food_vendor_stale",error);}
   var key=SpatialKey(cafe.transform);if(key.Length>128)return;
   if(foodVendors.Count<32||foodVendors.ContainsKey(key))foodVendors[key]=cafe;
  }catch(Exception error){GameplayLog.Fault("food_vendor",error);}
 }
 [HideFromIl2Cpp]
 CafeRegeneration? FoodVendor(string key)=>foodVendors.TryGetValue(key,out var cafe)&&cafe&&cafe.gameObject.activeInHierarchy&&cafe.gameObject.scene.name==scene?cafe:null;
 [HideFromIl2Cpp]
 bool FoodLocalAlive()=>player&&player.Health&&!player.Health.died&&player.Health.Health>0&&!Down&&!PartyWiped;
 [HideFromIl2Cpp]
 bool FoodReady()=>Network!=null&&Connected&&SameWorld&&RulesReady&&FoodLocalAlive()&&!returningAfterDisconnect&&!PersonalSavePending&&
  (scene is "Example_Scene_Night 1" or "URP_OfficeCorridor")&&SceneManager.GetActiveScene().name==scene&&world!=""&&
  !(elevator&&elevator.isMoving)&&!(FoodSceneLoadObservation.Loader&&FoodSceneLoadObservation.Loader._isLoading)&&
  (scene!="URP_OfficeCorridor"||SharedActorWorldReady);
 [HideFromIl2Cpp]
 bool FoodHostReady()=>Network?.Hosting==true&&Connected&&SameWorld&&RulesReady&&!PartyWiped&&!returningAfterDisconnect&&!PersonalSavePending&&
  (scene is "Example_Scene_Night 1" or "URP_OfficeCorridor")&&SceneManager.GetActiveScene().name==scene&&world!=""&&
  !(elevator&&elevator.isMoving)&&!(FoodSceneLoadObservation.Loader&&FoodSceneLoadObservation.Loader._isLoading)&&
  (scene!="URP_OfficeCorridor"||SharedActorWorldReady);
 [HideFromIl2Cpp]
 bool FoodPeerPosition(int id,out Vector3 point){
  point=Vector3.zero;if(Network==null)return false;
  if(id==Network.LocalId){if(!FoodLocalAlive())return false;point=player!.transform.position;return true;}
  if(!remotes.TryGetValue(id,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0||downPlayers.Contains(id))return false;
  var p=peer.State.Pose;point=new Vector3(p[0],p[1],p[2]);return true;
 }
 [HideFromIl2Cpp]
 bool FoodCafeSupported(CafeRegeneration? cafe){
  if(!cafe||!cafe!.gameObject.activeInHierarchy||cafe.gameObject.scene.name!=scene||!cafe.FoodPrefab||!cafe.FoodSpawnPoint||!cafe.SpawnSound||!cafe.Inventory||cafe.Inventory.gameObject.scene.name!=scene||cafe.price<0||cafe.price>FoodWire.MaximumPrice)return false;
  if(cafe.FoodSpawnPoint.gameObject.scene.name!=scene)return false;
  var heal=cafe.FoodPrefab.GetComponentsInChildren<FastHealItem>(true);
  if(heal.Length!=1||!heal[0]||heal[0].healAmount<0||heal[0].healAmount>1000000)return false;
  return FoodPoseValid(FoodNativePose(cafe));
 }
 [HideFromIl2Cpp]
 static float[] FoodNativePose(CafeRegeneration cafe){var p=cafe.FoodSpawnPoint.position;var q=cafe.FoodPrefab.transform.rotation;return new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w};}
 [HideFromIl2Cpp]
 static float[] FoodPose(Transform t){var p=t.position;var q=t.rotation;return new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w};}
 [HideFromIl2Cpp]
 static bool FoodPoseValid(float[] p)=>p.Length==7&&p.Take(3).All(x=>float.IsFinite(x)&&Math.Abs(x)<=1000000)&&UnitFrame.Rotation(p,3);
 [HideFromIl2Cpp]
 static void FoodSetPose(GameObject root,float[] p)=>root.transform.SetPositionAndRotation(new Vector3(p[0],p[1],p[2]),new Quaternion(p[3],p[4],p[5],p[6]));
 [HideFromIl2Cpp]
 bool EnsureFoodEpoch(){
  if(Network==null)return false;
  var epoch=$"{Network.Seed}:{scene}:{floor}:{world}";
  if(foodEpoch!=epoch){FoodReset(Network.Hosting);foodEpoch=epoch;}return true;
 }
 [HideFromIl2Cpp]
 internal void FoodReset(bool hosting){
  foreach(var actor in foodActors.Values.ToArray())FoodDestroy(actor);
  foodActors.Clear();foodHealIds.Clear();foodVendorSlots.Clear();foodOrders.Clear();foodBusyPeers.Clear();foodBuyerAt.Clear();foodRequestAt.Clear();foodDeniedReplyAt.Clear();foodDenials.Clear();foodRequestSequences.Clear();
  foodReceipts.Clear();foodStates.Clear();foodStateHistory.Clear();foodReplayCursors.Clear();foodReplayAgain.Clear();foodReplayPeerCursor=0;foodPendingSpawns.Clear();foodPendingPoses.Clear();foodSpawnQueue.Clear();foodPeerStates.Clear();foodStateQueue.Clear();foodReadyPeers.Clear();
  foodPendingBuy=foodPendingTake=null;foodEpoch="";foodLocalSequence=foodHostSequence=0;foodBuyAt=foodDebitAt=foodPoseAt=foodSpawnAt=0;foodPoseCursor=0;
  // Cafe.Start can precede connecting. Retain live sources, never retain old food.
  var actualScene=scene;try{actualScene=SceneManager.GetActiveScene().name;}catch(Exception error){GameplayLog.Fault("food_cleanup_scene",error);}
  foreach(var entry in foodVendors.ToArray())try{if(!entry.Value||entry.Value.gameObject.scene.name!=actualScene)foodVendors.Remove(entry.Key);}catch(Exception error){foodVendors.Remove(entry.Key);GameplayLog.Fault("food_vendor_prune",error);}
 }
 [HideFromIl2Cpp]
 void FoodDestroy(FoodActor actor){
  foodHealIds.Remove(actor.HealPointer);
  if(stableLootKeys.TryGetValue(actor.HealId,out var key)&&key==actor.Id)stableLootKeys.Remove(actor.HealId);
  if(lootObjects.TryGetValue(actor.Id,out var item)&&(ReferenceEquals(item,null)||item.Pointer==actor.HealPointer))lootObjects.Remove(actor.Id);
  try{if(actor.Root)actor.Root.SetActive(false);}catch(Exception error){GameplayLog.Fault("food_hide_cleanup",error);}
  // A stale hide call must not prevent attempting destruction of this actor or
  // cleaning the remaining actors during unrelated disconnect/save teardown.
  try{if(actor.Root)Object.Destroy(actor.Root);}catch(Exception error){GameplayLog.Fault("food_destroy",error);}
 }
 [HideFromIl2Cpp]
 FoodActor? FoodCreate(string id,string vendor,CafeRegeneration cafe,float[] pose,bool reserved){
  GameObject? root=null;
  try{
   root=Object.Instantiate(cafe.FoodPrefab,new Vector3(pose[0],pose[1],pose[2]),new Quaternion(pose[3],pose[4],pose[5],pose[6]));
   if(!root)return null;root.SetActive(false);if(root.scene.name!=scene)throw new InvalidOperationException("Food clone changed scene");
   var heals=root.GetComponentsInChildren<FastHealItem>(true);if(heals.Length!=1||!heals[0])throw new InvalidOperationException("Unsupported food clone");
   var heal=heals[0];var actor=new FoodActor{Id=id,Vendor=vendor,Root=root,Heal=heal,HealId=heal.GetInstanceID(),HealPointer=heal.Pointer};
   foodActors[id]=actor;foodHealIds[actor.HealPointer]=id;
   // Exclude the tracked plate from generic coordinate-based pickup identities.
   stableLootKeys[actor.HealId]=id;lootObjects[id]=heal;
   if(Network?.Hosting==false)foreach(var body in root.GetComponentsInChildren<Rigidbody>(true)){body.velocity=Vector3.zero;body.angularVelocity=Vector3.zero;body.isKinematic=true;body.useGravity=false;}
   if(!reserved)root.SetActive(true);return actor;
  }catch(Exception error){GameplayLog.Fault("food_clone",error);if(foodActors.Remove(id,out var actor))FoodDestroy(actor);else if(root){try{root.SetActive(false);Object.Destroy(root);}catch(Exception cleanup){GameplayLog.Fault("food_clone_cleanup",cleanup);}}return null;}
 }
 [HideFromIl2Cpp]
 void FoodSend(Packet packet,int peer=-1){
  if(!FoodWire.Valid(packet)){GameplayLog.Record("food_invalid_outgoing",new{kind=packet.Kind,category=packet.Category});return;}
  try{if(Network==null)return;if(peer<0)Network.Send(packet);else Network.SendTo(packet,peer);}catch(Exception error){GameplayLog.Fault("food_send",error);}
 }
 [HideFromIl2Cpp]
 void FoodSendGrant(Packet grant){if(grant.TargetId==Network?.LocalId)FoodExecuteGrant(grant);else FoodSend(grant,grant.TargetId);}
 [HideFromIl2Cpp]
 void FoodSendReceipt(Packet ack){if(Network?.Hosting==true)FoodAcknowledge(ack);else FoodSend(ack);}
 [HideFromIl2Cpp]
 void FoodSendRequest(Packet request){if(Network?.Hosting==true)FoodRequest(request);else FoodSend(request);}

 [HideFromIl2Cpp]
 internal bool BuyFood(CafeRegeneration cafe){
  if(!FoodWire.Integrated||Replay||!Connected)return true;
  try{
   EnsureFoodEpoch();RegisterFoodVendor(cafe);
   if(!FoodReady()||foodPendingBuy!=null||foodPendingTake!=null||Time.unscaledTime<foodBuyAt||foodReceipts.Count>=FoodWire.MaximumTransactions||!FoodCafeSupported(cafe)||FoodVendor(SpatialKey(cafe.transform))?.Pointer!=cafe.Pointer||Vector3.Distance(player!.transform.position,cafe.transform.position)>=7){detail="Cafe is waiting for a living player, a matching world, or the previous order.";return false;}
   var money=cafe.Inventory.DollarsCount;if(!float.IsFinite(money)||money<cafe.price){detail="Not enough money.";return false;}
   var request=Message("foodreq");request.Category=0;request.ObjectId=SpatialKey(cafe.transform);request.Sequence=++foodLocalSequence;request.PlayerId=Network!.LocalId;
   foodBuyAt=Time.unscaledTime+1;foodPendingBuy=new FoodPending{Request=request,RetryAt=Time.unscaledTime+1};FoodSendRequest(request);
  }catch(Exception error){GameplayLog.Fault("food_buy",error);detail="Cafe purchase could not be prepared.";}return false;
 }
 [HideFromIl2Cpp]
 internal bool TryFoodPickup(Component component,int category,out bool runNative){
  runNative=false;if(!FoodWire.Integrated||category!=2||!component||!foodHealIds.TryGetValue(component.Pointer,out var id))return false;
  if(Replay){runNative=true;return true;}
  try{
   EnsureFoodEpoch();
   if(!FoodReady()||foodPendingTake!=null||foodPendingBuy!=null||foodReceipts.Count>=FoodWire.MaximumTransactions||!foodActors.TryGetValue(id,out var actor)||!actor.Published||!actor.Root||!actor.Root.activeInHierarchy||Vector3.Distance(player!.transform.position,actor.Root.transform.position)>=7)return true;
   var request=Message("foodreq");request.Category=1;request.ObjectId=id;request.Sequence=++foodLocalSequence;request.PlayerId=Network!.LocalId;
   foodPendingTake=new FoodPending{Request=request,RetryAt=Time.unscaledTime+1};FoodSendRequest(request);
  }catch(Exception error){GameplayLog.Fault("food_pickup",error);}return true;
 }
 [HideFromIl2Cpp]
 bool FoodCapacity(){
  var peers=Math.Max(1,(Network?.PlayerCount??1)-1);
  // A this-capturing ValueTuple lambda would become an extra instance method
  // on this injected MonoBehaviour. Keep this managed ledger traversal plain.
  var retiredPending=0;foreach(var key in foodPeerStates.Keys)if(!foodActors.ContainsKey(key.Id))retiredPending++;
  return foodActors.Count<FoodWire.MaximumLive&&retiredPending+(foodActors.Count+1)*peers<=64;
 }
 [HideFromIl2Cpp]
 void FoodReject(Packet request,string vendor=""){
  var grant=Message("foodgrant");grant.Category=request.Category;grant.Sequence=request.Sequence;grant.TargetId=request.PlayerId;
  grant.ObjectId=request.Category==0?"":request.ObjectId;grant.MachineId=request.Category==0?request.ObjectId:vendor;
  foodDenials[request.PlayerId]=grant;
  if(Time.unscaledTime>=foodDeniedReplyAt.GetValueOrDefault(request.PlayerId)){foodDeniedReplyAt[request.PlayerId]=Time.unscaledTime+1;FoodSendGrant(grant);}
 }
 [HideFromIl2Cpp]
 void FoodRequest(Packet request){
  if(Network?.Hosting!=true||!FoodWire.Valid(request)||!PacketWorld(request)||request.Seed!=Network.Seed)return;
  if(request.PlayerId!=Network.LocalId&&!remotes.ContainsKey(request.PlayerId))return;
  var key=(request.PlayerId,request.Sequence);
  if(foodOrders.TryGetValue(key,out var previous)){
   if(previous.Requested==request.ObjectId&&previous.Grant.Category==request.Category&&Time.unscaledTime>=previous.RetryAt){previous.RetryAt=Time.unscaledTime+1;FoodSendGrant(previous.Grant);}return;
  }
  if(foodDenials.TryGetValue(request.PlayerId,out var denial)&&denial.Sequence==request.Sequence){
   if(denial.Category==request.Category&&(request.Category==0?denial.MachineId==request.ObjectId:denial.ObjectId==request.ObjectId)&&Time.unscaledTime>=foodDeniedReplyAt.GetValueOrDefault(request.PlayerId)){foodDeniedReplyAt[request.PlayerId]=Time.unscaledTime+1;FoodSendGrant(denial);}return;
  }
  // Invalid clicks cannot fill the permanent payment/consumption ledger. Rate
  // every new request before native inspection; keep one recent denial per peer.
  if(Time.unscaledTime<foodRequestAt.GetValueOrDefault(request.PlayerId)){FoodReject(request);return;}
  foodRequestAt[request.PlayerId]=Time.unscaledTime+1;
  if(request.Sequence<=foodRequestSequences.GetValueOrDefault(request.PlayerId))return;foodRequestSequences[request.PlayerId]=request.Sequence;
  if(foodOrders.Count>=FoodWire.MaximumTransactions){FoodReject(request);return;}
  var order=new FoodOrder{Requested=request.ObjectId,Closed=true};
  var denied=Message("foodgrant");denied.Category=request.Category;denied.Sequence=request.Sequence;denied.TargetId=request.PlayerId;denied.ObjectId=request.Category==0?"":request.ObjectId;denied.MachineId=request.Category==0?request.ObjectId:"";order.Grant=denied;
  try{
   if(!FoodHostReady()||!FoodPeerPosition(request.PlayerId,out var from)||foodBusyPeers.ContainsKey(request.PlayerId)){FoodReject(request);return;}
   if(request.Category==0){
    var cafe=FoodVendor(request.ObjectId);
    if(Time.unscaledTime<foodBuyerAt.GetValueOrDefault(request.PlayerId)||!FoodCafeSupported(cafe)||foodVendorSlots.ContainsKey(request.ObjectId)||!FoodCapacity()||Vector3.Distance(from,cafe!.transform.position)>=7){FoodReject(request);return;}
    var id="food:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(foodEpoch+":"+request.PlayerId+":"+request.Sequence)))[..32];
    var actor=FoodCreate(id,request.ObjectId,cafe!,FoodNativePose(cafe!),true);if(actor==null){FoodReject(request);return;}
    foodVendorSlots[actor.Vendor]=id;denied.ObjectId=id;denied.Count=cafe!.price;foodBuyerAt[request.PlayerId]=Time.unscaledTime+1;
   }else{
    if(!foodActors.TryGetValue(request.ObjectId,out var actor)||!actor.Published||actor.Claim!=0||!actor.Root||!actor.Root.activeInHierarchy||Vector3.Distance(from,actor.Root.transform.position)>=7){FoodReject(request);return;}
    actor.Claim=request.Sequence;denied.MachineId=actor.Vendor;
   }
   denied.Success=true;order.Closed=false;order.RetryAt=Time.unscaledTime+1;foodOrders[key]=order;foodBusyPeers[request.PlayerId]=request.Sequence;FoodSendGrant(denied);
  }catch(Exception error){GameplayLog.Fault("food_request",error);FoodCloseOrder(order,request.PlayerId,false);denied.Success=false;denied.Count=0;FoodSendGrant(denied);}
 }
 [HideFromIl2Cpp]
 void FoodExecuteGrant(Packet grant){
  if(Network==null||!FoodWire.Valid(grant)||!PacketWorld(grant)||grant.Seed!=Network.Seed||grant.TargetId!=Network.LocalId)return;
  var receiptKey=(grant.Category,grant.ObjectId,grant.Sequence);
  if(foodReceipts.TryGetValue(receiptKey,out var old)){if(old.MachineId==grant.MachineId)FoodSendReceipt(old);return;}
  var pending=grant.Category==0?foodPendingBuy:foodPendingTake;
  if(pending==null||pending.Request.Sequence!=grant.Sequence||(grant.Category==0?pending.Request.ObjectId!=grant.MachineId:pending.Request.ObjectId!=grant.ObjectId))return;
  if(grant.Category==0)foodPendingBuy=null;else foodPendingTake=null;
  if(!grant.Success){detail="Cafe action refused: plate already present, reserved, or unavailable.";return;}
  if(foodReceipts.Count>=FoodWire.MaximumTransactions)return;
  var ack=Message("foodack");ack.Category=grant.Category;ack.ObjectId=grant.ObjectId;ack.MachineId=grant.MachineId;ack.Sequence=grant.Sequence;ack.PlayerId=Network.LocalId;
  // Cache the immutable operation identity before a wallet setter or native heal.
  foodReceipts[receiptKey]=ack;
  try{
   if(!FoodReady()){FoodSendReceipt(ack);return;}
   if(grant.Category==0){
    var cafe=FoodVendor(grant.MachineId);
    if(!FoodCafeSupported(cafe)||cafe!.price!=grant.Count||Vector3.Distance(player!.transform.position,cafe.transform.position)>=7||Time.unscaledTime<foodDebitAt){FoodSendReceipt(ack);return;}
    var before=cafe.Inventory.DollarsCount;if(!float.IsFinite(before)||before<cafe.price){detail="Not enough money.";FoodSendReceipt(ack);return;}
    // Native setter entry is the uncertain-payment boundary. Never debit a replay.
    ack.Success=true;foodDebitAt=Time.unscaledTime+1;cafe.Inventory.DollarsCount=before-cafe.price;
    try{cafe.SpawnSound.Play();}catch(Exception error){GameplayLog.Fault("food_purchase_sound",error);}
   }else{
    if(!foodActors.TryGetValue(grant.ObjectId,out var actor)||actor.Vendor!=grant.MachineId||!actor.Published||!actor.Root||!actor.Root.activeInHierarchy||!actor.Heal||Vector3.Distance(player!.transform.position,actor.Root.transform.position)>=7){FoodSendReceipt(ack);return;}
    // FastHeal may throw after adding HP. Once entered, consume/quarantine once.
    ack.Success=true;var replay=Replay;Replay=true;
    try{actor.Heal.Interact();}finally{Replay=replay;try{if(actor.Root)actor.Root.SetActive(false);}catch(Exception error){GameplayLog.Fault("food_consume_hide",error);}}
   }
  }catch(Exception error){GameplayLog.Fault(grant.Category==0?"food_payment_uncertain":"food_consume_uncertain",error);detail="Cafe action entered native code; its receipt is retained.";}
  FoodSendReceipt(ack);
 }
 [HideFromIl2Cpp]
 void FoodCloseOrder(FoodOrder order,int peer,bool consumed){
  order.Closed=true;if(foodBusyPeers.GetValueOrDefault(peer)==order.Grant.Sequence)foodBusyPeers.Remove(peer);
  var grant=order.Grant;
  if(grant.Category==0){if(foodActors.TryGetValue(grant.ObjectId,out var actor)&&!actor.Published){foodActors.Remove(actor.Id);foodVendorSlots.Remove(actor.Vendor);FoodDestroy(actor);}}
  else if(foodActors.TryGetValue(grant.ObjectId,out var actor)){if(consumed)FoodTaken(actor,peer);else actor.Claim=0;}
 }
 [HideFromIl2Cpp]
 void FoodAcknowledge(Packet ack){
  if(Network?.Hosting!=true||!FoodWire.Valid(ack)||!PacketWorld(ack)||ack.Seed!=Network.Seed||!foodOrders.TryGetValue((ack.PlayerId,ack.Sequence),out var order))return;
  var grant=order.Grant;if(order.Closed||!grant.Success||grant.Category!=ack.Category||grant.ObjectId!=ack.ObjectId||grant.MachineId!=ack.MachineId)return;
  try{
   if(!ack.Success){FoodCloseOrder(order,ack.PlayerId,false);return;}
   if(grant.Category==1){FoodCloseOrder(order,ack.PlayerId,true);return;}
   if(!foodActors.TryGetValue(ack.ObjectId,out var actor)||!actor.Root)return;
   if(!actor.Published)actor.Root.SetActive(true);
   if(actor.Spawn==null){var spawn=Message("foodstate");spawn.Category=0;spawn.ObjectId=actor.Id;spawn.MachineId=actor.Vendor;spawn.TargetId=ack.PlayerId;spawn.Sequence=++foodHostSequence;spawn.Pose=FoodPose(actor.Root.transform);actor.Spawn=spawn;}
   actor.Published=true;FoodRememberState(actor.Spawn);FoodBroadcastState(actor.Spawn);
   order.Closed=true;foodBusyPeers.Remove(ack.PlayerId);
   // Payer already played the native purchase sound after its personal debit.
   if(ack.PlayerId!=Network.LocalId){var cafe=FoodVendor(actor.Vendor);if(cafe&&cafe.SpawnSound)cafe.SpawnSound.Play();}
  }catch(Exception error){GameplayLog.Fault("food_publish",error);/* Keep the paid reservation and identity; never debit or clone again. */}
 }
 [HideFromIl2Cpp]
 void FoodTaken(FoodActor actor,int eater){
  var state=Message("foodstate");state.Category=1;state.ObjectId=actor.Id;state.MachineId=actor.Vendor;state.TargetId=eater;state.Sequence=++foodHostSequence;
  FoodRememberState(state);FoodBroadcastState(state);
  foodActors.Remove(actor.Id);foodVendorSlots.Remove(actor.Vendor);FoodDestroy(actor);
 }
 [HideFromIl2Cpp]
 bool FoodQueueState(int peer,Packet state){
  var key=(peer,state.ObjectId);if(!foodPeerStates.ContainsKey(key)&&foodPeerStates.Count>=64)return false;
  if(!foodPeerStates.ContainsKey(key))foodStateQueue.Enqueue(key);foodPeerStates[key]=state;return true;
 }
 [HideFromIl2Cpp]
 void FoodRememberState(Packet packet){
  if(!foodStates.ContainsKey(packet.ObjectId)){if(foodStates.Count>=FoodWire.MaximumTransactions)return;foodStateHistory.Add(packet.ObjectId);}
  foodStates[packet.ObjectId]=new FoodState{Sequence=packet.Sequence,Taken=packet.Category==1,Vendor=packet.MachineId,Snapshot=packet};
 }
 [HideFromIl2Cpp]
 void FoodBroadcastState(Packet state){
  foreach(var peer in remotes.Values)if(PeerMatches(peer)&&!FoodQueueState(peer.Id,state)){
   foodReadyPeers.Remove(peer.Id);if(!foodReplayCursors.ContainsKey(peer.Id))foodReplayCursors[peer.Id]=0;else foodReplayAgain.Add(peer.Id);
  }
 }
 [HideFromIl2Cpp]
 void FoodReplayStates(){
  // Every re-ready peer receives live plates AND terminal tombstones. Walk the
  // bounded world ledger lazily, never enqueue its 512 entries all at once.
  foreach(var id in foodReadyPeers.Concat(foodReplayCursors.Keys).Distinct().ToArray())if(!remotes.TryGetValue(id,out var peer)||!PeerMatches(peer)){foodReadyPeers.Remove(id);foodReplayCursors.Remove(id);foodReplayAgain.Remove(id);}
  var peers=remotes.Values.Where(PeerMatches).Select(p=>p.Id).OrderBy(id=>id).ToArray();
  foreach(var id in peers)if(!foodReadyPeers.Contains(id)&&!foodReplayCursors.ContainsKey(id))foodReplayCursors[id]=0;
  for(var n=0;n<8&&peers.Length>0;n++){
   if(foodReplayPeerCursor>=peers.Length)foodReplayPeerCursor=0;var id=peers[foodReplayPeerCursor++];
   if(!foodReplayCursors.TryGetValue(id,out var cursor))continue;
   if(cursor>=foodStateHistory.Count){if(foodReplayAgain.Remove(id))foodReplayCursors[id]=0;else{foodReplayCursors.Remove(id);foodReadyPeers.Add(id);}continue;}
   if(!foodStates.TryGetValue(foodStateHistory[cursor],out var state)||state.Snapshot==null){foodReplayCursors[id]=cursor+1;continue;}
   if(FoodQueueState(id,state.Snapshot))foodReplayCursors[id]=cursor+1;
  }
 }
 [HideFromIl2Cpp]
 void FoodApplyState(Packet state){
  if(foodStates.TryGetValue(state.ObjectId,out var known)&&(known.Taken||state.Sequence<=known.Sequence))return;
  if(!foodStates.ContainsKey(state.ObjectId)&&foodStates.Count>=FoodWire.MaximumTransactions)return;
  foodStates[state.ObjectId]=new FoodState{Sequence=state.Sequence,Taken=state.Category==1,Vendor=state.MachineId};
  if(state.Category==1){
   foodPendingSpawns.Remove(state.ObjectId);foodPendingPoses.Remove(state.ObjectId);FoodPruneSpawnQueue();
   if(foodActors.Remove(state.ObjectId,out var actor)){foodVendorSlots.Remove(actor.Vendor);FoodDestroy(actor);}
   if(foodPendingTake?.Request.ObjectId==state.ObjectId)foodPendingTake=null;return;
  }
  if(FoodSpawnReplica(state))return;
  if(foodPendingSpawns.Count<64||foodPendingSpawns.ContainsKey(state.ObjectId)){
   if(!foodPendingSpawns.ContainsKey(state.ObjectId)){if(foodSpawnQueue.Count>=64)FoodPruneSpawnQueue();if(foodSpawnQueue.Count>=64)return;foodSpawnQueue.Enqueue(state.ObjectId);}foodPendingSpawns[state.ObjectId]=state;
  }
 }
 [HideFromIl2Cpp]
 void FoodPruneSpawnQueue(){var ids=foodSpawnQueue.Where(foodPendingSpawns.ContainsKey).Distinct(StringComparer.Ordinal).Take(64).ToArray();foodSpawnQueue.Clear();foreach(var id in ids)foodSpawnQueue.Enqueue(id);}
 [HideFromIl2Cpp]
 bool FoodSpawnReplica(Packet spawn){
  if(foodStates.TryGetValue(spawn.ObjectId,out var state)&&state.Taken)return true;
  if(foodActors.TryGetValue(spawn.ObjectId,out var existing)){
   if(existing.Vendor!=spawn.MachineId)return true;if(existing.Root)FoodSetPose(existing.Root,spawn.Pose);return true;
  }
  var cafe=FoodVendor(spawn.MachineId);if(!FoodCafeSupported(cafe)||foodActors.Count>=FoodWire.MaximumLive)return false;
  if(foodVendorSlots.TryGetValue(spawn.MachineId,out var occupied)&&occupied!=spawn.ObjectId&&foodActors.ContainsKey(occupied))return false;
  var actor=FoodCreate(spawn.ObjectId,spawn.MachineId,cafe!,spawn.Pose,false);if(actor==null)return false;
  actor.Published=true;actor.Spawn=spawn;foodVendorSlots[actor.Vendor]=actor.Id;
  if(foodPendingPoses.Remove(actor.Id,out var pose)&&pose.Sequence>spawn.Sequence&&actor.Root)FoodSetPose(actor.Root,pose.Pose);
  if(spawn.TargetId!=Network!.LocalId){try{cafe!.SpawnSound.Play();}catch(Exception error){GameplayLog.Fault("food_replica_sound",error);}}
  foodPendingSpawns.Remove(actor.Id);return true;
 }
 [HideFromIl2Cpp]
 internal bool ReceiveFood(Packet packet){
  if(!packet.Kind.StartsWith("food",StringComparison.Ordinal))return false;
  if(!FoodWire.Integrated)return true;
  if(Network==null||!FoodWire.Valid(packet)||!PacketWorld(packet)||packet.Seed!=Network.Seed)return true;
  try{
   EnsureFoodEpoch();
   if(Network.Hosting){if(packet.Kind=="foodreq")FoodRequest(packet);else if(packet.Kind=="foodack")FoodAcknowledge(packet);}
   else if(packet.PlayerId==0){
    if(packet.Kind=="foodgrant")FoodExecuteGrant(packet);
    else if(packet.Kind=="foodstate")FoodApplyState(packet);
    else if(packet.Kind=="foodpose"){
     if(foodStates.TryGetValue(packet.ObjectId,out var state)){
      if(state.Taken||packet.Sequence<=state.Sequence)return true;state.Sequence=packet.Sequence;
      if(foodActors.TryGetValue(packet.ObjectId,out var actor)&&actor.Root){FoodSetPose(actor.Root,packet.Pose);return true;}
     }
     if(foodPendingPoses.Count<64||foodPendingPoses.ContainsKey(packet.ObjectId))if(!foodPendingPoses.TryGetValue(packet.ObjectId,out var prior)||packet.Sequence>prior.Sequence)foodPendingPoses[packet.ObjectId]=packet;
    }
   }
  }catch(Exception error){GameplayLog.Fault("food_receive",error);}return true;
 }
 [HideFromIl2Cpp]
 internal void FoodPeerLeft(int id){
  foodReadyPeers.Remove(id);foodReplayCursors.Remove(id);foodReplayAgain.Remove(id);foodBuyerAt.Remove(id);foodRequestAt.Remove(id);foodDeniedReplyAt.Remove(id);foodDenials.Remove(id);foodRequestSequences.Remove(id);
  foreach(var key in foodPeerStates.Keys.Where(k=>k.Peer==id).ToArray())foodPeerStates.Remove(key);
  var queued=foodStateQueue.Where(foodPeerStates.ContainsKey).Distinct().Take(64).ToArray();foodStateQueue.Clear();foreach(var key in queued)foodStateQueue.Enqueue(key);
  if(Network?.Hosting!=true)return;
  foreach(var order in foodOrders.Values.Where(o=>!o.Closed&&o.Grant.TargetId==id).ToArray()){
   // A disconnected eater may already have received HP. Never release its plate.
   FoodCloseOrder(order,id,order.Grant.Category==1);order.Grant.Success=false;order.Grant.Count=0;
  }
  foodBusyPeers.Remove(id);
 }
 [HideFromIl2Cpp]
 internal void FoodTick(){
  if(!FoodWire.Integrated)return;
  try{
   if(Network==null){if(foodEpoch!="")FoodReset(false);return;}EnsureFoodEpoch();var now=Time.unscaledTime;
   foreach(var pending in new[]{foodPendingBuy,foodPendingTake})if(pending!=null&&now>=pending.RetryAt){pending.RetryAt=now+1;if(Connected)FoodSendRequest(pending.Request);}
   if(Network.Hosting){
    foreach(var order in foodOrders.Values.Where(o=>!o.Closed&&now>=o.RetryAt).Take(4).ToArray()){order.RetryAt=now+1;FoodSendGrant(order.Grant);}
    FoodReplayStates();
    for(var n=0;n<8&&foodStateQueue.Count>0;n++){
     var key=foodStateQueue.Dequeue();if(!foodPeerStates.Remove(key,out var state))continue;
     if(remotes.TryGetValue(key.Peer,out var peer)&&PeerMatches(peer))FoodSend(state,key.Peer);
     else{foodReadyPeers.Remove(key.Peer);foodReplayCursors.Remove(key.Peer);foodReplayAgain.Remove(key.Peer);}
    }
    if(now>=foodPoseAt){foodPoseAt=now+.25f;var actors=foodActors.Values.Where(a=>a.Published&&a.Root).ToArray();var sent=0;
     for(var visited=0;visited<actors.Length&&sent<4;visited++){
      if(foodPoseCursor>=actors.Length)foodPoseCursor=0;var actor=actors[foodPoseCursor++];var root=actor.Root.transform;var position=root.position;var rotation=root.rotation;
      if(now<actor.PoseAt&&(position-actor.SentPosition).sqrMagnitude<.0001f&&Quaternion.Angle(rotation,actor.SentRotation)<.5f)continue;
      var pose=Message("foodpose");pose.ObjectId=actor.Id;pose.Sequence=++foodHostSequence;pose.Pose=FoodPose(root);
      if(!FoodPoseValid(pose.Pose))continue;actor.SentPosition=position;actor.SentRotation=rotation;actor.PoseAt=now+5;sent++;
      if(actor.Spawn!=null){actor.Spawn.Pose=pose.Pose;actor.Spawn.Sequence=pose.Sequence;}
      foreach(var peer in remotes.Values)if(PeerMatches(peer))FoodSend(pose,peer.Id);
     }
    }
   }else if(now>=foodSpawnAt){
    foodSpawnAt=now+1;for(var n=Math.Min(2,foodSpawnQueue.Count);n>0;n--){var id=foodSpawnQueue.Dequeue();if(!foodPendingSpawns.TryGetValue(id,out var spawn))continue;if(FoodSpawnReplica(spawn))foodPendingSpawns.Remove(id);else foodSpawnQueue.Enqueue(id);}
   }
  }catch(Exception error){GameplayLog.Fault("food_tick",error);}
 }
}

[HarmonyPatch(typeof(CafeRegeneration),"Start")]
static class RememberFoodCafe {static void Postfix(CafeRegeneration __instance)=>Together.Current?.RegisterFoodVendor(__instance);}
[HarmonyPatch(typeof(CafeRegeneration),nameof(CafeRegeneration.Interact))]
static class SharedCafePurchase {static bool Prefix(CafeRegeneration __instance)=>Together.Current?.BuyFood(__instance)??true;}
[HarmonyPatch]
static class FoodSceneLoadObservation {
 internal static SceneLoadingManager? Loader;
 static IEnumerable<System.Reflection.MethodBase> TargetMethods(){yield return AccessTools.Method(typeof(SceneLoadingManager),nameof(SceneLoadingManager.LoadScene));yield return AccessTools.Method(typeof(SceneLoadingManager),nameof(SceneLoadingManager.LoadSceneImmediately));}
 static void Prefix(SceneLoadingManager __instance)=>Loader=__instance;
}
