using Ballistics;
using Demo.Scripts.Runtime.Character;
using DungeonArchitect;
using EnemyAI;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FractureTogether;

public sealed partial class Together
{
 readonly Stack<Transform> worldBallisticNodes=new();
 readonly HashSet<IntPtr> worldBallisticQueued=new(),worldBallisticChecked=new();
 readonly Dictionary<IntPtr,DefaultBallisticObject> worldBallisticAdded=new();
 readonly Dictionary<IntPtr,Transform> worldBallisticCabins=new();
 Transform? worldBallisticRoot;
 Transform? worldBallisticExpanding;int worldBallisticChild=-1;
 BallisticsSettings? worldBallisticSettings;
 string worldBallisticScene="";int worldBallisticRevision=-1,worldBallisticMetal=-1,worldBallisticStone=-1,worldBallisticRepaired,worldBallisticButtonSurfaces;
 bool worldBallisticStarted,worldBallisticFinished;

 // An authored utility mesh has a solid Default collider but no BallisticObject.
 // Native UpdateBullets dereferences that missing component after its raycast.
 // Repair generated static geometry and the native Home Props subtree once;
 // ambient Home NPCs also shoot these authored colliders while offline.
 void WorldBallisticSafetyTick(){
  LocalPlayerBallisticSafety.Tick(player);
  var active=SceneManager.GetActiveScene().name;var revision=DungeonSeedPatch.Revision;
  if(worldBallisticScene!=active||worldBallisticRevision!=revision){
   if(worldBallisticScene!=active)worldBallisticAdded.Clear();else foreach(var pointer in worldBallisticAdded.Where(p=>!p.Value).Select(p=>p.Key).ToArray())worldBallisticAdded.Remove(pointer);
   worldBallisticScene=active;worldBallisticRevision=revision;worldBallisticNodes.Clear();worldBallisticQueued.Clear();worldBallisticChecked.Clear();worldBallisticCabins.Clear();
   worldBallisticRoot=null;worldBallisticExpanding=null;worldBallisticChild=-1;worldBallisticSettings=null;worldBallisticMetal=worldBallisticStone=-1;worldBallisticRepaired=worldBallisticButtonSurfaces=0;worldBallisticStarted=worldBallisticFinished=false;
  }
  var raid=active=="URP_OfficeCorridor";var home=active=="Example_Scene_Night 1";
  if((!raid&&!home)||(raid&&DungeonSeedPatch.Building)||worldBallisticFinished)return;
  if(!worldBallisticStarted){
   Transform? root;
   if(home){
    // The exception census proved scene-root Props/Distant_Building_B (4).
    // Restrict Home discovery to that native root, never the whole scene.
    var props=GameObject.Find("/Props");if(!props||props.transform.parent||props.scene.name!=active)return;root=props.transform;
   }else if(floor==0&&!DungeonSeedPatch.Latest){
    // Native exception census proved /Lobby/Meshes/Cube11 on the authored
    // entrance street. Penetrating bullets can reach it after their first hit
    // within one native update, beyond the single-segment preflight guard.
    var meshes=GameObject.Find("/Lobby/Meshes");
    if(!meshes||meshes.scene.name!=active||!meshes.transform.parent||meshes.transform.parent.name!="Lobby"||meshes.transform.parent.parent)return;
    root=meshes.transform;
   }else{
    var dungeon=DungeonSeedPatch.Latest;if(!dungeon||dungeon.gameObject.scene.name!=active)return;
    var provider=dungeon.GetComponent<DungeonSceneProvider>();root=provider&&provider.itemParent?provider.itemParent.transform:null;
    if(!root||root.childCount==0){if(elevator&&elevator.levelParent&&elevator.levelParent.childCount>0)root=elevator.levelParent;else return;}
   }
   var handler=BulletHandler.instance;var settings=handler?handler.Settings:null;if(!settings||settings.MaterialData==null||settings.MaterialData.Count==0)return;
   worldBallisticRoot=root;worldBallisticSettings=settings;worldBallisticMetal=UniqueWorldMaterial("Metal");worldBallisticStone=UniqueWorldMaterial("Stone");
   worldBallisticStarted=true;QueueWorldBallisticNode(root);
   var arrival=root.GetComponentInParent<LevelElevator>();if(arrival)RegisterWorldBallisticCabin(arrival);
  }
  var childBudget=64;
  for(var n=0;n<64;n++){
   if(worldBallisticExpanding){
    worldBallisticChild=Math.Min(worldBallisticChild,worldBallisticExpanding!.childCount-1);
    while(worldBallisticChild>=0&&childBudget>0){QueueWorldBallisticNode(worldBallisticExpanding.GetChild(worldBallisticChild--));childBudget--;}
    if(worldBallisticChild>=0)break;
    worldBallisticExpanding=null;
   }
   if(worldBallisticNodes.Count==0)break;
   var node=worldBallisticNodes.Pop();worldBallisticQueued.Remove(node.Pointer);if(!node||node.gameObject.scene.name!=active)continue;
   worldBallisticExpanding=node;worldBallisticChild=node.childCount-1;
   RepairWorldBallisticNode(node);
  }
  if(worldBallisticNodes.Count==0&&!worldBallisticExpanding){worldBallisticFinished=true;if(worldBallisticRepaired>0)Plugin.Instance.Log.LogInfo("Repaired native static-world ballistic surfaces: "+worldBallisticRepaired);}
 }
 void QueueWorldBallisticNode(Transform node){if(node&&!worldBallisticChecked.Contains(node.Pointer)&&worldBallisticQueued.Add(node.Pointer))worldBallisticNodes.Push(node);}
 // The native departure cabin is detached from GeneratedLevel during startup.
 // Remember only a cabin whose native hierarchy already proves this generation.
 [HideFromIl2Cpp]
 internal void RegisterWorldBallisticCabin(LevelElevator lift){
  if(!lift||!worldBallisticStarted||!worldBallisticRoot||lift.gameObject.scene.name!=worldBallisticScene)return;
  var root=lift.transform;
  if(!root.IsChildOf(worldBallisticRoot)&&!worldBallisticRoot.IsChildOf(root))return;
  if(worldBallisticCabins.TryAdd(root.Pointer,root)){worldBallisticFinished=false;QueueWorldBallisticNode(root);}
 }
 bool WithinWorldBallisticScope(Transform node){
  if(worldBallisticRoot&&(node==worldBallisticRoot||node.IsChildOf(worldBallisticRoot)))return true;
  foreach(var root in worldBallisticCabins.Values)if(root&&(node==root||node.IsChildOf(root)))return true;
  return false;
 }
 bool CabinWorldGeometry(Transform node){
  for(var parent=node;parent;parent=parent.parent){if(parent==worldBallisticRoot)return false;if(parent.GetComponent<LevelElevator>())return true;}
  return false;
 }
 bool ValidWorldMaterial(int id){var settings=worldBallisticSettings;if(!settings||settings.MaterialData==null||id<0||id>=settings.MaterialData.Count)return false;var material=settings.MaterialData[id];return material.RicochetPropability!=null&&material.impactObject;}
 int UniqueWorldMaterial(string name){
  var settings=worldBallisticSettings;if(!settings||settings.MaterialData==null)return -1;var match=-1;
  for(var i=0;i<settings.MaterialData.Count;i++)if(string.Equals(settings.MaterialData[i].Name?.Trim(),name,StringComparison.OrdinalIgnoreCase)){if(match>=0)return -1;match=i;}
  return ValidWorldMaterial(match)?match:-1;
 }
 bool NativeWorldSurface(BallisticObject surface)=>surface&&surface.isStatic&&!worldBallisticAdded.ContainsKey(surface.Pointer)&&ValidWorldMaterial(surface.MatID);
 int InheritedWorldMaterial(Transform node){
  for(var parent=node.parent;parent;parent=parent.parent){var surface=parent.GetComponent<BallisticObject>();if(NativeWorldSurface(surface))return surface.MatID;if(parent==worldBallisticRoot)break;}
  var best=-1;var distance=float.PositiveInfinity;var ambiguous=false;var remaining=32;
  for(var parent=node.parent;parent&&remaining>0;parent=parent.parent){
   for(var i=0;i<parent.childCount&&remaining>0;i++,remaining--){var sibling=parent.GetChild(i);if(sibling==node)continue;var surface=sibling.GetComponent<BallisticObject>();if(!NativeWorldSurface(surface))continue;
    var candidate=(sibling.position-node.position).sqrMagnitude;if(candidate<distance-.0001f){distance=candidate;best=surface.MatID;ambiguous=false;}else if(Math.Abs(candidate-distance)<=.0001f&&best!=surface.MatID)ambiguous=true;
   }
   // Search only the actual sibling level, bounded regardless of module size.
   break;
  }
  return ambiguous?-1:best;
 }
 bool ExcludedWorldBallisticOwner(Transform node,bool buttonSurface=false){
  // GeneratedLevel itself belongs to the arrival elevator. Stop at the generated
  // root so its outer cabin does not incorrectly classify every room as a lift.
  for(var parent=node;parent;parent=parent.parent){if(parent.GetComponent<EnemyHealth>()||parent.GetComponent<StateController>()||parent.GetComponent<EnemySpawner>()||parent.GetComponent<FPSController>()||parent.GetComponent<LivingEntity>()||parent.GetComponent<LivingEntityCollider>()||parent.GetComponent<LootableItem>()||parent.GetComponent<LootableChanceItems>()||parent.GetComponent<FastHealItem>()||parent.GetComponent<Grenade>()||parent.GetComponent<Door>()||(!buttonSurface&&parent.GetComponent<ElevatorButton>()))return true;if(parent==worldBallisticRoot||worldBallisticCabins.ContainsKey(parent.Pointer))break;}
  return false;
 }
 BallisticObject? RenderedElevatorButtonSurface(Transform node){
  // Authored Office_EndOfFloor/DoorC/Door is an ElevatorButton, not a Door.
  // Its solid rendered child has no ballistic surface; its immediate parent
  // already supplies the correct authored material and non-static flag.
  // Ordinary buttons/trigger volumes without this exact evidence stay excluded.
  var button=node.GetComponent<ElevatorButton>();
  if(!button||button.type!=ElevatorButton.ButtonType.Down||!button.elevator||!node.parent||node.name!="Door"||node.parent.name!="DoorC"||!node.GetComponent<MeshFilter>()||!node.GetComponent<Renderer>()||
   (!node.GetComponent<BoxCollider>()&&!node.GetComponent<MeshCollider>()))return null;
  var authored=node.parent.GetComponent<BallisticObject>();
  return authored&&!worldBallisticAdded.ContainsKey(authored.Pointer)&&ValidWorldMaterial(authored.MatID)?authored:null;
 }
 static bool UtilityWorldGeometry(Transform node){for(var parent=node;parent;parent=parent.parent){var name=parent.name.ToLowerInvariant();if(name.Contains("utilities")||name.Contains("wire_basket")||name.Contains("cable")||name.Contains("pipe")||name.Contains("duct"))return true;if(parent.name=="GeneratedLevel")break;}return false;}
 [HideFromIl2Cpp]
 internal void RepairWorldBallisticNode(Transform node){
  if(!node||!worldBallisticRoot||!worldBallisticSettings||!WithinWorldBallisticScope(node)||!worldBallisticChecked.Add(node.Pointer)||node.gameObject.layer!=0)return;
  var colliders=node.GetComponents<Collider>();Collider? solid=null;foreach(var collider in colliders)if(collider&&!collider.isTrigger&&!collider.attachedRigidbody){solid=collider;break;}if(!solid)return;
  // With no attached Rigidbody, RaycastHit.transform is precisely this collider's
  // transform. Existing authored surfaces, actors and pickups are never replaced.
  var hitTransform=solid.transform;var buttonSurface=RenderedElevatorButtonSurface(hitTransform);
  if(hitTransform.GetComponent<BallisticObject>()||ExcludedWorldBallisticOwner(hitTransform,buttonSurface))return;
  // A cabin contains buttons and interaction volumes as well as walls. Only its
  // actual box/mesh geometry may receive a missing static bullet surface.
  if(CabinWorldGeometry(hitTransform)&&((!hitTransform.GetComponent<BoxCollider>()&&!hitTransform.GetComponent<MeshCollider>())||(!hitTransform.GetComponent<MeshFilter>()&&!hitTransform.GetComponent<Renderer>())))return;
  var material=buttonSurface?buttonSurface.MatID:InheritedWorldMaterial(hitTransform);if(material<0)material=UtilityWorldGeometry(hitTransform)?worldBallisticMetal:worldBallisticStone;if(!ValidWorldMaterial(material))return;
  var surface=hitTransform.gameObject.AddComponent<DefaultBallisticObject>();surface.MatID=material;
  surface.isStatic=buttonSurface?buttonSurface.isStatic:true;
  worldBallisticAdded[surface.Pointer]=surface;worldBallisticRepaired++;if(buttonSurface)worldBallisticButtonSurfaces++;
 }
 [HideFromIl2Cpp]
 internal object WorldBallisticSafetyDiagnostics()=>new{scene=worldBallisticScene,root=worldBallisticRoot?worldBallisticRoot.name:null,revision=worldBallisticRevision,started=worldBallisticStarted,finished=worldBallisticFinished,pending=worldBallisticNodes.Count,checkedTransforms=worldBallisticChecked.Count,repaired=worldBallisticRepaired,elevatorButtonSurfaces=worldBallisticButtonSurfaces,metal=worldBallisticMetal,stone=worldBallisticStone,cabins=worldBallisticCabins.Count,localPlayer=LocalPlayerBallisticSafety.Diagnostics()};
}

[HarmonyPatch(typeof(LevelElevator),nameof(LevelElevator.PlayStartupAnim))]
static class BallisticDepartureProvenance
{
 static void Prefix(LevelElevator __instance)=>Together.Current?.RegisterWorldBallisticCabin(__instance);
}
