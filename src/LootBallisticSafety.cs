using Ballistics;
using Demo.Scripts.Runtime.Character;
using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;

namespace FractureTogether;

// Pinned LootData Item27 (Big Motor Oil) is Plane002: a rendered root with a
// solid BoxCollider, no Rigidbody and no BallisticObject. NPC bullets hit that
// exact transform and native UpdateBullets dereferences its missing surface.
static class LootBallisticSafety
{
 static Inventory? inventory;
 internal static long Repaired,Unresolved;
 internal static string LastSource="";

 static bool ValidMaterial(BallisticsSettings settings,int id)
 {
  if(!settings||settings.MaterialData==null||id<0||id>=settings.MaterialData.Count)return false;
  var material=settings.MaterialData[id];return material.RicochetPropability!=null&&material.impactObject;
 }

 // The scope is native generated geometry or the already-audited Home Props
 // root. A live/remote player, inventory preview or detached drop is outside it.
 static Transform? ScopeRoot(Transform item)
 {
  var scene=item.gameObject.scene.name;var child=item;
  if(scene!="URP_OfficeCorridor"&&scene!="Example_Scene_Night 1")return null;
  for(var parent=item.parent;parent;child=parent,parent=parent.parent)
  {
   if(scene=="URP_OfficeCorridor"&&parent.name=="GeneratedLevel"&&parent.parent&&parent.parent.GetComponent<LevelElevator>())return child;
   if(scene=="Example_Scene_Night 1"&&parent.name=="Props"&&!parent.parent)return parent;
  }
  return null;
 }

 static GameObject? CanonicalPrefab(LootableItem item)
 {
  var owner=item.Inventory;if(!owner)owner=inventory;if(!owner)owner=Object.FindAnyObjectByType<Inventory>();
  if(!owner||!owner.LootData||owner.LootData.Loot==null)return null;inventory=owner;
  foreach(var entry in owner.LootData.Loot)
  {
   // Match native List.Find order and the actual active inventory catalogue.
   if(entry==null)return null;if(entry.Id==27)return entry.Prefab;
  }
  return null;
 }

 static bool BlocksMaterialSource(Transform node,Transform root)
 {
  for(var parent=node;parent;parent=parent.parent)
  {
   if(parent.GetComponent<LootableItem>()||parent.GetComponent<LootableChanceItems>()||parent.GetComponent<FastHealItem>()||parent.GetComponent<LivingEntity>()||parent.GetComponent<LivingEntityCollider>()||parent.GetComponent<FPSController>()||parent.GetComponent<EnemyHealth>()||parent.GetComponent<StateController>()||parent.GetComponent<Grenade>())return true;
   if(parent==root)break;
  }
  return false;
 }
 static bool AuthoredSource(DefaultBallisticObject source,Transform root,BallisticsSettings settings)
 {
  return source&&ValidMaterial(settings,source.MatID)&&
   (Together.Current==null||!Together.Current.RuntimeWorldBallisticSurface(source))&&!BlocksMaterialSource(source.transform,root);
 }
 static float SurfaceDistanceSquared(Collider collider,Vector3 point)
 {
  var mesh=collider.TryCast<MeshCollider>();
  // Unity only supports ClosestPoint for primitives and convex meshes. Native
  // room floors/walls also contain non-convex meshes; their world bounds give
  // a safe proximity estimate for material provenance, without a physics call.
  if(collider.TryCast<BoxCollider>()||collider.TryCast<SphereCollider>()||collider.TryCast<CapsuleCollider>()||(mesh&&mesh.convex))return (collider.ClosestPoint(point)-point).sqrMagnitude;
  var bounds=collider.bounds;var offset=point-bounds.center;var extents=bounds.extents;
  var x=Math.Max(0,Math.Abs(offset.x)-extents.x);var y=Math.Max(0,Math.Abs(offset.y)-extents.y);var z=Math.Max(0,Math.Abs(offset.z)-extents.z);
  return x*x+y*y+z*z;
 }
 static DefaultBallisticObject? MaterialSource(Transform item,Transform root,BallisticsSettings settings,Vector3 point)
 {
  // An explicit authored ancestor material is stronger evidence than proximity.
  for(var parent=item.parent;parent;parent=parent.parent)
  {
   var source=parent.GetComponent<DefaultBallisticObject>();if(AuthoredSource(source,root,settings))return source;
   if(parent==root)break;
  }
  // Local, one-shot bounded search. No global census or per-frame work. Only
  // solid native geometry supplies the closest material; pickups/actors and
  // runtime world repairs cannot become provenance for another repair.
  var queue=new Queue<Transform>();queue.Enqueue(root);var visited=0;var best=float.PositiveInfinity;DefaultBallisticObject? found=null;var ambiguous=false;
  while(queue.Count>0&&visited<256)
  {
   var node=queue.Dequeue();visited++;if(!node)continue;
   for(var i=0;i<node.childCount&&visited+queue.Count<256;i++)queue.Enqueue(node.GetChild(i));
   var source=node.GetComponent<DefaultBallisticObject>();if(!AuthoredSource(source,root,settings))continue;
   var distance=float.PositiveInfinity;
   foreach(var collider in node.GetComponents<Collider>())if(collider&&collider.enabled&&collider.gameObject.activeInHierarchy&&!collider.isTrigger&&!collider.attachedRigidbody)distance=Math.Min(distance,SurfaceDistanceSquared(collider,point));
   if(!float.IsFinite(distance))continue;
   if(distance<best-.000001f){best=distance;found=source;ambiguous=false;}
   else if(Math.Abs(distance-best)<=.000001f&&found&&source.MatID!=found.MatID)ambiguous=true;
  }
  return ambiguous?null:found;
 }

