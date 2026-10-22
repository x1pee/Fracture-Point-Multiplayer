using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Reflection;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 sealed class EnemyRagdoll
 {
  public Transform[] Bones=Array.Empty<Transform>();public Rigidbody?[] Bodies=Array.Empty<Rigidbody?>();public Vector3[] Positions=Array.Empty<Vector3>();public Quaternion[] Rotations=Array.Empty<Quaternion>();public long[] Sequences=Array.Empty<long>();
  public Vector3[] SentPositions=Array.Empty<Vector3>();public Quaternion[] SentRotations=Array.Empty<Quaternion>();
  public Vector3[] StagedPositions=Array.Empty<Vector3>();public Quaternion[] StagedRotations=Array.Empty<Quaternion>();public long[] StagedBones=Array.Empty<long>();
  public long CaptureSequence,StagedSequence,CommittedSequence;public int StagedCount;
  public float DeathAt=-1,SendAt,RefreshAt,RigRetryAt;public bool Ready,Prepared,Sending,Applying;public int SendOffset;public LootableChanceItems? Loot;
 }
 readonly Dictionary<IntPtr,int> corpseColliders=new();
 sealed class PendingEnemyBody {public Packet Packet=null!;public float Expires;}
 readonly Dictionary<(int Enemy,int Offset),PendingEnemyBody> pendingEnemyBodies=new();readonly Queue<(int Enemy,int Offset)> pendingEnemyBodyQueue=new();
 [HideFromIl2Cpp]
 static EnemyRagdoll EnemyBody(SyncedEnemy enemy){
  var rig=enemy.Body??=new EnemyRagdoll();if(rig.Ready||Time.unscaledTime<rig.RigRetryAt)return rig;rig.RigRetryAt=Time.unscaledTime+.25f;
  var bones=new List<Transform>();var puppet=enemy.Health?enemy.Health.puppet:null;
  if(puppet){
   if(GuestEnemyAuthority&&!puppet!.initiated)puppet.Initiate();
   var muscles=puppet!.muscles;if(!puppet.initiated||muscles==null||muscles.Length is <1 or >64)return rig;
   for(var i=0;i<muscles.Length;i++){
    var muscle=muscles[i];if(muscle==null||!muscle.target)return rig;var bone=muscle.joint?muscle.joint.transform:muscle.transform;
    // Wire indices are authored muscle indices. Never compact a temporarily
    // missing bone and map a later limb into that missing bone's slot.
    if(!bone)return rig;bones.Add(bone);
   }
  }
  // Bosses without PuppetMaster still expose native physical bodies.
  else if(enemy.Root)foreach(var body in enemy.Root.GetComponentsInChildren<Rigidbody>(true))if(bones.Count<64)bones.Add(body.transform);
  if(bones.Count==0)return rig;
  rig.Bones=bones.ToArray();rig.Bodies=bones.Select(t=>(Rigidbody?)t.GetComponent<Rigidbody>()).ToArray();rig.Positions=bones.Select(t=>t.position).ToArray();rig.Rotations=bones.Select(t=>t.rotation).ToArray();rig.SentPositions=new Vector3[bones.Count];rig.SentRotations=new Quaternion[bones.Count];rig.Sequences=new long[bones.Count];
  rig.StagedPositions=new Vector3[bones.Count];rig.StagedRotations=new Quaternion[bones.Count];rig.StagedBones=new long[bones.Count];rig.Ready=true;return rig;
 }
 [HideFromIl2Cpp]
 void PrepareEnemyCorpse(SyncedEnemy enemy){
  var rig=EnemyBody(enemy);if(rig.Prepared||!rig.Ready||!enemy.Root)return;rig.Prepared=true;rig.DeathAt=Time.unscaledTime;
  var physical=new HashSet<IntPtr>(rig.Bones.Select(t=>t.Pointer));
  foreach(var collider in enemy.Root.GetComponentsInChildren<Collider>(true))if(collider.attachedRigidbody&&physical.Contains(collider.attachedRigidbody.transform.Pointer))corpseColliders[collider.Pointer]=enemy.Id;
  // Native live hitboxes belong to the animated target. A corpse must use its
  // physical muscle colliders; leaving both produces an invisible upright body.
  foreach(var hit in enemy.Root.GetComponentsInChildren<Ballistics.LivingEntityCollider>(true))foreach(var c in hit.GetComponents<Collider>()){
   // A muscle can own colliders on child transforms. Preserve the physical
   // owner's floor collisions even when the LEC is not on the joint itself.
   var owner=c.attachedRigidbody;if(!owner||!physical.Contains(owner.transform.Pointer))c.enabled=false;
  }
  if(GuestEnemyAuthority){if(enemy.Health&&enemy.Health.puppet)enemy.Health.puppet.enabled=false;foreach(var animator in enemy.Root.GetComponentsInChildren<Animator>(true))animator.enabled=false;foreach(var body in rig.Bodies)if(body){if(!body.isKinematic){body.velocity=Vector3.zero;body.angularVelocity=Vector3.zero;body.isKinematic=true;}body.interpolation=RigidbodyInterpolation.None;}}
 }
 void EnemyBodyTick(){
  if(!SharedRun||Network?.Hosting!=true||!Connected)return;
  var now=Time.unscaledTime;enemyBodyTokens=Math.Min(12,enemyBodyTokens+Math.Max(0,now-enemyBodyTokenAt)*80);enemyBodyTokenAt=now;if(enemyBodyTokens<1)return;
  var enemies=syncedEnemies.Values.ToArray();if(enemies.Length==0)return;
  if(enemyBodyPeerCount!=Network.PlayerCount){enemyBodyPeerCount=Network.PlayerCount;foreach(var enemy in enemies)if(enemy.Body is {} body){body.RefreshAt=0;body.SendAt=0;}}
  // One chunk per actor per pass, retaining the cursor across frames. A busy
  // low-ID corpse cannot starve another actor or flood transport's 800/s limit.
  var sent=0;for(var visited=0;visited<enemies.Length&&enemyBodyTokens>=1&&sent<8;visited++){
   if(enemyBodyCursor>=enemies.Length)enemyBodyCursor=0;var enemy=enemies[enemyBodyCursor++];if(!enemy.Health||!enemy.Health.dead)continue;PrepareEnemyCorpse(enemy);var rig=EnemyBody(enemy);if(!rig.Prepared||rig.Bones.Length==0||now<rig.SendAt)continue;
   var valid=true;for(var i=0;i<rig.Bones.Length;i++)if(!rig.Bones[i]){valid=false;break;}if(!valid)continue;
   if(!rig.Sending){var changed=now>=rig.RefreshAt;for(var i=0;!changed&&i<rig.Bones.Length;i++)if((rig.Bones[i].position-rig.SentPositions[i]).sqrMagnitude>.000025f||Quaternion.Angle(rig.Bones[i].rotation,rig.SentRotations[i])>.5f)changed=true;if(!changed){rig.SendAt=now+.5f;continue;}
    // Capture one coherent host pose before chunking it across later frames.
    // Otherwise moving hips and limbs can describe different simulation times.
    for(var i=0;i<rig.Bones.Length;i++){rig.SentPositions[i]=rig.Bones[i].position;rig.SentRotations[i]=rig.Bones[i].rotation;}rig.CaptureSequence=++enemyBodySequence;rig.Sending=true;rig.SendOffset=0;
   }
   var offset=rig.SendOffset;var count=Math.Min(8,rig.Bones.Length-offset);var pose=new float[count*7];for(var i=0;i<count;i++){var p=rig.SentPositions[offset+i];var q=rig.SentRotations[offset+i];var n=i*7;pose[n]=p.x;pose[n+1]=p.y;pose[n+2]=p.z;pose[n+3]=q.x;pose[n+4]=q.y;pose[n+5]=q.z;pose[n+6]=q.w;}var packet=Message("enemybody");packet.Sequence=rig.CaptureSequence;packet.Body=new EnemyBodyFrame{EnemyId=enemy.Id,Offset=offset,Pose=pose};Network.Send(packet);enemyBodyTokens--;sent++;rig.SendOffset+=count;
   if(rig.SendOffset>=rig.Bones.Length){rig.Sending=false;rig.RefreshAt=now+6;rig.SendAt=now+(now-rig.DeathAt<8?.125f:.5f);}
  }
 }
 long enemyBodySequence;float enemyBodyTokens=12,enemyBodyTokenAt;int enemyBodyCursor,enemyBodyPeerCount;
 [HideFromIl2Cpp]
 bool ReceiveEnemyBody(Packet packet){
  if(packet.Kind!="enemybody")return false;if(!SharedRun||Network?.Hosting!=false||!PacketWorld(packet)||packet.Body is not {} frame||!frame.Valid())return true;
  if(enemyEpoch!=world){ClearEnemies();enemyEpoch=world;}
  var key=(frame.EnemyId,frame.Offset);if(TryApplyEnemyBody(packet)){pendingEnemyBodies.Remove(key);return true;}
  if(pendingEnemyBodies.TryGetValue(key,out var pending)){if(packet.Sequence>pending.Packet.Sequence){pending.Packet=packet;pending.Expires=Time.unscaledTime+8;}return true;}
  if(pendingEnemyBodies.Count<1024){pendingEnemyBodies[key]=new PendingEnemyBody{Packet=packet,Expires=Time.unscaledTime+8};pendingEnemyBodyQueue.Enqueue(key);}return true;
 }
 void PendingEnemyBodyTick(){
  var count=Math.Min(64,pendingEnemyBodyQueue.Count);for(var i=0;i<count;i++){var key=pendingEnemyBodyQueue.Dequeue();if(!pendingEnemyBodies.TryGetValue(key,out var pending))continue;var packet=pending.Packet;if(!SharedRun||Network?.Hosting!=false||!PacketWorld(packet)||Time.unscaledTime>=pending.Expires||TryApplyEnemyBody(packet))pendingEnemyBodies.Remove(key);else pendingEnemyBodyQueue.Enqueue(key);}
 }
 [HideFromIl2Cpp]
 bool TryApplyEnemyBody(Packet packet){
  var frame=packet.Body!;if(!syncedEnemies.TryGetValue(frame.EnemyId,out var enemy)||enemy.Frame?.Dead!=true)return false;PrepareEnemyCorpse(enemy);var rig=EnemyBody(enemy);if(!rig.Prepared)return false;
  var count=frame.Pose.Length/7;if(frame.Offset+count>rig.Bones.Length||packet.Sequence<=rig.CommittedSequence||packet.Sequence<rig.StagedSequence)return true;
  if(packet.Sequence>rig.StagedSequence){rig.StagedSequence=packet.Sequence;rig.StagedCount=0;}
  for(var i=0;i<count;i++){
   var index=frame.Offset+i;if(rig.StagedBones[index]==packet.Sequence)continue;rig.StagedBones[index]=packet.Sequence;rig.StagedCount++;var n=i*7;
   rig.StagedPositions[index]=new Vector3(frame.Pose[n],frame.Pose[n+1],frame.Pose[n+2]);rig.StagedRotations[index]=new Quaternion(frame.Pose[n+3],frame.Pose[n+4],frame.Pose[n+5],frame.Pose[n+6]);
  }
  // Keep displaying the last complete capture while a new one is in flight.
  // A partial packet must never move the hips against limbs from another pose.
  if(rig.StagedCount==rig.Bones.Length){Array.Copy(rig.StagedPositions,rig.Positions,rig.Bones.Length);Array.Copy(rig.StagedRotations,rig.Rotations,rig.Bones.Length);for(var i=0;i<rig.Sequences.Length;i++)rig.Sequences[i]=packet.Sequence;rig.CommittedSequence=packet.Sequence;rig.Applying=true;}
  return true;
 }
 internal void EnemyLateTick(){
  if(!SharedRun||Network?.Hosting!=false)return;var blend=1-Mathf.Exp(-22*Time.unscaledDeltaTime);
  foreach(var enemy in syncedEnemies.Values){if(enemy.Frame?.Dead!=true||enemy.Body is not {Applying:true} rig)continue;
   var valid=rig.CommittedSequence>0;for(var i=0;valid&&i<rig.Bones.Length;i++)if(!rig.Bones[i]||rig.Sequences[i]!=rig.CommittedSequence)valid=false;
   var puppet=enemy.Health?enemy.Health.puppet:null;var muscles=puppet?puppet!.muscles:null;
   if(puppet&&(!puppet!.initiated||muscles==null||muscles.Length!=rig.Bones.Length))valid=false;
   for(var i=0;valid&&muscles!=null&&i<muscles.Length;i++){var muscle=muscles[i];if(muscle==null||!muscle.target){valid=false;break;}var bone=muscle.joint?muscle.joint.transform:muscle.transform;if(!bone||bone.Pointer!=rig.Bones[i].Pointer)valid=false;}
   if(!valid)continue;var settled=true;for(var i=0;i<rig.Bones.Length;i++){
   var bone=rig.Bones[i];var body=rig.Bodies[i];var currentPosition=body?body.position:bone.position;var currentRotation=body?body.rotation:bone.rotation;
   var distance=(currentPosition-rig.Positions[i]).sqrMagnitude;var angle=Quaternion.Angle(currentRotation,rig.Rotations[i]);if(distance>.000001f||angle>.05f)settled=false;
   var position=distance>9?rig.Positions[i]:Vector3.Lerp(currentPosition,rig.Positions[i],blend);var rotation=Quaternion.Slerp(currentRotation,rig.Rotations[i],blend);
   // The guest owns a kinematic replica. Update its physics pose and render
   // transform together; native interpolation must not restore an older pose.
   if(body){body.position=position;body.rotation=rotation;}bone.SetPositionAndRotation(position,rotation);
  }if(muscles!=null)foreach(var muscle in muscles)muscle.Map(1);if(settled)rig.Applying=false;}
 }
 [HideFromIl2Cpp]
 internal Vector3? EnemyLootAnchor(Component component){var enemy=EnemyForLoot(component);if(enemy==null||!enemy.Health||!enemy.Health.dead)return null;var rig=EnemyBody(enemy);return rig.Bones.Length>0&&rig.Bones[0]?rig.Bones[0].position:enemy.Health.transform.position;}
 [HideFromIl2Cpp]
 SyncedEnemy? EnemyForLoot(Component component){foreach(var enemy in syncedEnemies.Values)if(enemy.Root&&component.transform.IsChildOf(enemy.Root.transform))return enemy;return null;}
 [HideFromIl2Cpp]
 internal LootableChanceItems? EnemyCorpseLoot(Collider collider){
  if(!SharedRun||!collider)return null;
  if(!corpseColliders.TryGetValue(collider.Pointer,out var id)||!syncedEnemies.TryGetValue(id,out var enemy)||!enemy.Health||!enemy.Health.dead||!enemy.Root)return null;
  var rig=EnemyBody(enemy);if(!rig.Loot){var boxes=enemy.Root.GetComponentsInChildren<LootableChanceItems>(true);rig.Loot=boxes.FirstOrDefault(c=>c&&c.gameObject.activeInHierarchy&&c.enabled)??boxes.FirstOrDefault(c=>c&&c.gameObject.activeInHierarchy);}
  // Retain a disabled canonical box so this corpse cannot fall back to a stale
  // native SEARCH prompt after its shared contents have been retired.
  return rig.Loot&&rig.Loot.gameObject.activeInHierarchy?rig.Loot:null;
 }
 [HideFromIl2Cpp]
 internal bool CorpseInteraction(UserInteractions input){
  if(!SharedRun||Down||PartyWiped||!Physics.Raycast(input._ray,out var hit,input._maxDistanceRay))return true;
  var loot=EnemyCorpseLoot(hit.collider);if(!loot)return true;
  if(SharedContainerExhausted(loot,true)){loot.enabled=false;input.HandleNoInteraction();return false;}
  if(!loot.enabled)return true;
  // Run the native interaction UI/hold/input pipeline with the original canonical
  // container. Its existing multiplayer claim hook keeps a single loot ledger.
  input._hit=hit;var interactable=loot.Cast<Interactable>();input.UpdateTooltip(interactable);if(loot.RequiresHold())input.HandleHoldInteraction(interactable);else input.HandleInstantInteraction(interactable);input.UpdateInteractionUI(interactable);return false;
 }
}
[HarmonyPatch(typeof(UserInteractions),"Interact")]
static class RagdollLootInteraction {static bool Prefix(UserInteractions __instance)=>Together.Current?.CorpseInteraction(__instance)??true;}
[HarmonyPatch(typeof(EnemyHealth),nameof(EnemyHealth.Kill))]
static class CoopCorpseReady {static void Postfix(EnemyHealth __instance)=>Together.Current?.EnemyDied(__instance);}
[HarmonyPatch]
static class HostAdaptiveEnemyDeath
{
 // Kill inlines the async state-machine startup, so patching DieAdaptive's
 // wrapper alone cannot prevent its delayed guest physics/quest side effects.
 static IEnumerable<MethodBase> TargetMethods()=>typeof(EnemyHealth).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic).Where(t=>t.Name.Contains("DieAdaptive")||t.Name.Contains("DisablePhysics")).Select(t=>t.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)).OfType<MethodBase>();
 static bool Prefix()=>!Together.GuestEnemyAuthority;
}
