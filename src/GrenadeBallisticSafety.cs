using Ballistics;
using HarmonyLib;
using ImpactCFX;
using UnityEngine;

namespace FractureTogether;

// Both native F1 variants omit a ballistic surface on their Rigidbody root.
// Their authored ImpactCFX TinyNail material is also used by native AK/AR/TT
// physical weapon props, consistently with ballistic Metal. Inherit that exact
// material relationship; never assign health or alter the grenade simulation.
static class GrenadeBallisticSafety
{
 sealed class Provenance {internal int Material=-1,Matches;internal string Source="";internal float RetryAt;}
 static readonly Dictionary<(IntPtr,IntPtr),Provenance> materials=new();
 internal static long Repaired,Unresolved;
 internal static string LastSource="";

 static bool Valid(BallisticsSettings settings,int id)
 {
  if(!settings||settings.MaterialData==null||id<0||id>=settings.MaterialData.Count)return false;
  var material=settings.MaterialData[id];return material.RicochetPropability!=null&&material.impactObject;
 }
 static Provenance Resolve(ImpactMaterialAuthoring material,BallisticsSettings settings)
 {
  var key=(material.Pointer,settings.Pointer);
  if(materials.TryGetValue(key,out var cached)&&(cached.Material>=0||Time.unscaledTime<cached.RetryAt))return cached;
  var result=new Provenance();var invalid=false;var ids=new HashSet<int>();
  // One census per canonical Impact material/native settings pair, never per
  // grenade/frame. Runtime grenade repairs cannot become their own source.
  foreach(var source in Resources.FindObjectsOfTypeAll<DefaultBallisticObject>())
  {
   if(!source||source.GetComponent<Grenade>()||source.GetComponentInParent<Grenade>()||
    (Together.Current!=null&&Together.Current.RuntimeWorldBallisticSurface(source)))continue;
   var impact=source.GetComponent<ImpactObjectRigidbody>();
   if(!impact||!impact.Material||impact.Material.Pointer!=material.Pointer)continue;
   result.Matches++;ids.Add(source.MatID);
   if(!Valid(settings,source.MatID))invalid=true;
   if(result.Source.Length==0)result.Source=source.name;
  }
  if(!invalid&&ids.Count==1)result.Material=ids.Single();else result.RetryAt=Time.unscaledTime+5;
  if(materials.Count>=16)materials.Clear();materials[key]=result;return result;
 }
 internal static void Repair(Grenade actor)
 {
  if(!actor||!actor.gameObject.scene.IsValid())return;
  var scene=actor.gameObject.scene.name;if(scene!="URP_OfficeCorridor"&&scene!="Example_Scene_Night 1")return;
  var name=actor.name.Replace("(Clone)","").Trim();if(name!="grenade ENEMY"&&name!="grenade f1 1")return;
  if(actor.type!=Grenade.ExplosiveType.Timer||actor.GetComponent<BallisticObject>())return;
  var capsule=actor.GetComponent<CapsuleCollider>();var body=actor.GetComponent<Rigidbody>();var mesh=actor.transform.Find("Mesh (1)/f_1_main_low");
  if(!capsule||!body||capsule.isTrigger||capsule.attachedRigidbody!=body||body.transform!=actor.transform||
   Math.Abs(capsule.radius-99)>.0001f||Math.Abs(capsule.height-255)>.0001f||capsule.direction!=1||capsule.center.sqrMagnitude>.000001f||
   !mesh||!mesh.GetComponent<MeshFilter>()||!mesh.GetComponent<MeshFilter>().sharedMesh||!mesh.GetComponent<Renderer>())return;
  var impact=actor.GetComponent<ImpactObjectRigidbody>();var material=impact?impact.Material:null;
  if(!material||material.name!="TinyNail")return;
  var handler=BulletHandler.instance;var settings=handler?handler.Settings:null;if(!settings){Unresolved++;return;}
  var provenance=Resolve(material,settings);
  if(provenance.Material<0||!Valid(settings,provenance.Material)){Unresolved++;return;}
  // RaycastHit.transform is this attached Rigidbody root. The non-static native
  // route keeps its existing Rigidbody impulse; null LEC is already supported.
  var surface=actor.gameObject.AddComponent<DefaultBallisticObject>();surface.MatID=provenance.Material;surface.isStatic=false;
  Repaired++;LastSource=provenance.Source+"/"+settings.MaterialData[surface.MatID].Name+"/"+material.name;
  if(Repaired<=8)Plugin.Instance.Log.LogInfo("Repaired native F1 grenade ballistic surface: "+actor.name+", MatID="+surface.MatID+", authored source="+LastSource+", consistent sources="+provenance.Matches);
 }
}

[HarmonyPatch(typeof(Grenade),"Start")]
static class NativeF1GrenadeBallisticSurface
{
 // Repair even when the multiplayer Start prefix freezes a guest replica.
 [HarmonyPriority(Priority.First)]
 static void Prefix(Grenade __instance)=>GrenadeBallisticSafety.Repair(__instance);
}
