using Ballistics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

// Armed native mines expose their LivingEntityCollider on a trigger. Their
// solid loot collider is intentionally disabled until disarmed. Keep the
// general trigger-ray exclusion, exposing only proven mine surfaces during
// the synchronous native bullet update, without enabling any other collider.
internal static class MineShotCollisionScope
{
 const int Limit=128;
 sealed class Mine {internal Landmine Owner=null!;internal Collider Trigger=null!;internal LivingEntity Entity=null!;internal LivingEntityCollider Bridge=null!;internal DefaultBallisticObject Surface=null!;}
 internal sealed class Scope {
  internal readonly Collider[] Changed=new Collider[Limit];internal int Count;
  internal void Restore(){
   for(var i=Count-1;i>=0;i--){var collider=Changed[i];Changed[i]=null!;try{if(collider&&!collider.isTrigger)collider.isTrigger=true;}catch(Exception error){GameplayLog.Fault("mine_trigger_restore",error);}}
   Count=0;
  }
 }
 static readonly Dictionary<IntPtr,Mine> mines=new();
 static readonly IntPtr[] stale=new IntPtr[Limit];
 static readonly Stack<Scope> available=new(new[]{new Scope(),new Scope()});
 static int sceneHandle=int.MinValue;
 // Start can run before the build finalizer increments its revision. Preserve
 // exact live scene objects across that increment; destruction is pruned below.
 static void EnsureScene(){var handle=SceneManager.GetActiveScene().handle;if(handle==sceneHandle)return;mines.Clear();sceneHandle=handle;}
 internal static void Register(Landmine owner){
  try{
   EnsureScene();if(!owner||owner.gameObject.scene.handle!=sceneHandle||!owner.Trigger)return;
   var trigger=owner.Trigger;if(trigger.gameObject.Pointer!=owner.gameObject.Pointer)return;
   var entity=owner.GetComponent<LivingEntity>();var bridge=trigger.GetComponent<LivingEntityCollider>();var surface=trigger.GetComponent<DefaultBallisticObject>();
   if(!entity||entity.EnemyHealth||entity.PlayerHealthManager||!entity.ExplodingObject||!bridge||bridge.ParentLivingEntity!=entity||!surface)return;
   // RaycastHit.transform follows the attached rigidbody; accept only the
   // authored root carrying both the native material and health bridge.
   if(trigger.attachedRigidbody&&trigger.attachedRigidbody.transform!=trigger.transform)return;
   if(mines.Count>=Limit){var count=0;foreach(var pair in mines)if(!pair.Value.Owner||!pair.Value.Trigger||!pair.Value.Entity){stale[count++]=pair.Key;}for(var i=0;i<count;i++)mines.Remove(stale[i]);}
   if(mines.Count<Limit||mines.ContainsKey(owner.Pointer))mines[owner.Pointer]=new Mine{Owner=owner,Trigger=trigger,Entity=entity,Bridge=bridge,Surface=surface};
  }catch(Exception error){GameplayLog.Fault("mine_trigger_register",error);}
 }
 internal static Scope? Begin(BulletHandler handler){
  // NativeBallisticQueries excludes trigger rays in every mode, including
  // offline play. Preserve native mine shooting without requiring a session.
  if(!handler||handler.Bullets==null||handler.Bullets.Count==0)return null;
  Scope? scope=null;
  try{
   EnsureScene();var settings=handler.Settings;if(!settings||!settings.useBallisticMaterials||settings.MaterialData==null)return null;
   // Native UpdateBullets is synchronous. Two pooled scopes also support
   // nesting without sharing restoration state; unusual deeper nesting skips.
   if(available.Count==0)return null;scope=available.Pop();var staleCount=0;
   foreach(var pair in mines){
    var mine=pair.Value;var collider=mine.Trigger;
    if(!mine.Owner||!collider||!mine.Entity||!mine.Bridge||!mine.Surface){if(staleCount<Limit)stale[staleCount++]=pair.Key;continue;}
    if(collider.gameObject.scene.handle!=sceneHandle||!collider.enabled||!collider.gameObject.activeInHierarchy||!collider.isTrigger||mine.Owner.Trigger!=collider||mine.Bridge.ParentLivingEntity!=mine.Entity||!mine.Entity.ExplodingObject)continue;
    var id=mine.Surface.MatID;if(id<0||id>=settings.MaterialData.Count)continue;
    var material=settings.MaterialData[id];if(material.RicochetPropability==null||!material.impactObject)continue;
    scope.Changed[scope.Count++]=collider;collider.isTrigger=false;
   }
   for(var i=0;i<staleCount;i++)mines.Remove(stale[i]);
   return scope;
  }catch(Exception error){if(scope!=null){scope.Restore();available.Push(scope);}GameplayLog.Fault("mine_trigger_scope",error);return null;}
 }
 internal static void End(Scope? scope){if(scope==null)return;try{scope.Restore();}finally{available.Push(scope);}}
}

[HarmonyPatch(typeof(Landmine),nameof(Landmine.Start))]
static class RememberShootableMine {static void Postfix(Landmine __instance)=>MineShotCollisionScope.Register(__instance);}

[HarmonyPatch(typeof(BulletHandler),"UpdateBullets")]
static class MineBulletQueries
{
 [HarmonyPriority(Priority.Last)]
 static void Prefix(BulletHandler __instance,out MineShotCollisionScope.Scope? __state){__state=null;__state=MineShotCollisionScope.Begin(__instance);}
 [HarmonyPriority(Priority.First)]
 static Exception? Finalizer(MineShotCollisionScope.Scope? __state,Exception? __exception){MineShotCollisionScope.End(__state);return __exception;}
}
