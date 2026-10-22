using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using UnityEngine;
using Object=UnityEngine.Object;

namespace FractureTogether;

public sealed partial class Together
{
 public int RunPlayers=1;
 public bool SharedRun=>RunPlayers>1&&scene=="URP_OfficeCorridor"&&floor>=0;
 public int LootFactor=>Rules.Loot(RunPlayers);
 readonly Dictionary<string,Component> lootObjects=new();
 readonly Dictionary<int,string> stableLootKeys=new();
 readonly Dictionary<int,(Component Object,string Key)> initialLootKeys=new();
 readonly Dictionary<string,Packet> reservations=new();
 readonly HashSet<string> requestedLoot=new();
 readonly Dictionary<string,Packet> completedGrants=new();
 readonly Dictionary<string,LootEntry[]> containerStates=new();
 readonly SharedLootLedger takenLoot=new();
 readonly Dictionary<IntPtr,(Component Object,int Category)> knownLoot=new();
 readonly Queue<IntPtr> pendingLoot=new();readonly HashSet<IntPtr> queuedLoot=new();
 readonly Dictionary<(int Player,string Key),(Packet Request,float Until)> pendingLootClaims=new();
 readonly HashSet<(int Player,string Key)> lootRebindOffered=new();
 readonly HashSet<string> lootRebindAccepted=new(StringComparer.Ordinal);
 readonly HashSet<IntPtr> nearbyLootSeen=new();
 readonly List<(Component Object,string Key,Vector3 Initial)> nearbyLootDetails=new(96);
 enum LootResolution {Missing,Exact,Rebound}
 readonly Stack<Transform> lootBirthNodes=new();readonly HashSet<IntPtr> queuedLootNodes=new();
 Transform? lootExpanding;int lootChildIndex=-1;
 readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> nearbyLootColliders=new(96);
 string lootKnownScene="",lootDiscoveryWorld="",lootEpoch="";int lootDiscoveryRevision=-1;float grantResendAt;
 long claimSequence;
 float lootDiagnosticUntil;int lootDiagnosticCount;
 [HideFromIl2Cpp]
 bool LootIdentityReady(Component component,int category){
  if(DroppedIdentity(component)!=null)return true;
  var enemy=component.GetComponentInParent<EnemyAI.EnemyHealth>();
  if(!enemy||enemyIds.ContainsKey(enemy.Pointer))return true;
  QueueKnownLoot(component,category);return false;
 }
 [HideFromIl2Cpp]
 void LootDecision(Packet packet,string phase,string reason,Component? component=null,float distance=-1,string? candidateKey=null,Vector3? initial=null){
  if(!GameplayLog.Operational)return;var now=Time.realtimeSinceStartup;
  if(now>=lootDiagnosticUntil){lootDiagnosticUntil=now+1;lootDiagnosticCount=0;}if(lootDiagnosticCount++>=16)return;
  var item=component?component!.TryCast<LootableItem>():null;reservations.TryGetValue(packet.ObjectId,out var reservation);
  GameplayLog.Record("loot_decision",new{phase,reason,peer=packet.PlayerId,target=packet.TargetId,sequence=packet.Sequence,category=packet.Category,objectKey=DiagnosticObjectKey(packet.ObjectId),requestedItem=packet.ItemId,requestedType=packet.Count,requestedX=packet.Pose.Length==3?(float?)packet.Pose[0]:null,requestedY=packet.Pose.Length==3?(float?)packet.Pose[1]:null,requestedZ=packet.Pose.Length==3?(float?)packet.Pose[2]:null,bound=(bool)component,nativeItem=item?item!.ItemId:-1,nativeType=item?(int)item!.ItemType:-1,distance,reservedOwner=reservation?.TargetId??-1,reservedSequence=reservation?.Sequence??0,requested=requestedLoot.Contains(packet.ObjectId),candidateKey=candidateKey==null?null:DiagnosticObjectKey(candidateKey),initialX=initial?.x,initialY=initial?.y,initialZ=initial?.z,pathKey=component?DiagnosticObjectKey(ObjectKey(component!.transform)):null,packetFloor=packet.Floor,packetWorld=packet.World,scene,floor});
 }
 [HideFromIl2Cpp]
 float LootDistance(Vector3 from,Component component){
  if(EnemyLootAnchor(component) is {} corpse)return Vector3.Distance(from,corpse);
  // Reach is measured to the usable surface, not an offset prefab pivot.
  var distance=Vector3.Distance(from,component.transform.position);
  var colliders=component.GetComponentsInChildren<Collider>();
  for(var i=0;i<Math.Min(32,colliders.Length);i++){var collider=colliders[i];if(collider&&collider.enabled)distance=Math.Min(distance,Vector3.Distance(from,collider.bounds.ClosestPoint(from)));}
  return distance;
 }
 void LootPeerLeft(int id){
  foreach(var key in pendingLootClaims.Keys.Where(k=>k.Player==id).ToArray())pendingLootClaims.Remove(key);
  // An ACK can be lost when the connection closes after the native pickup.
  // Never release an uncertain grant to another player and duplicate the item.
  foreach(var grant in reservations.Values.Where(g=>g.TargetId==id).ToArray()){
   reservations.Remove(grant.ObjectId);takenLoot.TryClaim(grant.ObjectId,id);HideTaken(grant.ObjectId);
   var taken=Message("loottaken");taken.ObjectId=grant.ObjectId;taken.TargetId=id;taken.Category=grant.Category;Network?.Send(taken);
   Plugin.Instance.Log.LogWarning("Disconnected pickup owner; quarantined uncertain object "+grant.ObjectId);
  }
  foreach(var order in vendingOrders.Where(p=>p.Value.TargetId==id).Select(p=>p.Key).ToArray())vendingOrders.Remove(order);
 }
 [HideFromIl2Cpp]
 Packet Message(string kind)=>new(){Kind=kind,Scene=scene,Floor=floor,World=world,Seed=Network?.Seed??0};
 [HideFromIl2Cpp]
 bool PacketWorld(Packet p)=>p.Scene==scene&&p.Floor==floor&&p.World==world;
 [HideFromIl2Cpp]
 internal void DiscoverLootSubtree(Transform root){if(root&&queuedLootNodes.Add(root.Pointer))lootBirthNodes.Push(root);}
 void QueueKnownLoot(Component component,int category){
  if(!component)return;var pointer=component.Pointer;knownLoot[pointer]=(component,category);
  if(queuedLoot.Add(pointer))pendingLoot.Enqueue(pointer);
 }
 Transform? SharedBirthRoot(){
  if(elevator&&elevator.levelParent&&elevator.levelParent.childCount>0)return elevator.levelParent;
  var dungeon=DungeonSeedPatch.Latest;if(!dungeon)return null;
  var provider=dungeon.GetComponent<DungeonArchitect.DungeonSceneProvider>();return provider&&provider.itemParent?provider.itemParent.transform:null;
 }
 void PruneLootBirths(string activeScene){
  foreach(var key in knownLoot.Where(p=>!p.Value.Object||p.Value.Object.gameObject.scene.name!=activeScene).Select(p=>p.Key).ToArray())knownLoot.Remove(key);
  foreach(var key in initialLootKeys.Where(p=>!p.Value.Object||p.Value.Object.gameObject.scene.name!=activeScene).Select(p=>p.Key).ToArray())initialLootKeys.Remove(key);
  foreach(var key in lootObjects.Where(p=>!p.Value||p.Value.gameObject.scene.name!=activeScene).Select(p=>p.Key).ToArray())lootObjects.Remove(key);
  foreach(var key in stableLootKeys.Where(p=>!lootObjects.ContainsKey(p.Value)).Select(p=>p.Key).ToArray())stableLootKeys.Remove(key);
  pendingLoot.Clear();queuedLoot.Clear();lootBirthNodes.Clear();queuedLootNodes.Clear();lootExpanding=null;lootChildIndex=-1;pendingLootClaims.Clear();lootRebindOffered.Clear();lootRebindAccepted.Clear();nearbyLootSeen.Clear();nearbyLootDetails.Clear();
 }
 void DiscoverWorldBirths(){
  // ChanceItems and FastHealItem have no native Start/Awake/OnEnable hooks.
  // Walk only the actual generated root once per build, amortizing hierarchy
  // traversal and identity capture instead of running three scene-wide finds.
  if(lootDiscoveryWorld!=world||lootDiscoveryRevision!=DungeonSeedPatch.Revision){
   var root=SharedBirthRoot();if(root){lootDiscoveryWorld=world;lootDiscoveryRevision=DungeonSeedPatch.Revision;DiscoverLootSubtree(root);}
   else if(SharedActorWorldReady&&floor==0&&!DungeonSeedPatch.Latest){
    // The authored entrance street has no GeneratedLevel. Discover it once,
    // using the same bounded walker as generated rooms rather than scene finds.
    lootDiscoveryWorld=world;lootDiscoveryRevision=DungeonSeedPatch.Revision;
    foreach(var streetRoot in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
     if(streetRoot&&!streetRoot.GetComponent<Demo.Scripts.Runtime.Character.FPSController>())DiscoverLootSubtree(streetRoot.transform);
   }
  }
  var childBudget=64;
  for(var n=0;n<64;n++){
   // A wide street root can have thousands of children. Bound hierarchy edges
   // as well as processed nodes and continue that exact parent next frame.
   if(lootExpanding){
    lootChildIndex=Math.Min(lootChildIndex,lootExpanding!.childCount-1);
    while(lootChildIndex>=0&&childBudget>0){DiscoverLootSubtree(lootExpanding.GetChild(lootChildIndex--));childBudget--;}
    if(lootChildIndex>=0)break;
    lootExpanding=null;
   }
   if(lootBirthNodes.Count==0)break;
   var node=lootBirthNodes.Pop();queuedLootNodes.Remove(node.Pointer);if(!node||node.gameObject.scene.name!=scene)continue;
   lootExpanding=node;lootChildIndex=node.childCount-1;
   RepairWorldBallisticNode(node);
   var item=node.GetComponent<LootableItem>();if(item&&!stableLootKeys.ContainsKey(item.GetInstanceID()))QueueKnownLoot(item,0);
   var box=node.GetComponent<LootableChanceItems>();if(box&&!stableLootKeys.ContainsKey(box.GetInstanceID()))QueueKnownLoot(box,1);
   var heal=node.GetComponent<FastHealItem>();if(heal&&!stableLootKeys.ContainsKey(heal.GetInstanceID()))QueueKnownLoot(heal,2);
   var machine=node.GetComponent<VendingMachine>();if(machine)RegisterVendingMachine(machine);
   // The same bounded census supplies UnitSync's initial/inactive-boss births.
   // LivingEntity.Awake supplies subsequent dynamic activations and spawns.
   var unit=node.GetComponent<Ballistics.LivingEntity>();if(unit)RegisterUnitAwake(unit);
  }
  var count=Math.Min(8,pendingLoot.Count);
  for(var n=0;n<count;n++){
   var pointer=pendingLoot.Dequeue();queuedLoot.Remove(pointer);if(!knownLoot.TryGetValue(pointer,out var birth))continue;var component=birth.Object;
   if(!component){knownLoot.Remove(pointer);continue;}if(!component.gameObject.activeInHierarchy||stableLootKeys.ContainsKey(component.GetInstanceID()))continue;
   // EnemyTick runs first, but a bounded enemy-discovery backlog can still
   // precede the authoritative ID needed for a movable corpse's identity.
   var enemy=component.GetComponentInParent<EnemyAI.EnemyHealth>();if(enemy&&!enemyIds.ContainsKey(enemy.Pointer)){QueueKnownLoot(component,birth.Category);continue;}
   if(birth.Category==0&&PersonalSupplyItem(component.Cast<LootableItem>())||birth.Category==1&&PersonalSupplyBox(component.Cast<LootableChanceItems>()))continue;
   if(!initialLootKeys.ContainsKey(component.GetInstanceID()))CaptureInitialLootPosition(component);
   RegisterLoot(component,birth.Category);
  }
  if(pendingLootClaims.Count>0)foreach(var key in pendingLootClaims.Keys.Take(4).ToArray()){
   var claim=pendingLootClaims[key];if(!lootObjects.ContainsKey(claim.Request.ObjectId)&&Time.unscaledTime<claim.Until)continue;
   // One final bounded local lookup after the eight-second wait, not every
   // frame. The target may now be active or absent from the earlier full buffer.
   pendingLootClaims.Remove(key);
   if(!lootObjects.ContainsKey(claim.Request.ObjectId)&&PacketWorld(claim.Request)&&ResolveNearbyLoot(claim.Request,false)==LootResolution.Rebound)continue;
   HandleLootRequest(claim.Request,false);
  }
 }
 void UpdateSharedLoot(){
  var activeScene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
  if(lootKnownScene!=activeScene){lootKnownScene=activeScene;PruneLootBirths(activeScene);lootDiscoveryWorld="";lootDiscoveryRevision=-1;}
  if(Network==null)return;
  var epoch=world;
  if(lootEpoch!=epoch){lootEpoch=epoch;lootObjects.Clear();stableLootKeys.Clear();reservations.Clear();requestedLoot.Clear();completedGrants.Clear();containerStates.Clear();takenLoot.Reset();PruneLootBirths(activeScene);foreach(var birth in knownLoot.Values.ToArray())QueueKnownLoot(birth.Object,birth.Category);lootDiscoveryWorld="";lootDiscoveryRevision=-1;}
  if(Network.Hosting&&Connected&&Time.unscaledTime>=grantResendAt){grantResendAt=Time.unscaledTime+1;foreach(var grant in reservations.Values.ToArray())if(grant.TargetId!=Network.LocalId)Network.SendTo(grant,grant.TargetId);}
  if(!SharedRun||activeScene!=scene||DungeonSeedPatch.Building||elevator&&elevator.isMoving)return;
  DiscoverWorldBirths();
 }
 [HideFromIl2Cpp]
 string RegisterLoot(Component component,int category){
  var instance=component.GetInstanceID();if(DroppedIdentity(component) is {} dropKey){if(stableLootKeys.TryGetValue(instance,out var previous)&&previous!=dropKey)lootObjects.Remove(previous);stableLootKeys[instance]=dropKey;lootObjects[dropKey]=component;if(takenLoot.IsTaken(dropKey))HideTaken(dropKey);return dropKey;}
  if(stableLootKeys.TryGetValue(instance,out var key)){lootObjects[key]=component;if(takenLoot.IsTaken(key))HideTaken(key);return key;}
  var p=component.transform.position;
  // Initial path and location are captured once; destroying a sibling cannot renumber this identity.
  // Display items can rotate locally every frame; rotation is not part of pickup identity.
  var identity=EnemyLootIdentity(component);
  var raw=category+"|"+(identity??(initialLootKeys.TryGetValue(instance,out var initial)&&initial.Object&&initial.Object.Pointer==component.Pointer?initial.Key:FormattableString.Invariant($"{component.name}|{Quantize(p.x,100)},{Quantize(p.y,100)},{Quantize(p.z,100)}")));
  key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
  stableLootKeys[instance]=key;lootObjects[key]=component;
  if(category==1&&containerStates.TryGetValue(key,out var contents))SetContainer(component.Cast<LootableChanceItems>(),contents);
  if(takenLoot.IsTaken(key))HideTaken(key);return key;
 }
 [HideFromIl2Cpp]
 void CaptureInitialLootPosition(Component component){var p=component.transform.position;initialLootKeys[component.GetInstanceID()]=(component,FormattableString.Invariant($"{component.name}|{Quantize(p.x,100)},{Quantize(p.y,100)},{Quantize(p.z,100)}"));}
 [HideFromIl2Cpp]
 Vector3 InitialLootPosition(Component component){
  if(initialLootKeys.TryGetValue(component.GetInstanceID(),out var initial)&&initial.Object&&initial.Object.Pointer==component.Pointer){
   var split=initial.Key.LastIndexOf('|');var coordinates=split>=0?initial.Key[(split+1)..].Split(','):Array.Empty<string>();
   if(coordinates.Length==3&&int.TryParse(coordinates[0],NumberStyles.Integer,CultureInfo.InvariantCulture,out var x)&&int.TryParse(coordinates[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out var y)&&int.TryParse(coordinates[2],NumberStyles.Integer,CultureInfo.InvariantCulture,out var z))return new Vector3(x/100f,y/100f,z/100f);
  }
  return component.transform.position;
 }
 [HideFromIl2Cpp]
 Packet LootRequest(Component component,int category,string key){
  var packet=Message("lootreq");packet.ObjectId=key;packet.Category=category;packet.PlayerId=Network!.LocalId;
  var item=component.TryCast<LootableItem>();if(item){packet.ItemId=item.ItemId;packet.Count=(int)item.ItemType;if(category==0&&item.ItemType==LootableItem.LootType.Item){var initial=InitialLootPosition(component);packet.Pose=new[]{initial.x,initial.y,initial.z};}}
  return packet;
 }
 [HideFromIl2Cpp]
 public void CaptureInitialLoot(Component component){CaptureInitialLootPosition(component);QueueKnownLoot(component,0);}
 [HideFromIl2Cpp]
 public bool RequestPickup(Component component,int category){
  if(TryFoodPickup(component,category,out var foodNative))return foodNative;
  if(returningAfterDisconnect)return false;
  if(Replay||(!SharedRun&&!(Connected&&(category==2&&stableLootKeys.ContainsKey(component.GetInstanceID())||DroppedIdentity(component)!=null))))return true;
  if(PendingDrop(component)){detail="Waiting for dropped item confirmation";return false;}
  if(Down||PartyWiped)return false;
  if(!LootIdentityReady(component,category)){detail="Waiting for the host's enemy identity. Try again shortly.";return false;}
  if(category==0&&component.TryCast<LootableItem>() is {} supplyItem&&PersonalSupplyItem(supplyItem))return true;
  if(category==1&&component.TryCast<LootableChanceItems>() is { } supply&&PersonalSupplyBox(supply))return true;
  if(elevator&&elevator.isMoving){detail="Pickup blocked during floor transition";return false;}
  if(!Connected||!RulesReady||SharedRun&&!SharedActorWorldReady||Network?.Hosting!=true&&(!remotes.TryGetValue(0,out var host)||!PeerMatches(host))){detail="Pickup blocked: waiting for the host's world";return false;}
  var key=RegisterLoot(component,category);
  if(takenLoot.IsTaken(key)){detail="Your partner already took this";return false;}
  if(!requestedLoot.Add(key))return false;
  var p=LootRequest(component,category,key);
  if(Network.Hosting)HandleLootRequest(p);else Network.Send(p);
  return false;
 }
 [HideFromIl2Cpp]
 bool ReceiveSharedLoot(Packet p){
  if(p.Kind=="run"&&Network?.Hosting==false&&p.Scene==scene){RunPlayers=p.PlayerCount;return true;}
  if(p.Kind=="lootreq"&&Network?.Hosting==true){HandleLootRequest(p);return true;}
  if(p.Kind=="lootack"&&Network?.Hosting==true){HandleLootAck(p);return true;}
  if(p.Kind=="lootgrant"&&Network?.Hosting==false){ExecuteLootGrant(p);return true;}
  if(p.Kind=="lootrebind"&&Network?.Hosting==false){ApplyLootRebind(p);return true;}
  if(p.Kind=="lootreject"){if(PacketWorld(p)){LootDecision(p,"client_reject","host_rejected");requestedLoot.Remove(p.ObjectId);detail="Pickup refused: already claimed, too far away, or loot mismatch";}return true;}
  if(p.Kind=="loottaken"){
   if(PacketWorld(p)){
    if(p.Category==1){containerStates[p.ObjectId]=p.Contents;if(lootObjects.TryGetValue(p.ObjectId,out var box)&&box)SetContainer(box.Cast<LootableChanceItems>(),p.Contents);}
    if(p.Category==0||p.Contents.Length==0){takenLoot.TryClaim(p.ObjectId,p.TargetId);HideTaken(p.ObjectId);}
    requestedLoot.Remove(p.ObjectId);
   }
   return true;
  }
  return false;
 }
 [HideFromIl2Cpp]
 LootResolution ResolveNearbyLoot(Packet request,bool diagnose=true){
  Vector3 origin;
  if(Network==null)return LootResolution.Missing;
  if(request.PlayerId==Network.LocalId){if(!player)return LootResolution.Missing;origin=player.transform.position;}
  else {if(!remotes.TryGetValue(request.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0)return LootResolution.Missing;var pose=peer.State.Pose;origin=new Vector3(pose[0],pose[1],pose[2]);}
  LootableItem? unique=null;string? canonical=null;var matches=0;
  var mayRebind=Network.Hosting&&SharedActorWorldReady&&request.PlayerId!=Network.LocalId&&request.Category==0&&request.Count==0&&LootRebindWire.Key(request.ObjectId)&&LootRebindWire.Position(request.Pose);
  var requestedPosition=mayRebind?new Vector3(request.Pose[0],request.Pose[1],request.Pose[2]):Vector3.zero;
  nearbyLootSeen.Clear();nearbyLootDetails.Clear();
  try{
  var count=Physics.OverlapSphereNonAlloc(origin,8,nearbyLootColliders,~0,QueryTriggerInteraction.Collide);
  for(var i=0;i<count&&i<nearbyLootColliders.Length;i++){
   var collider=nearbyLootColliders[i];if(!collider||collider.gameObject.scene.name!=scene)continue;
   Component? candidate=request.Category switch{0=>collider.GetComponentInParent<LootableItem>(),1=>collider.GetComponentInParent<LootableChanceItems>(),2=>collider.GetComponentInParent<FastHealItem>(),_=>null};
   if(!candidate||!candidate.gameObject.activeInHierarchy||!nearbyLootSeen.Add(candidate.Pointer))continue;
   if(request.Category==0&&PersonalSupplyItem(candidate.Cast<LootableItem>())||request.Category==1&&PersonalSupplyBox(candidate.Cast<LootableChanceItems>()))continue;
   if(!LootIdentityReady(candidate,request.Category))continue;
   if(!initialLootKeys.ContainsKey(candidate.GetInstanceID()))CaptureInitialLootPosition(candidate);
   var key=RegisterLoot(candidate,request.Category);if(key==request.ObjectId)return LootResolution.Exact;
   var initial=InitialLootPosition(candidate);
   if(diagnose&&GameplayLog.Operational)nearbyLootDetails.Add((candidate,key,initial));
   var item=candidate.TryCast<LootableItem>();
   if(mayRebind&&item&&item!.ItemType==LootableItem.LootType.Item&&item.ItemId==request.ItemId&&DroppedIdentity(item)==null&&EnemyLootIdentity(item)==null&&LootRebindWire.Key(key)&&Vector3.Distance(initial,requestedPosition)<=.35f){matches++;unique=item;canonical=key;}
  }
  if(diagnose){var shown=0;for(var pass=0;pass<2&&shown<8;pass++)foreach(var candidate in nearbyLootDetails){var item=candidate.Object.TryCast<LootableItem>();var same=item&&item!.ItemId==request.ItemId&&(int)item.ItemType==request.Count;if(same!=(pass==0))continue;LootDecision(request,"nearby","different_key",candidate.Object,LootDistance(origin,candidate.Object),candidate.Key,candidate.Initial);if(++shown>=8)break;}}
   // Dense gallery rooms can fill the collider buffer with walls and props.
   // In that case prove uniqueness against the completed bounded birth census,
   // rather than treating a truncated physics result as a complete item set.
   var complete=count<nearbyLootColliders.Length;
   if(mayRebind&&!complete){
    complete=RegisteredLootRebind(request,requestedPosition,out unique,out canonical,out matches);
    if(complete&&unique&&canonical==request.ObjectId)return LootResolution.Exact;
   }
   if(mayRebind&&complete&&matches==1&&unique&&canonical!=null&&LootDistance(origin,unique)<7&&!takenLoot.IsTaken(canonical)&&!reservations.ContainsKey(canonical)){
   var attempt=(request.PlayerId,request.ObjectId);
   if(lootRebindOffered.Count<32&&!lootRebindOffered.Contains(attempt)){
    lootRebindOffered.Add(attempt);pendingLootClaims.Remove(attempt);var position=InitialLootPosition(unique);var rebind=Message("lootrebind");rebind.ObjectId=request.ObjectId;rebind.MachineId=canonical;rebind.TargetId=request.PlayerId;rebind.Category=0;rebind.Count=0;rebind.ItemId=request.ItemId;rebind.Pose=new[]{position.x,position.y,position.z};
    Network.SendTo(rebind,request.PlayerId);LootDecision(request,"host_rebind","unique_initial_position",unique,LootDistance(origin,unique),canonical,position);return LootResolution.Rebound;
   }
  }
   if(diagnose)LootDecision(request,"nearby",!complete?"buffer_full_census_incomplete":matches>1?"ambiguous":matches==1?"rebind_target_unavailable_or_attempt_limit":"exact_key_absent",unique,candidateKey:canonical);
  return LootResolution.Missing;
  }finally{nearbyLootSeen.Clear();nearbyLootDetails.Clear();for(var i=0;i<nearbyLootColliders.Length;i++)nearbyLootColliders[i]=null!;}
 }
  [HideFromIl2Cpp]
  bool RegisteredLootRebind(Packet request,Vector3 position,out LootableItem? unique,out string? canonical,out int matches){
   unique=null;canonical=null;matches=0;
   // A partial census cannot rule out a second item at the requested position.
   // The same wait/retry path used for normal discovery will try again later.
   if(!SharedActorWorldReady||lootDiscoveryWorld!=world||lootDiscoveryRevision!=DungeonSeedPatch.Revision||lootExpanding||lootBirthNodes.Count!=0||knownLoot.Count>4096)return false;
   foreach(var birth in knownLoot.Values){
    if(birth.Category!=0||!birth.Object||!birth.Object.gameObject.activeInHierarchy||birth.Object.gameObject.scene.name!=scene)continue;
    var item=birth.Object.TryCast<LootableItem>();
    if(!item||item.ItemType!=LootableItem.LootType.Item||item.ItemId!=request.ItemId||DroppedIdentity(item)!=null||EnemyLootIdentity(item)!=null)continue;
    // Never substitute a current animated/moved pose for a missing birth pose.
    if(!initialLootKeys.TryGetValue(item.GetInstanceID(),out var initial)||!initial.Object||initial.Object.Pointer!=item.Pointer)return false;
    if(Vector3.Distance(InitialLootPosition(item),position)>.35f)continue;
    var key=RegisterLoot(item,0);if(!LootRebindWire.Key(key))return false;
    if(key==request.ObjectId){unique=item;canonical=key;matches=1;return true;}
    matches++;unique=item;canonical=key;if(matches>1)return true;
   }
   return true;
  }
 [HideFromIl2Cpp]
 void ApplyLootRebind(Packet packet){
  if(Network?.Hosting!=false||!PacketWorld(packet)||packet.TargetId!=Network.LocalId||!LootRebindWire.Valid(packet)){LootDecision(packet,"client_rebind_drop","invalid_world_or_wire");return;}
  if(!requestedLoot.Contains(packet.ObjectId)||!lootObjects.TryGetValue(packet.ObjectId,out var component)||!component){LootDecision(packet,"client_rebind_drop","original_not_requested_or_bound");return;}
  var item=component.TryCast<LootableItem>();
  if(!item||item.ItemType!=LootableItem.LootType.Item||item.ItemId!=packet.ItemId||DroppedIdentity(item)!=null||EnemyLootIdentity(item)!=null||Vector3.Distance(InitialLootPosition(item),new Vector3(packet.Pose[0],packet.Pose[1],packet.Pose[2]))>.35f){LootDecision(packet,"client_rebind_drop","item_or_position_mismatch",component);requestedLoot.Remove(packet.ObjectId);return;}
  if(lootRebindAccepted.Count>=32||lootRebindAccepted.Contains(packet.ObjectId)||lootRebindAccepted.Contains(packet.MachineId)||requestedLoot.Contains(packet.MachineId)||takenLoot.IsTaken(packet.MachineId)||lootObjects.TryGetValue(packet.MachineId,out var other)&&other&&other.Pointer!=component.Pointer){LootDecision(packet,"client_rebind_drop","attempt_limit_or_collision",component);requestedLoot.Remove(packet.ObjectId);return;}
  lootRebindAccepted.Add(packet.ObjectId);lootRebindAccepted.Add(packet.MachineId);
  lootObjects.Remove(packet.ObjectId);requestedLoot.Remove(packet.ObjectId);stableLootKeys[component.GetInstanceID()]=packet.MachineId;lootObjects[packet.MachineId]=component;requestedLoot.Add(packet.MachineId);
  LootDecision(packet,"client_rebind","canonical_key",component,candidateKey:packet.MachineId,initial:InitialLootPosition(component));Network.Send(LootRequest(component,0,packet.MachineId));
 }
 [HideFromIl2Cpp]
 void HandleLootRequest(Packet p,bool allowDiscovery=true){
  LootDecision(p,"host_request",allowDiscovery?"received":"discovery_retry");
  // The guest can reach an item while the host's bounded initial/corpse census
  // is still running. Retain that authenticated intent until its object binds;
  // never resolve it with a blocking scene-wide search or lose its first click.
  if(allowDiscovery&&SharedRun&&PacketWorld(p)&&p.Category is >=0 and <=2&&!lootObjects.ContainsKey(p.ObjectId)&&!takenLoot.IsTaken(p.ObjectId)&&Network!=null&&
   (p.PlayerId==Network.LocalId?player&&!Down:remotes.TryGetValue(p.PlayerId,out var claimant)&&PeerMatches(claimant)&&claimant.State!.Health>0&&!downPlayers.Contains(p.PlayerId))){
   var resolved=ResolveNearbyLoot(p);if(resolved==LootResolution.Rebound)return;
   if(resolved==LootResolution.Missing){var key=(p.PlayerId,p.ObjectId);if(pendingLootClaims.Count<32||pendingLootClaims.ContainsKey(key)){
    if(!pendingLootClaims.ContainsKey(key)){pendingLootClaims[key]=(p,Time.unscaledTime+8);var root=SharedBirthRoot();if(root)DiscoverLootSubtree(root);}LootDecision(p,"host_pending","object_key_unbound");return;
   }}
  }
  lootObjects.TryGetValue(p.ObjectId,out var component);var reason="";var distance=-1f;var who=Vector3.zero;
  if(!(SharedRun||(Connected&&(p.Category==2||droppedActors.ContainsKey(p.ObjectId)))))reason="run_not_shared";
  else if(!PacketWorld(p))reason="world_mismatch";
  else if(takenLoot.IsTaken(p.ObjectId))reason="already_taken";
  else if(reservations.ContainsKey(p.ObjectId))reason="reserved";
  else if(!component)reason="object_key_unbound";
  else if(!component!.gameObject.activeInHierarchy||component.gameObject.scene.name!=scene)reason="object_inactive_or_scene";
  else if(!LootIdentityReady(component,p.Category))reason="enemy_identity_unbound";
  else if(p.PlayerId==Network!.LocalId){if(!player)reason="local_player_unavailable";else if(Down)reason="claimant_down";else who=player.transform.position;}
  else if(!remotes.TryGetValue(p.PlayerId,out var remote)||!PeerMatches(remote))reason="peer_world_unsettled";
  else if(remote.State!.Health<=0||downPlayers.Contains(p.PlayerId))reason="claimant_down";
  else {var pose=remote.State!.Pose;who=new Vector3(pose[0],pose[1],pose[2]);}
  if(reason==""){
   distance=LootDistance(who,component!);if(distance>=7)reason="out_of_range";
   else {var item=component!.TryCast<LootableItem>();if(!(p.Category==2?component.TryCast<FastHealItem>()!=null:p.Category==1?component.TryCast<LootableChanceItems>()!=null:p.Category==0&&item&&item.ItemId==p.ItemId&&(int)item.ItemType==p.Count))reason="native_item_or_type_mismatch";}
  }
  if(reason!=""){LootDecision(p,"host_reject",reason,component,distance);var no=Message("lootreject");no.ObjectId=p.ObjectId;if(p.PlayerId==Network!.LocalId){requestedLoot.Remove(p.ObjectId);detail="Pickup rejected: "+reason;}else Network!.SendTo(no,p.PlayerId);return;}
  var grant=Message("lootgrant");grant.ObjectId=p.ObjectId;grant.TargetId=p.PlayerId;grant.Sequence=++claimSequence;grant.Amount=droppedActors.TryGetValue(p.ObjectId,out var dropped)?dropped.Spawn.Drop?.ValueFactor??1:LootFactor;grant.Category=p.Category;
  if(p.Category==0){var item=lootObjects[p.ObjectId].Cast<LootableItem>();grant.ItemId=item.ItemId;grant.Count=(int)item.ItemType;if(item.ItemType==LootableItem.LootType.Wallet)grant.Seed=UnityEngine.Random.Range(1,int.MaxValue);}
  if(p.Category==1){
   var box=lootObjects[p.ObjectId].Cast<LootableChanceItems>();
   if(box.DroppedItems==null){if(!box.loot||box.loot.lootDrop==null){LootDecision(p,"host_reject","container_loot_unavailable",box);var no=Message("lootreject");no.ObjectId=p.ObjectId;if(p.PlayerId==Network!.LocalId){requestedLoot.Remove(p.ObjectId);detail="Pickup rejected: container loot is unavailable";}else Network.SendTo(no,p.PlayerId);return;}box.DroppedItems=box.loot.GetLoot(Math.Min(3,box.loot.lootDrop.Count));GameplayLog.Record("container_roll",new{objectKey=DiagnosticObjectKey(p.ObjectId),peer=p.PlayerId,tableEntries=box.loot.lootDrop.Count,chanceOfNothing=box.loot.chanceOfNothing,resultCount=box.DroppedItems?.Count??0});}
   grant.Contents=ReadContainer(box);containerStates[p.ObjectId]=grant.Contents;
  }
  reservations[p.ObjectId]=grant;
  LootDecision(grant,"host_grant","reserved",component,distance);
  if(p.PlayerId==Network!.LocalId)ExecuteLootGrant(grant);else Network.SendTo(grant,p.PlayerId);
 }
 [HideFromIl2Cpp]
 string LootGrantRefusal(Component component){
  if(Network==null||!Connected)return "network_not_connected";
  if(returningAfterDisconnect||PersonalSavePending)return "personal_transition_pending";
  if(Down||PartyWiped||PlayingNativeDeath)return "claimant_down";
  var health=player?player!.Health:null;
  if(!player||!health||health!.died||health.Health<=0)return "claimant_not_alive";
  if(!RulesReady||SharedRun&&!SharedActorWorldReady||!Network.Hosting&&(!remotes.TryGetValue(0,out var host)||!PeerMatches(host)))return "host_world_unsettled";
  if(elevator&&elevator.isMoving)return "floor_transition";
  if(!component.gameObject.activeInHierarchy||component.gameObject.scene.name!=scene)return "local_object_inactive_or_scene";
  if(LootDistance(player!.transform.position,component)>=7)return "out_of_range";
  return "";
 }
 [HideFromIl2Cpp]
 void ExecuteLootGrant(Packet p){
  if(Network==null||!PacketWorld(p)||p.TargetId!=Network.LocalId){LootDecision(p,"client_grant_drop","world_or_target_mismatch");return;}
  var token=p.ObjectId+":"+p.Sequence;
  if(completedGrants.TryGetValue(token,out var receipt)){LootDecision(p,"client_grant","duplicate_ack_replay");if(Network.Hosting)HandleLootAck(receipt);else Network.Send(receipt);return;}
  if(!requestedLoot.Contains(p.ObjectId)){LootDecision(p,"client_grant_drop","not_requested");return;}
  if(!lootObjects.TryGetValue(p.ObjectId,out var component)||!component){LootDecision(p,"client_grant_drop","local_object_unbound");return;}
  // A request may have been made before death, travel or movement. Completed
  // receipts above still replay unchanged; a declined fresh token stays declined.
  var refusal=LootGrantRefusal(component);
  if(refusal.Length>0){
   var denied=Message("lootack");denied.ObjectId=p.ObjectId;denied.Sequence=p.Sequence;denied.PlayerId=Network.LocalId;denied.Contents=p.Contents;denied.Success=false;
   completedGrants[token]=denied;requestedLoot.Remove(p.ObjectId);LootDecision(p,"client_grant_reject",refusal,component);
   if(Network.Hosting)HandleLootAck(denied);else Network.Send(denied);return;
  }
  LootDecision(p,"client_grant","execute_native",component);
  var ack=Message("lootack");ack.ObjectId=p.ObjectId;ack.Sequence=p.Sequence;ack.PlayerId=Network.LocalId;ack.Contents=p.Contents;
  // Retain the identity before native inventory/health code. A retry can only
  // replay this receipt, including a conservatively quarantined native fault.
  completedGrants[token]=ack;
  var inv=Object.FindObjectOfType<Inventory>();var before=InventorySummary(inv);var lootBefore=LootEconomy.Snapshot(inv);
  var dollarsBefore=inv?inv.DollarsCount:0;var roublesBefore=inv?inv.RoublesCount:0;
  var success=false;var remaining=p.Contents;var nativeFault=false;var healEntered=false;var containerEntered=false;LootableChanceItems? container=null;
  var replay=Replay;Replay=true;
  try{
   if(p.Category==0){
    var item=component.Cast<LootableItem>();var input=Object.FindObjectOfType<UserInteractions>();var wallet=item.ItemType==LootableItem.LootType.Wallet;var random=UnityEngine.Random.state;
    try{if(wallet){if(p.Seed<=0)throw new IOException("Missing authoritative wallet seed");UnityEngine.Random.InitState(p.Seed);}if(input)item.Interact(input);else item.Interact();success=wallet||!item||!item.gameObject.activeSelf||InventorySummary(inv)!=before;}
    finally{if(wallet)UnityEngine.Random.state=random;}
   }
   else if(p.Category==2){
    var heal=component.Cast<FastHealItem>();healEntered=true;success=true;ack.Success=true;
    // Native FastHeal writes HP before audio lookup and object hiding. Once
    // entered, even an exception before the HP write consumes the uncertain can.
    heal.Interact();
   }
   else {container=component.Cast<LootableChanceItems>();SetContainer(container,p.Contents);containerEntered=true;container.Interact();}
  }catch(Exception e){nativeFault=true;detail="Pickup failed: "+e.GetType().Name;try{Plugin.Instance.Log.LogError(e);}catch{}}
  finally{Replay=replay;}
  if(containerEntered){
   var quarantine=nativeFault;var reason=nativeFault?"native_container_exception":"";
   try{remaining=ReadContainer(container!);if(!LootContents.IsRemainder(p.Contents,remaining)){quarantine=true;reason="invalid_native_remainder";}}
   catch(Exception error){quarantine=true;reason="native_remainder_unreadable";try{Plugin.Instance.Log.LogError(error);}catch{}}
   // Native Interact removes awarded entries only after its whole award loop.
   // On a fault, even a valid-looking full/subset remainder can contain an
   // already-awarded item. Retire that uncertain remainder instead of reoffering.
   if(quarantine){remaining=Array.Empty<LootEntry>();success=true;ack.Success=true;ack.Contents=remaining;try{HideTaken(p.ObjectId);}catch(Exception error){try{Plugin.Instance.Log.LogError(error);}catch{}}GameplayLog.Record("loot_quarantined",new{objectKey=DiagnosticObjectKey(p.ObjectId),category=p.Category,sequence=p.Sequence,reason});detail="Container pickup entered native code; uncertain remaining contents were quarantined.";}
   else success=remaining.Length<p.Contents.Length||p.Contents.Length==0;
  }
  if(healEntered){try{HideTaken(p.ObjectId);}catch(Exception error){try{Plugin.Instance.Log.LogError(error);}catch{}}if(nativeFault){GameplayLog.Record("loot_quarantined",new{objectKey=DiagnosticObjectKey(p.ObjectId),category=p.Category,sequence=p.Sequence,reason="native_heal_exception"});detail="Healing pickup entered native code; its receipt is retained and the object was quarantined.";}}
  // A native exception after adding some items must not release the object for a second pickup.
  success|=InventorySummary(inv)!=before;
  ack.Success=success;ack.Contents=remaining;
  if(success){
   var factor=Math.Clamp((int)p.Amount,1,6);
   LootEconomy.RecordAcquired(inv,lootBefore,factor);
   if(inv&&factor>1){var dollars=Math.Max(0,inv.DollarsCount-dollarsBefore);var roubles=Math.Max(0,inv.RoublesCount-roublesBefore);if(dollars>0)inv.AddDollars(dollars*(factor-1));if(roubles>0)inv.AddRoubles(roubles*(factor-1));}
  }
  GameplayLog.Record("loot_native_result",new{objectKey=DiagnosticObjectKey(p.ObjectId),category=p.Category,sequence=p.Sequence,granted=p.Contents.Length,remaining=remaining.Length,success,inventoryChanged=InventorySummary(inv)!=before});
  if(Network.Hosting)HandleLootAck(ack);else Network.Send(ack);
  requestedLoot.Remove(p.ObjectId);
 }
 [HideFromIl2Cpp]
 void HandleLootAck(Packet p){
  if(!PacketWorld(p)){LootDecision(p,"host_ack_drop","world_mismatch");return;}
  if(!reservations.TryGetValue(p.ObjectId,out var grant)){LootDecision(p,"host_ack_drop","reservation_missing");return;}
  if(grant.TargetId!=p.PlayerId||grant.Sequence!=p.Sequence){LootDecision(p,"host_ack_drop","owner_or_sequence_mismatch");return;}
  if(grant.Category==1&&!LootContents.IsRemainder(grant.Contents,p.Contents)){LootDecision(p,"host_ack_drop","invalid_container_remainder");return;}
  LootDecision(p,"host_ack",p.Success?"native_success":"native_failed");
  reservations.Remove(p.ObjectId);
  if(!p.Success)return;
  if(grant.Category==1){containerStates[p.ObjectId]=p.Contents;if(lootObjects.TryGetValue(p.ObjectId,out var component)&&component)SetContainer(component.Cast<LootableChanceItems>(),p.Contents);}
  if(grant.Category!=1||p.Contents.Length==0){takenLoot.TryClaim(p.ObjectId,p.PlayerId);HideTaken(p.ObjectId);}
  var taken=Message("loottaken");taken.ObjectId=p.ObjectId;taken.TargetId=p.PlayerId;taken.Category=grant.Category;taken.Contents=p.Contents;Network!.Send(taken);
 }
 [HideFromIl2Cpp]
 static LootEntry[] ReadContainer(LootableChanceItems box){var entries=new List<LootEntry>();if(box.DroppedItems!=null)foreach(var e in box.DroppedItems)entries.Add(new LootEntry{Type=(int)e.dropType,Index=e.ItemIndex});return entries.ToArray();}
 [HideFromIl2Cpp]
 bool PersonalSupplyBox(LootableChanceItems box){
  if(EnemyLootIdentity(box)!=null)return false;
  if(!box.loot||box.loot.lootDrop==null||box.loot.lootDrop.Count==0)return false;
  foreach(var drop in box.loot.lootDrop)if(drop.dropType is not (LootDrop.DropType.Weapon or LootDrop.DropType.Ammo or LootDrop.DropType.Mag))return false;
  return true;
 }
 [HideFromIl2Cpp]
 bool PersonalSupplyItem(LootableItem item){
  if(DroppedIdentity(item)!=null)return false;
  if(item.ItemType is not (LootableItem.LootType.Weapon or LootableItem.LootType.Ammo or LootableItem.LootType.Mag)||EnemyLootIdentity(item)!=null)return false;
  // Some supplies are a single LootableItem attached to a crate mesh rather
  // than a LootableChanceItems container. Keep loose dropped guns shared.
  for(var parent=item.transform;parent;parent=parent.parent){var name=parent.name.ToLowerInvariant();if(name.Contains("crate")||name.Contains("box")||name.Contains("case")||name.Contains("ящик"))return true;}
  return false;
 }
 [HideFromIl2Cpp]
 static void SetContainer(LootableChanceItems box,LootEntry[] entries){var list=new Il2CppSystem.Collections.Generic.List<LootDrop>();foreach(var e in entries)list.Add(new LootDrop{dropType=(LootDrop.DropType)e.Type,ItemIndex=e.Index});box.DroppedItems=list;}
 void HideTaken(string key){
  if(!lootObjects.TryGetValue(key,out var component)||!component)return;
  var item=component.TryCast<LootableItem>();if(item)item.gameObject.SetActive(false);
  else{var box=component.TryCast<LootableChanceItems>();if(box){box.DroppedItems=new Il2CppSystem.Collections.Generic.List<LootDrop>();box.enabled=false;}else if(component.TryCast<FastHealItem>()!=null)component.gameObject.SetActive(false);}
 }
 // A null native list has not rolled yet; only a non-null empty list is exhausted.
 // UI checks use existing identity bindings, never RegisterLoot or a scene scan.
 [HideFromIl2Cpp]
 bool SharedContainerExhausted(LootableChanceItems box,bool canonicalCorpse=false){
  if(!SharedRun||!box)return false;
  var key="";var bound=lootEpoch==world&&stableLootKeys.TryGetValue(box.GetInstanceID(),out key)&&lootObjects.TryGetValue(key,out var component)&&component&&component.Pointer==box.Pointer;
  if(bound&&(takenLoot.IsTaken(key)||containerStates.TryGetValue(key,out var remaining)&&remaining.Length==0))return true;
  var items=box.DroppedItems;if(items==null||items.Count!=0)return false;
  if(bound||canonicalCorpse)return true;
  // Unbound personal supply boxes remain native. Only a canonical dead actor
  // can use the known-empty fallback before shared discovery has bound its key.
  var enemy=EnemyForLoot(box);return enemy!=null&&enemy.Health&&enemy.Health.dead;
 }
 [HideFromIl2Cpp]
 internal bool AllowSharedContainerPrompt(UserInteractions input,Interactable interactable,bool clear){
  if(!SharedRun||!input||interactable==null)return true;
  var box=interactable.TryCast<LootableChanceItems>();if(!box||!SharedContainerExhausted(box))return true;
  box.enabled=false;
  // The current native call supplies this controller's exact prompt target.
  // Never reset another controller/prompt while processing a network receipt.
  if(clear)input.HandleNoInteraction();return false;
 }
 [HideFromIl2Cpp]
 static string InventorySummary(Inventory inv){
  if(!inv)return "";
  var sum=0;foreach(var kv in inv.LootItems)sum+=kv.Value;foreach(var kv in inv.AmmoStored)sum+=kv.Value;
  foreach(var kv in inv.HealthItems)sum+=kv.Value;foreach(var kv in inv.GrenadeItems)sum+=kv.Value;
  return $"{inv.DollarsCount}|{inv.RoublesCount}|{sum}|{inv.ArmorItems.Count}|{inv.MagStored.Count}|{(inv.FPSController?inv.FPSController.InstantiatedWeapons.Count:0)}|{inv.hasNVG}";
 }
}

[HarmonyPatch(typeof(LootableItem))]
static class SharedItemPickupPatch
{
 static IEnumerable<System.Reflection.MethodBase> TargetMethods()=>typeof(LootableItem).GetMethods().Where(m=>m.Name=="Interact");
 static bool Prefix(LootableItem __instance)=>Together.Current?.RequestPickup(__instance,0)??true;
}
[HarmonyPatch(typeof(LootableChanceItems),nameof(LootableChanceItems.Interact))]
static class SharedContainerPickupPatch {static bool Prefix(LootableChanceItems __instance)=>Together.Current?.RequestPickup(__instance,1)??true;}
[HarmonyPatch(typeof(UserInteractions),"UpdateTooltip")]
static class SharedContainerTooltipPatch {static bool Prefix(UserInteractions __instance,Interactable __0)=>Together.Current?.AllowSharedContainerPrompt(__instance,__0,false)??true;}
[HarmonyPatch(typeof(UserInteractions),"HandleHoldInteraction")]
static class SharedContainerHoldPromptPatch {static bool Prefix(UserInteractions __instance,Interactable __0)=>Together.Current?.AllowSharedContainerPrompt(__instance,__0,false)??true;}
[HarmonyPatch(typeof(UserInteractions),"UpdateInteractionUI")]
static class SharedContainerInteractionUiPatch {static bool Prefix(UserInteractions __instance,Interactable __0)=>Together.Current?.AllowSharedContainerPrompt(__instance,__0,true)??true;}
[HarmonyPatch(typeof(LootableItem),"Start")]
static class InitialLootIdentity {
 static bool Prefix(LootableItem __instance){
  // The shipped menu contains three decorative gun pickups but no Inventory.
  // Their native Start dereferences that missing dependency; they are never usable here.
  if(__instance.gameObject.scene.name=="MainMenu"&&!Object.FindObjectOfType<Inventory>())return false;
  Together.Current?.CaptureInitialLoot(__instance);return true;
 }
}
[HarmonyPatch(typeof(FastHealItem),nameof(FastHealItem.Interact))]
static class SharedFastHeal {static bool Prefix(FastHealItem __instance)=>Together.Current?.RequestPickup(__instance,2)??true;}
