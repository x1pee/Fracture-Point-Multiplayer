using BepInEx.Configuration;
using HarmonyLib;
using ShinySSRR;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
namespace FractureTogether;

// Runtime-only changes; no persistent graphics settings or resolution writes.
public static class Performance
{
 public static ConfigEntry<int> Mode=null!;
 public static ConfigEntry<bool> NativeResolution=null!;
 public static ConfigEntry<bool> LocalizationScans=null!;
 public static ConfigEntry<float> ShadowDistance=null!,LocalShadowDistance=null!;
 public static ConfigEntry<int> PointShadows=null!,SpotShadows=null!;
 public static int Active=>Mode.Value;
 static UniversalRenderPipelineAsset? asset;static float distance,appliedDistance;static int mainResolution,additionalResolution,cascades,appliedMain,appliedAdditional,appliedCascades;static bool captured;
 public static void Initialize(){Mode=Plugin.Instance.Config.Bind("Performance","Mode",1,new ConfigDescription("Default Balanced. F10 cycles 0 original / 1 balanced nearby shadows / 2 fewer shadows and no SSR. Hitch fixes stay active in Original. Shadow presets can help GPU-limited scenes but are not guaranteed to increase FPS. Texture resolution is unchanged.",new AcceptableValueRange<int>(0,2)));NativeResolution=Plugin.Instance.Config.Bind("Performance","NativeResolution",false,"Sharper image in the Graphics menu turns off FSR. Sharper image; GPU cost can increase. Previous FSR quality is restored when disabled.");
  LocalizationScans=Plugin.Instance.Config.Bind("Performance","LimitLocalizationDiscovery",true,"Bound passive full-scene localization discovery in all graphics presets and solo. Forced language/scene refreshes and native text-change callbacks stay immediate. Disable to restore the game's discovery cadence.");
  ShadowDistance=Plugin.Instance.Config.Bind("Performance","ShadowDistance",45f,new ConfigDescription("Maximum shadow distance in optimized modes.",new AcceptableValueRange<float>(10,100)));
  LocalShadowDistance=Plugin.Instance.Config.Bind("Performance","LocalShadowDistance",12f,new ConfigDescription("Keep point/spot shadows near the camera; local player flashlight remains protected.",new AcceptableValueRange<float>(4,30)));
  PointShadows=Plugin.Instance.Config.Bind("Performance","PointShadowCount",4,new ConfigDescription("Maximum nearby point lights with shadows; lighting stays enabled.",new AcceptableValueRange<int>(0,8)));
  SpotShadows=Plugin.Instance.Config.Bind("Performance","SpotShadowCount",8,new ConfigDescription("Maximum nearby spotlights with shadows.",new AcceptableValueRange<int>(0,16)));
 }
 public static void Tick(){
  IncrementalLocalization.Tick();
  if(!Together.MenuHotkeysBlocked&&InputBridge.Active&&!InputBridge.ModifiersHeld&&InputBridge.Down(KeyCode.F10)){Mode.Value=(Mode.Value+1)%3;Plugin.Instance.Log.LogInfo("Performance mode "+Mode.Value);}
  NativeImage.Tick();
  LightShadowBudget.Tick();
  if(Active==0){Restore();return;}
  var current=GraphicsSettings.currentRenderPipeline?.TryCast<UniversalRenderPipelineAsset>();if(!current)return;
  if(asset!=current){Restore();asset=current;}
  if(!captured){asset=current;distance=asset.shadowDistance;mainResolution=asset.mainLightShadowmapResolution;additionalResolution=asset.additionalLightsShadowmapResolution;cascades=asset.shadowCascadeCount;captured=true;}
  else {
   if(asset!.shadowDistance!=appliedDistance)distance=asset.shadowDistance;
   if(asset.mainLightShadowmapResolution!=appliedMain)mainResolution=asset.mainLightShadowmapResolution;
   if(asset.additionalLightsShadowmapResolution!=appliedAdditional)additionalResolution=asset.additionalLightsShadowmapResolution;
   if(asset.shadowCascadeCount!=appliedCascades)cascades=asset.shadowCascadeCount;
  }
  appliedDistance=Math.Min(distance,Active==1?ShadowDistance.Value:Math.Min(ShadowDistance.Value,30));appliedMain=Math.Min(mainResolution,2048);appliedAdditional=Math.Min(additionalResolution,1024);appliedCascades=Math.Min(cascades,2);
  if(asset!.shadowDistance!=appliedDistance)asset.shadowDistance=appliedDistance;
  if(asset.mainLightShadowmapResolution!=appliedMain)asset.mainLightShadowmapResolution=appliedMain;
  if(asset.additionalLightsShadowmapResolution!=appliedAdditional)asset.additionalLightsShadowmapResolution=appliedAdditional;
  if(asset.shadowCascadeCount!=appliedCascades)asset.shadowCascadeCount=appliedCascades;
 }
 static void Restore(){if(captured&&asset){asset!.shadowDistance=distance;asset.mainLightShadowmapResolution=mainResolution;asset.additionalLightsShadowmapResolution=additionalResolution;asset.shadowCascadeCount=cascades;}captured=false;}
}

[HarmonyPatch(typeof(ShinyScreenSpaceRaytracedReflections),nameof(ShinyScreenSpaceRaytracedReflections.IsActive))]
static class ReflectionBudgetPatch
{
 sealed class Original {public int Samples,Binary,Downsample,AppliedSamples,AppliedBinary,AppliedDownsample;public bool Backfaces,AppliedBackfaces,Modified;}
 static readonly Dictionary<IntPtr,Original> originals=new();
 static void Prefix(ShinyScreenSpaceRaytracedReflections __instance){
  if(!originals.TryGetValue(__instance.Pointer,out var saved)){saved=new(){Samples=__instance.sampleCount.value,Binary=__instance.binarySearchIterations.value,Downsample=__instance.downsampling.value,Backfaces=__instance.computeBackFaces.value};originals[__instance.Pointer]=saved;}
  var balance=Performance.Active>0;
  if(saved.Modified){
   if(__instance.sampleCount.value!=saved.AppliedSamples)saved.Samples=__instance.sampleCount.value;
   if(__instance.binarySearchIterations.value!=saved.AppliedBinary)saved.Binary=__instance.binarySearchIterations.value;
   if(__instance.downsampling.value!=saved.AppliedDownsample)saved.Downsample=__instance.downsampling.value;
   if(__instance.computeBackFaces.value!=saved.AppliedBackfaces)saved.Backfaces=__instance.computeBackFaces.value;
  }else{
   saved.Samples=__instance.sampleCount.value;saved.Binary=__instance.binarySearchIterations.value;saved.Downsample=__instance.downsampling.value;saved.Backfaces=__instance.computeBackFaces.value;
  }
  if(!balance&&!saved.Modified)return;
  __instance.sampleCount.value=balance?Math.Min(saved.Samples,24):saved.Samples;
  __instance.binarySearchIterations.value=balance?Math.Min(saved.Binary,4):saved.Binary;
  __instance.downsampling.value=balance?Math.Max(saved.Downsample,2):saved.Downsample;
  __instance.computeBackFaces.value=balance?false:saved.Backfaces;
  saved.AppliedSamples=__instance.sampleCount.value;saved.AppliedBinary=__instance.binarySearchIterations.value;saved.AppliedDownsample=__instance.downsampling.value;saved.AppliedBackfaces=__instance.computeBackFaces.value;saved.Modified=balance;
 }
 static void Postfix(ref bool __result){if(Performance.Active==2)__result=false;}
}
