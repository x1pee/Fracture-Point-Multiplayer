using Ballistics;
using UnityEngine;
namespace FractureTogether;

// Pinned native UpdateBullets dereferences parentWeapon and the exact
// RaycastHit.transform's material without null checks. Validate only bullets
// the next native update can process; valid bullets retain native simulation.
static class NativeBulletSafety
{
 const int ScanLimit=4096;
 const int RayLimit=32,RayQueueLimit=256;
 static readonly HashSet<IntPtr> invalid=new();
 static readonly Dictionary<string,int> reports=new(StringComparer.Ordinal);
 internal static long Quarantined,InheritedSurfaces,Deferred,Inspected,PreflightRays,PreflightSkipped;
 static int rayFrame=-1,frameRays,maxFrameRays;
 static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
 static bool MaterialValid(BallisticsSettings settings,int id)=>settings.MaterialData!=null&&id>=0&&id<settings.MaterialData.Count&&settings.MaterialData[id].RicochetPropability!=null&&settings.MaterialData[id].impactObject;
 static void Record(string reason,BulletHandler handler,int count=1,int colliderId=0){
  var seen=reports.GetValueOrDefault(reason);reports[reason]=seen+count;
  if(seen>=8||!GameplayLog.Operational)return;
  GameplayLog.Record("bullet.quarantine",new{reason,handlerId=handler.GetInstanceID(),count,colliderId,total=Quarantined});
 }
 static string? Unsafe(BulletHandler handler,BulletData bullet,bool preflight,out int colliderId){
  colliderId=0;var weapon=bullet.parentWeapon;if(!weapon)return "orphan_weapon";
  if(!Finite(bullet.bulletPos)||!Finite(bullet.LastPos)||!Finite(bullet.bulletDir)||!float.IsFinite(bullet.Speed)||!float.IsFinite(bullet.ySpeed)||!float.IsFinite(bullet.lifeTime)||!float.IsFinite(bullet.timeSinceLastUpdate))return "nonfinite_bullet";
  if(bullet.BulletTrans&&!weapon.BulletPref)return "missing_visual_prefab";
  if(!preflight||frameRays>=RayLimit){PreflightSkipped++;return null;}
  var dt=Time.deltaTime+Math.Max(0,bullet.timeSinceLastUpdate);
  if(!float.IsFinite(dt)||dt<=0||bullet.lifeTime-dt<=0)return null;
  var settings=handler.Settings;var speed=bullet.Speed;
  if(settings.useBulletdrag){if(!float.IsFinite(weapon.PreDrag))return "nonfinite_drag";speed-=speed*speed*weapon.PreDrag*dt;}
  var next=bullet.bulletPos+bullet.bulletDir*(speed*dt);
  if(settings.useBulletdrop){var vertical=bullet.ySpeed+handler.g*dt;if(settings.useBulletdrag)vertical+=vertical*vertical*weapon.PreDrag*dt;next.y+=vertical*dt;}
  if(!Finite(next))return "nonfinite_step";
  var delta=next-bullet.LastPos;var distance=delta.magnitude;if(distance<=.000001f)return null;
  frameRays++;PreflightRays++;maxFrameRays=Math.Max(maxFrameRays,frameRays);
  if(!Physics.Raycast(new Ray(bullet.LastPos,delta/distance),out var hit,distance,weapon.HitMask,QueryTriggerInteraction.Ignore))return null;
  var collider=hit.collider;var transform=hit.transform;if(!collider||!transform)return "invalid_ray_target";colliderId=collider.GetInstanceID();
  if(!settings.useBallisticMaterials)return "disabled_native_materials";
  var surface=transform.GetComponent<BallisticObject>();
  if(!surface){
   // Native RaycastHit.transform follows attachedRigidbody. An authored surface
   // on that exact collider is a proven source for its missing Rigidbody root.
   var source=collider.GetComponent<BallisticObject>();
   if(source&&transform!=collider.transform&&MaterialValid(settings,source.MatID)){
    var inherited=transform.gameObject.AddComponent<DefaultBallisticObject>();inherited.MatID=source.MatID;inherited.isStatic=source.isStatic;surface=inherited;InheritedSurfaces++;
   }else{
    Together.Current?.RepairWorldBallisticNode(transform);surface=transform.GetComponent<BallisticObject>();
   }
  }
  if(!surface)return "missing_surface";
  if(!MaterialValid(settings,surface.MatID))return "invalid_surface_material";
  var living=transform.GetComponent<LivingEntityCollider>();
  if(living&&!living.ParentLivingEntity)return "missing_living_owner";
  return null;
 }
 internal static bool Prepare(BulletHandler handler){
  var queue=handler.Bullets;if(queue==null||queue.Count==0)return false;
  if(rayFrame!=Time.frameCount){rayFrame=Time.frameCount;frameRays=0;}
  if(!handler.Settings||!handler.myPool){Deferred++;Record("handler_not_ready",handler,queue.Count);return false;}
  invalid.Clear();var removeNull=false;var inspected=0;var valid=0;var budget=Math.Max(1,handler.MaxBulletUpdatesPerFrame);var preflight=queue.Count<=RayQueueLimit;
  foreach(var bullet in queue){
   if(inspected++>=ScanLimit||valid>=budget)break;
   Inspected++;var colliderId=0;var reason=bullet==null?"null_bullet":Unsafe(handler,bullet,preflight,out colliderId);
   if(reason==null){valid++;continue;}
   if(bullet!=null){invalid.Add(bullet.Pointer);if(bullet.BulletTrans)bullet.BulletTrans.gameObject.SetActive(false);}
   else removeNull=true;
   Quarantined++;Record(reason,handler,1,colliderId);
  }
  if(invalid.Count>0||removeNull){
   // A full rotation preserves FIFO for every retained valid entry. A partial
   // rotation would reorder trajectories and is deliberately not used.
   var count=queue.Count;for(var i=0;i<count;i++){var bullet=queue.Dequeue();if(bullet==null){if(!removeNull)queue.Enqueue(bullet);continue;}if(!invalid.Contains(bullet.Pointer))queue.Enqueue(bullet);}
  }
  return queue.Count>0;
 }
 internal static object Diagnostics()=>new{Quarantined,InheritedSurfaces,Deferred,Inspected,PreflightRays,PreflightSkipped,frameRays,maxFrameRays,rayLimit=RayLimit,rayQueueLimit=RayQueueLimit};
}
