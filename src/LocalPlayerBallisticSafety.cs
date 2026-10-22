using Ballistics;
using Demo.Scripts.Runtime.Character;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

// Native UpperChest has a physical Player-layer BoxCollider but neither of the
// components UpdateBullets requires on RaycastHit.transform. Use the player's
// authored damage route and material; never create another health component.
static class LocalPlayerBallisticSafety
{
 static IntPtr body,health;
 static float retryAt;
 static bool complete;
 static int repaired,unresolved;
 static string status="waiting",sourceName="";
 static readonly HashSet<IntPtr> added=new();

 internal static void Tick(FPSController? player){
  if(!player||!player.Health)return;
  var scene=SceneManager.GetActiveScene().name;
  if((scene!="URP_OfficeCorridor"&&scene!="Example_Scene_Night 1")||player.gameObject.scene.name!=scene)return;
  if(body!=player.Pointer||health!=player.Health.Pointer){body=player.Pointer;health=player.Health.Pointer;retryAt=0;complete=false;repaired=unresolved=0;added.Clear();status="waiting";sourceName="";}
  if(complete||Time.unscaledTime<retryAt)return;retryAt=Time.unscaledTime+1;
  var skeleton=player.transform.Find("Skeleton");var handler=BulletHandler.instance;var settings=handler?handler.Settings:null;
  if(!skeleton||!settings||settings.MaterialData==null){status="waiting for native body/materials";return;}
  var sources=new List<LivingEntityCollider>();
  foreach(var part in player.GetComponentsInChildren<LivingEntityCollider>(true)){
   if(!part||added.Contains(part.Pointer)||!part.GetComponent<Collider>()||!part.ParentLivingEntity||part.ParentLivingEntity.PlayerHealthManager!=player.Health)continue;
   var material=part.GetComponent<BallisticObject>();if(!material||material.isStatic||!ValidMaterial(settings,material.MatID)||!float.IsFinite(part.DamageMultiplier)||part.DamageMultiplier<=0)continue;
   sources.Add(part);
  }
  if(sources.Count==0){status="waiting for canonical native player hitbox";return;}
  unresolved=0;
  foreach(var collider in skeleton.GetComponentsInChildren<Collider>(true)){
   // This repair targets the observed physical bones, not the movement capsule,
   // weapons, triggers, or a Rigidbody whose ray-hit transform is elsewhere.
   if(!collider||collider.isTrigger||collider.attachedRigidbody||collider.gameObject.layer!=sources[0].gameObject.layer)continue;
   if(!collider.GetComponent<BoxCollider>()&&!collider.GetComponent<CapsuleCollider>()&&!collider.GetComponent<SphereCollider>())continue;
   var target=collider.transform;if(target.GetComponent<BallisticObject>())continue;
   var existing=target.GetComponent<LivingEntityCollider>();
   if(existing&&(!existing.ParentLivingEntity||existing.ParentLivingEntity.PlayerHealthManager!=player.Health))continue;
   var source=SourceFor(target,sources);if(!source){unresolved++;continue;}
   var native=source.GetComponent<BallisticObject>();
   var entity=existing?existing:target.gameObject.AddComponent<LivingEntityCollider>();
   if(!existing){entity.ParentLivingEntity=source.ParentLivingEntity;entity.DamageMultiplier=source.DamageMultiplier;added.Add(entity.Pointer);}
   var surface=target.gameObject.AddComponent<DefaultBallisticObject>();surface.MatID=native.MatID;surface.isStatic=false;
   sourceName=source.name;repaired++;
  }
  complete=unresolved==0;status=complete?"ready":"ambiguous native player damage routes";
 }
 static bool ValidMaterial(BallisticsSettings settings,int id){if(id<0||id>=settings.MaterialData.Count)return false;var material=settings.MaterialData[id];return material.RicochetPropability!=null&&material.impactObject;}
 static LivingEntityCollider? SourceFor(Transform target,List<LivingEntityCollider> sources){
  // Prefer an authored ancestor's anatomical damage multiplier. Otherwise use
  // the canonical route only if every physical native source agrees with it.
  for(var parent=target;parent;parent=parent.parent)foreach(var source in sources)if(source.transform==parent)return source;
  var first=sources[0];var material=first.GetComponent<BallisticObject>().MatID;
  foreach(var source in sources)if(source.ParentLivingEntity!=first.ParentLivingEntity||source.GetComponent<BallisticObject>().MatID!=material||source.DamageMultiplier!=first.DamageMultiplier)return null;
  return first;
 }
 internal static object Diagnostics()=>new{body=body.ToInt64(),health=health.ToInt64(),status,complete,repaired,unresolved,source=sourceName};
}
