using AEG.FSR;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

static class NativeImage
{
 sealed class State {public FSR3_BASE Fsr=null!;public FSR_Quality Original;public bool Modified;}
 static readonly Dictionary<int,State> states=new();static float scanAt;
 internal static void Tick(){
  if(!Performance.NativeResolution.Value){foreach(var state in states.Values)if(state.Modified&&state.Fsr){if(state.Fsr.FSRQuality==FSR_Quality.Off)state.Fsr.OnSetQuality(state.Original);state.Modified=false;}return;}
  if(Time.unscaledTime<scanAt)return;scanAt=Time.unscaledTime+1;
  foreach(var fsr in Object.FindObjectsOfType<FSR3_BASE>()){
   if(!states.TryGetValue(fsr.GetInstanceID(),out var state)){state=new(){Fsr=fsr,Original=fsr.FSRQuality};states[fsr.GetInstanceID()]=state;}
   if(!state.Modified||fsr.FSRQuality!=FSR_Quality.Off)state.Original=fsr.FSRQuality;
   if(fsr.FSRQuality!=FSR_Quality.Off)fsr.OnSetQuality(FSR_Quality.Off);state.Modified=true;
  }
 }
}
