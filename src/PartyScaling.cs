using DungeonArchitect.Grammar;
using DungeonArchitect.RuntimeGraphs;
using HarmonyLib;
using UnityEngine;

namespace FractureTogether;

public static class PartyScaling
{
 public static bool Building;
 public static double AppliedLength=1;
 static readonly HashSet<IntPtr> scaled=new();
 static double enemyRemainder;
 public static void Begin(){Building=true;scaled.Clear();AppliedLength=1;enemyRemainder=0;PartySpawnPlacement.Reset();}
 public static void End()=>Building=false;
 public static int SpawnCount(EnemySpawner spawner,int basis){
  var current=Together.Current;if(current?.Connected!=true||current.Network?.Hosting!=true||!current.SharedRun||current.RunPlayers<=1||basis<=0)return basis;
  var factor=current.Rules.Enemies(current.RunPlayers);
  // This is a per-native-batch density multiplier. More rooms can add further
  // native batches; dividing here previously diluted the requested density.
  var exact=basis*factor+enemyRemainder;var result=(int)Math.Floor(exact+1e-6);enemyRemainder=exact-result;
  return Math.Clamp(result,0,1000);
 }
 public static void Grow(GraphGrammarProcessor processor){
  var current=Together.Current;if(!Building||current?.Connected!=true||current.RunPlayers<=1)return;
  var graph=processor.Grammar.ResultGraph;if(graph==null||!scaled.Add(graph.Pointer))return;
  // SnapBuilder grows the first root's connected component; grammar also leaves an unused Insert node.
  var reachable=new HashSet<IntPtr>();var originalNodes=new List<RuntimeGraphNode<GrammarRuntimeGraphNodeData>>();
  void Visit(RuntimeGraphNode<GrammarRuntimeGraphNodeData> node){if(!reachable.Add(node.Pointer))return;originalNodes.Add(node);foreach(var next in node.Outgoing)Visit(next);}
  var root=graph.Nodes.ToArray().FirstOrDefault(n=>n.Incoming.Count==0);if(root==null)throw new InvalidOperationException("Dungeon graph has no root");Visit(root);
  var original=originalNodes.ToArray();var basis=original.Length;
  // Boss arenas use a fixed approach/arena connector layout. Repeating their
  // single approach node can make every native layout attempt fail.
  if(original.Any(n=>n.Payload.nodeType?.nodeName.Contains("Boss",StringComparison.OrdinalIgnoreCase)==true)){
   Plugin.Instance.Log.LogInfo("Fixed boss arena: preserving native room layout; Victor HP scales with party size.");return;
  }
  // Trim the prior co-op target by one third, retaining every native node and
  // its connector categories. Solo and fixed boss layouts are untouched.
  var target=Math.Max(basis,checked((int)Math.Round(basis*current.Rules.Length(current.RunPlayers)*.67,MidpointRounding.AwayFromZero)));
  if(target==basis){AppliedLength=1;Plugin.Instance.Log.LogInfo($"Party floor graph: players={current.RunPlayers}, modules={basis}->{target}, preserving native minimum, enemy batch density={current.Rules.Enemies(current.RunPlayers):0.##}");GameplayLog.Record("dungeon_graph_scaled",new{players=current.RunPlayers,basis,target,cooperativeRoomScale=.67,requestedLength=current.Rules.Length(current.RunPlayers),actualLength=AppliedLength,enemyBatchDensity=current.Rules.Enemies(current.RunPlayers),lootFactor=current.LootFactor});return;}
  var template=original.Where(n=>n.Incoming.Count==1&&n.Outgoing.Count==1&&n.Payload?.nodeType!=null)
   .Where(n=>!Reserved(n.Payload.nodeType.nodeName))
   .OrderByDescending(n=>n.Payload.nodeType.nodeName.Contains("Corridor",StringComparison.OrdinalIgnoreCase))
   .ThenByDescending(n=>n.Payload.nodeType.nodeName.Contains("Straight",StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
  if(template==null)throw new InvalidOperationException("No two-connector room template; refusing to stretch floor geometry");
  // Reception and elevator use a special connector category and must stay directly adjacent.
  var edges=original.Where(a=>!Reserved(a.Payload.nodeType.nodeName)).SelectMany(a=>a.Outgoing.ToArray().Where(b=>!Reserved(b.Payload.nodeType.nodeName)).Select(b=>(From:a,To:b))).ToArray();
  if(edges.Length==0)throw new InvalidOperationException("Dungeon graph has no connected rooms");
  var maxIndex=original.Max(n=>n.Payload.index);
  var tails=new Dictionary<int,RuntimeGraphNode<GrammarRuntimeGraphNodeData>>();
  for(var i=0;i<target-basis;i++){
   var edgeIndex=i%edges.Length;var edge=edges[edgeIndex];var from=tails.GetValueOrDefault(edgeIndex)??edge.From;
   var node=new RuntimeGraphNode<GrammarRuntimeGraphNodeData>(graph){Payload=new GrammarRuntimeGraphNodeData{nodeType=template.Payload.nodeType,index=++maxIndex},Position=(from.Position+edge.To.Position)*.5f};
   from.BreakLinkTo(edge.To);from.MakeLinkTo(node);node.MakeLinkTo(edge.To);
   // The runtime node constructor registers itself in its graph.
   if(!graph.Nodes.Contains(node))graph.Nodes.Add(node);
   tails[edgeIndex]=node;
  }
  AppliedLength=(double)target/basis;
  Plugin.Instance.Log.LogInfo($"Party floor graph: players={current.RunPlayers}, modules={basis}->{target}, template={template.Payload.nodeType.nodeName}, co-op trim=0.67, enemy batch density={current.Rules.Enemies(current.RunPlayers):0.##}");
  GameplayLog.Record("dungeon_graph_scaled",new{players=current.RunPlayers,basis,target,cooperativeRoomScale=.67,requestedLength=current.Rules.Length(current.RunPlayers),actualLength=AppliedLength,enemyBatchDensity=current.Rules.Enemies(current.RunPlayers),lootFactor=current.LootFactor});
 }
 static bool Reserved(string name)=>name is "Insert" or "End" or "ElevatorSpace" or "Reception";
}
[HarmonyPatch(typeof(GraphGrammarProcessor),nameof(GraphGrammarProcessor.Build))]
static class GrowPartyFloorPatch {static void Postfix(GraphGrammarProcessor __instance)=>PartyScaling.Grow(__instance);}
[HarmonyPatch(typeof(EnemySpawner),nameof(EnemySpawner.Spawn))]
static class PartyEnemyCountPatch
{
 [HarmonyPriority(Priority.Last)]
 static bool Prefix(EnemySpawner __instance,ref int enemiesCount,Transform? __2,out PartySpawnPlacement.Batch? __state){
  __state=null;
  var before=enemiesCount;enemiesCount=PartyScaling.SpawnCount(__instance,before);
  var current=Together.Current;
  if(current?.Connected==true&&current.Network?.Hosting==true&&current.SharedRun&&current.RunPlayers>1&&before>0)__state=PartySpawnPlacement.Before(__instance,before,enemiesCount,__2);
  if(before!=enemiesCount)Plugin.Instance.Log.LogInfo($"Party enemy batch: {before}->{enemiesCount}, perRoom={__instance.SpawnOnStart}");
  return enemiesCount>0;
 }
 static Exception? Finalizer(EnemySpawner __instance,PartySpawnPlacement.Batch? __state,Exception? __exception){try{if(__state!=null)PartySpawnPlacement.After(__instance,__state,__exception);}catch(Exception error){GameplayLog.Fault("party_spawn_placement",error);}return __exception;}
}