 internal static void Repair(LootableItem item)
 {
  if(!item||item.ItemType!=LootableItem.LootType.Item||item.ItemId!=27||!item.gameObject.scene.IsValid()||item.GetComponent<BallisticObject>())return;
  var root=ScopeRoot(item.transform);if(!root)return;
  var source=CanonicalPrefab(item);if(!source)return;
  var sourceItem=source.GetComponent<LootableItem>();var sourceMesh=source.GetComponent<MeshFilter>();var mesh=item.GetComponent<MeshFilter>();
  var sourceBox=source.GetComponent<BoxCollider>();var box=item.GetComponent<BoxCollider>();
  // Match the actual authored prefab/physical root rather than a similarly
  // named LOD or arbitrary LootableItem mesh. All collider settings stay intact.
  if(!sourceItem||sourceItem.ItemType!=item.ItemType||sourceItem.ItemId!=item.ItemId||!sourceMesh||!mesh||!sourceMesh.sharedMesh||sourceMesh.sharedMesh!=mesh.sharedMesh||!sourceBox||!box||sourceBox.isTrigger||box.isTrigger||sourceBox.attachedRigidbody||box.attachedRigidbody||source.GetComponent<Rigidbody>()||item.GetComponent<Rigidbody>()||!item.GetComponent<Renderer>()||
   (sourceBox.size-box.size).sqrMagnitude>.000001f||(sourceBox.center-box.center).sqrMagnitude>.000001f)return;
  var handler=BulletHandler.instance;var settings=handler?handler.Settings:null;if(!settings){Unresolved++;return;}
  var inherited=MaterialSource(item.transform,root,settings,box.transform.TransformPoint(box.center));if(!inherited){Unresolved++;return;}
  var surface=item.gameObject.AddComponent<DefaultBallisticObject>();surface.MatID=inherited.MatID;surface.isStatic=false;
  Repaired++;LastSource=inherited.name+"/"+settings.MaterialData[inherited.MatID].Name;
  if(Repaired<=8)Plugin.Instance.Log.LogInfo("Repaired native loot ballistic surface: Item27 "+item.name+", prefab="+source.name+", material="+surface.MatID+", authored source="+LastSource);
 }
 internal static void Spawned(LootSpawnPoint point,int previousChildren)
 {
  if(!point)return;
  for(var i=Math.Max(0,previousChildren);i<point.transform.childCount;i++)
  {
   var child=point.transform.GetChild(i);var item=child.GetComponent<LootableItem>();if(item)Repair(item);
  }
 }
}

public sealed partial class Together
{
 [HideFromIl2Cpp]
 internal bool RuntimeWorldBallisticSurface(BallisticObject surface)=>surface&&worldBallisticAdded.ContainsKey(surface.Pointer);
}

[HarmonyPatch(typeof(LootSpawnPoint),nameof(LootSpawnPoint.OnEnable))]
static class SpawnedLootBallisticSurface
{
 static void Prefix(LootSpawnPoint __instance,out int __state)=>__state=__instance?__instance.transform.childCount:0;
 static void Postfix(LootSpawnPoint __instance,int __state)=>LootBallisticSafety.Spawned(__instance,__state);
}
[HarmonyPatch(typeof(LootableItem),"Start")]
static class AuthoredLootBallisticSurface
{
 static void Postfix(LootableItem __instance)=>LootBallisticSafety.Repair(__instance);
}
