using EnemyAI;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.AI;
using Object=UnityEngine.Object;
namespace FractureTogether;

// EnemySpawner.Spawn (945CB0) uses Instantiate(prefab, spawner.transform).
// Preserve that native selection, damage setup and patrol initialization. Only
// added co-op actors wait inactive for their own clear, reachable placement.
static class PartySpawnPlacement
{
 internal sealed class Batch {internal int BeforeChildren,Basis,Requested;internal Transform? Player;}
 sealed class Pending {
  internal GameObject Root=null!;internal EnemySpawner Spawner=null!;internal Transform? Player;internal Vector3 Origin;internal int Ordinal,Attempts,AreaMask;internal float Born,RetryAt,Radius,MinimumPlayerDistance;internal long BatchId;
 }
 const int QueueLimit=256,ActorsPerFrame=4,CandidatesPerAttempt=12,AllAreas=-1;
 static readonly Queue<Pending> pending=new();
 static readonly Il2CppReferenceArray<Collider> overlap=new(8);
 static readonly List<Vector3> framePositions=new(68);
 static readonly GameObject?[] recentActors=new GameObject?[64];static int recentCursor;
 static NavMeshPath? path;static long batchSequence,nativeRequested,scaledRequested,nativeCreated,extrasQueued,extrasPlaced,extrasDiscarded;static int spawnLogs;
 internal static Batch Before(EnemySpawner spawner,int basis,int requested,Transform? player)=>new(){BeforeChildren=spawner.transform.childCount,Basis=basis,Requested=requested,Player=player};
 internal static void After(EnemySpawner spawner,Batch batch,Exception? error){
  if(!spawner)return;var owner=spawner.transform;var actual=0;var queued=0;var discarded=0;var id=++batchSequence;
  // Native Spawn only appends direct child instances. Start is deferred until
  // after this synchronous native call, before EnemySync assigns host IDs.
  for(var i=batch.BeforeChildren;i<owner.childCount;i++){
   var child=owner.GetChild(i);if(!child||!child.GetComponentInChildren<EnemyHealth>(true)||!child.GetComponentInChildren<StateController>(true))continue;
   actual++;if(actual<=batch.Basis||error!=null)continue;
   var actor=child.gameObject;actor.SetActive(false);
   if(pending.Count>=QueueLimit){Object.Destroy(actor);extrasDiscarded++;discarded++;continue;}
   var agent=child.GetComponentInChildren<NavMeshAgent>(true);var radius=agent?Mathf.Clamp(agent.radius,.25f,.55f):.4f;
   var minimum=!spawner.SpawnOnStart&&batch.Player?Math.Min(25,Vector3.Distance(owner.position,batch.Player!.position)):0;
   pending.Enqueue(new Pending{Root=actor,Spawner=spawner,Player=batch.Player,MinimumPlayerDistance=minimum,AreaMask=agent?agent.areaMask:AllAreas,Origin=child.position,Ordinal=actual-batch.Basis,Born=Time.unscaledTime,RetryAt=Time.unscaledTime+.1f,Radius=radius,BatchId=id});extrasQueued++;queued++;
  }
  nativeRequested+=batch.Basis;scaledRequested+=batch.Requested;nativeCreated+=actual;
  if(GameplayLog.Operational)GameplayLog.Record("enemy_batch_actual",new{batch=id,basis=batch.Basis,requested=batch.Requested,created=actual,activeNative=Math.Min(actual,batch.Basis),queuedExtra=queued,discardedExtra=discarded,perRoom=spawner.SpawnOnStart,exceptionType=error?.GetType().Name});
 }
 internal static void Reset(){while(pending.Count>0){var item=pending.Dequeue();if(item.Root)Object.Destroy(item.Root);extrasDiscarded++;}framePositions.Clear();Array.Clear(recentActors);recentCursor=0;spawnLogs=0;}
 internal static void Tick(){
  if(pending.Count==0)return;var current=Together.Current;
  if(current?.Connected!=true||current.Network?.Hosting!=true||!current.SharedRun){Reset();return;}
  if(!current.SharedActorWorldReady||PartyScaling.Building)return;
  framePositions.Clear();foreach(var actor in recentActors)if(actor&&actor!.activeInHierarchy&&actor.scene.name==current.scene)framePositions.Add(actor.transform.position);
  var now=Time.unscaledTime;var count=Math.Min(ActorsPerFrame,pending.Count);
  for(var n=0;n<count;n++){
   var item=pending.Dequeue();if(!item.Root||!item.Spawner)continue;
   if(item.Root.scene.name!=current.scene||now-item.Born>30){Discard(item,"placement_expired");continue;}
   if(now<item.RetryAt){pending.Enqueue(item);continue;}
   if(TryPosition(item,out var position)){
    item.Root.transform.position=position;item.Root.SetActive(true);framePositions.Add(position);recentActors[recentCursor]=item.Root;recentCursor=(recentCursor+1)%recentActors.Length;extrasPlaced++;
    if(GameplayLog.Operational&&spawnLogs++<64)GameplayLog.Record("enemy_extra_placed",new{batch=item.BatchId,ordinal=item.Ordinal,x=position.x,y=position.y,z=position.z,attempts=item.Attempts});
   }else{item.Attempts++;item.RetryAt=now+.5f;pending.Enqueue(item);}
  }
 }
 static void Discard(Pending item,string reason){if(item.Root)Object.Destroy(item.Root);extrasDiscarded++;if(GameplayLog.Operational&&spawnLogs++<64)GameplayLog.Record("enemy_extra_discarded",new{batch=item.BatchId,ordinal=item.Ordinal,reason,attempts=item.Attempts});}
 static bool TryPosition(Pending item,out Vector3 position){
  position=default;
  // SamplePosition, CalculatePath and OverlapCapsuleNonAlloc all have concrete
  // native bodies in the pinned dump. Stripped NavMesh.Raycast/CheckCapsule are
  // deliberately avoided. No Unity random state or global actor scan is used.
  if(item.MinimumPlayerDistance>0&&!item.Player)return false;
  if(!NavMesh.SamplePosition(item.Origin,out var origin,2,item.AreaMask)||Math.Abs(origin.position.y-item.Origin.y)>1.75f)return false;
  path??=new NavMeshPath();var radius=item.Radius;var height=Math.Max(1.8f,radius*2+.2f);
  for(var i=0;i<CandidatesPerAttempt;i++){
   var step=item.Attempts*CandidatesPerAttempt+i;var angle=(item.Ordinal*137.508f+step*137.508f)*Mathf.Deg2Rad;var distance=1.5f+.65f*((step/6+item.Ordinal)%7);
   var guess=origin.position+new Vector3(Mathf.Cos(angle)*distance,0,Mathf.Sin(angle)*distance);
   if(!NavMesh.SamplePosition(guess,out var hit,.65f,item.AreaMask)||Math.Abs(hit.position.y-origin.position.y)>.6f)continue;
   var at=hit.position;if((at-origin.position).sqrMagnitude<1.44f)continue;
   // Native wave selection keeps its marker at least 25m from the supplied
   // player. Preserve that distance, or its authored shorter distance, instead
   // of moving additional wave actors closer to the player.
   if(item.MinimumPlayerDistance>0&&Vector3.Distance(at,item.Player!.position)<item.MinimumPlayerDistance)continue;
   var close=false;foreach(var occupied in framePositions)if((occupied-at).sqrMagnitude<1.44f){close=true;break;}if(close)continue;
   var bottom=at+Vector3.up*(radius+.08f);var top=at+Vector3.up*(height-radius);
   if(Physics.OverlapCapsuleNonAlloc(bottom,top,radius,overlap,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)>0)continue;
   // Keep additions inside the marker's own visible, connected region; a
   // nearby room through a wall is not a safe placement for this batch.
   var sight=at-origin.position;var length=sight.magnitude;if(Physics.Raycast(origin.position+Vector3.up*.8f,sight/length,length,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore))continue;
   if(!NavMesh.CalculatePath(origin.position,at,item.AreaMask,path)||path.status!=NavMeshPathStatus.PathComplete)continue;
   position=at;return true;
  }
  return false;
 }
 internal static object Diagnostics()=>new{pending=pending.Count,nativeRequested,scaledRequested,nativeCreated,extrasQueued,extrasPlaced,extrasDiscarded,batches=batchSequence,queueLimit=QueueLimit,actorsPerFrame=ActorsPerFrame,candidatesPerAttempt=CandidatesPerAttempt};
}
