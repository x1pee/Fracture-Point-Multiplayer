using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace FractureTogether;

// Each point shadow renders six views. Keep nearby point shadows and preserve lighting itself.
static class LightShadowBudget
{
 sealed class State {public Light Light=null!;public LightShadows Original,Applied;public bool Changed;}
 static readonly Dictionary<int,State> states=new();static float rootCheckAt,refreshAt,updateAt;
 static readonly Stack<(Transform Node,int Child,bool Local)> nodes=new();static readonly HashSet<IntPtr> visited=new();
 static Il2CppArrayBase<GameObject>? roots;static Il2CppArrayBase<Light>? candidates;static int rootIndex,candidateIndex;
 static int sceneHandle=int.MinValue,revision=-1,rootCount=-1;static bool discoveryRequested=true;
 static Demo.Scripts.Runtime.Character.FPSController? localPlayer;
 public static int Kept,Limited;
 static void ResetDiscovery(){roots=null;candidates=null;rootIndex=candidateIndex=0;nodes.Clear();visited.Clear();discoveryRequested=true;}
 static void BeginDiscovery(Scene active){
  roots=active.GetRootGameObjects();rootIndex=0;candidates=null;candidateIndex=0;nodes.Clear();visited.Clear();rootCount=active.rootCount;discoveryRequested=false;refreshAt=Time.unscaledTime+10;
  // The native controller may live in DontDestroyOnLoad rather than this scene.
  if(localPlayer)nodes.Push((localPlayer!.transform,-1,true));
 }
 static void WalkDiscovery(){
  var walk=64;var register=8;
  while(roots!=null){
   if(candidates!=null){
    while(candidateIndex<candidates.Length&&register>0){
     var light=candidates[candidateIndex++];register--;
     if(!light||(light.type!=LightType.Point&&light.type!=LightType.Spot))continue;
     var id=light.GetInstanceID();if(!states.TryGetValue(id,out var saved)||!saved.Light)states[id]=new State{Light=light,Original=light.shadows,Applied=light.shadows};
    }
    if(candidateIndex<candidates.Length)break;candidates=null;candidateIndex=0;
   }
   if(walk--<=0)break;
   if(nodes.Count==0){
    if(rootIndex>=roots.Length){roots=null;visited.Clear();break;}
    var root=roots[rootIndex++];if(root&&root.scene.handle==sceneHandle)nodes.Push((root.transform,-1,false));continue;
   }
   var frame=nodes.Pop();var node=frame.Node;if(!node||!frame.Local&&node.gameObject.scene.handle!=sceneHandle)continue;
   if(frame.Child<0){
    if(!visited.Add(node.Pointer))continue;
    nodes.Push((node,0,frame.Local));candidates=node.GetComponents<Light>();
   }else if(frame.Child<node.childCount){
    // Bound child enumeration as well as component visits, including wide roots.
    nodes.Push((node,frame.Child+1,frame.Local));nodes.Push((node.GetChild(frame.Child),-1,frame.Local));
   }
  }
 }
 internal static void Tick(){
  if(Performance.Active==0){if(states.Count>0){Restore();states.Clear();}ResetDiscovery();localPlayer=null;sceneHandle=int.MinValue;revision=rootCount=-1;rootCheckAt=refreshAt=updateAt=0;Kept=Limited=0;return;}
  var active=SceneManager.GetActiveScene();var currentRevision=DungeonSeedPatch.Revision;
  if(active.handle!=sceneHandle||currentRevision!=revision){Restore();states.Clear();ResetDiscovery();sceneHandle=active.handle;revision=currentRevision;rootCount=-1;rootCheckAt=refreshAt=updateAt=0;}
  var currentPlayer=Together.Current?.LocalController;
  if((currentPlayer?currentPlayer!.Pointer:IntPtr.Zero)!=(localPlayer?localPlayer!.Pointer:IntPtr.Zero)){
   localPlayer=currentPlayer;discoveryRequested=true;if(localPlayer&&roots!=null)nodes.Push((localPlayer!.transform,-1,true));
  }
  if(Time.unscaledTime>=rootCheckAt){
   rootCheckAt=Time.unscaledTime+2;var count=active.rootCount;if(count!=rootCount){rootCount=count;discoveryRequested=true;}
   foreach(var key in states.Where(p=>!p.Value.Light).Select(p=>p.Key).ToArray())states.Remove(key);
  }
  if(Time.unscaledTime>=refreshAt)discoveryRequested=true;
  if(!DungeonSeedPatch.Building){if(roots==null&&discoveryRequested)BeginDiscovery(active);WalkDiscovery();}
  if(Time.unscaledTime<updateAt)return;updateAt=Time.unscaledTime+.5f;
  var camera=Camera.main;if(!camera)return;var position=camera.transform.position;var distance=Performance.Active==1?Performance.LocalShadowDistance.Value:Math.Min(Performance.LocalShadowDistance.Value,8);var budget=Performance.Active==1?Performance.PointShadows.Value:Math.Min(Performance.PointShadows.Value,2);var spotBudget=Performance.Active==1?Performance.SpotShadows.Value:Math.Min(Performance.SpotShadows.Value,4);
  foreach(var state in states.Values)if(state.Light){if(!state.Changed||state.Light.shadows!=state.Applied)state.Original=state.Light.shadows;}
  var keep=new HashSet<int>();
  foreach(var type in new[]{LightType.Point,LightType.Spot})foreach(var selected in states.Values.Where(s=>s.Light&&s.Light.gameObject.activeInHierarchy&&s.Light.enabled&&s.Light.type==type&&s.Original!=LightShadows.None&&Vector3.Distance(position,s.Light.transform.position)<=distance)
   .OrderBy(s=>Vector3.SqrMagnitude(position-s.Light.transform.position)).Take(type==LightType.Point?budget:spotBudget))keep.Add(selected.Light.GetInstanceID());
  if(localPlayer)foreach(var state in states.Values)if(state.Light&&state.Light.gameObject.activeInHierarchy&&state.Light.transform.IsChildOf(localPlayer!.transform)&&state.Original!=LightShadows.None)keep.Add(state.Light.GetInstanceID());
  Kept=keep.Count;Limited=0;
  foreach(var state in states.Values){if(!state.Light)continue;
   // Inactive descendants are known early, but budget only the objects the old
   // active-object census could affect. Restore a previously limited dormant light.
   if(!state.Light.gameObject.activeInHierarchy){if(state.Changed&&state.Light.shadows==state.Applied)state.Light.shadows=state.Original;state.Applied=state.Light.shadows;state.Changed=false;continue;}
   var desired=keep.Contains(state.Light.GetInstanceID())?state.Original:LightShadows.None;
   if(desired!=state.Original)Limited++;if(state.Light.shadows!=desired)state.Light.shadows=desired;state.Applied=desired;state.Changed=desired!=state.Original;}
 }
 static void Restore(){foreach(var state in states.Values)if(state.Changed&&state.Light){if(state.Light.shadows==state.Applied)state.Light.shadows=state.Original;state.Changed=false;}Kept=Limited=0;}
}
