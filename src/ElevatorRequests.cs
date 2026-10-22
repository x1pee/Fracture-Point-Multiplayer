using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

public sealed partial class Together
{
 bool executingRemoteButton;long buttonSequence;readonly PeerTimeline buttonRequests=new();
 readonly Dictionary<IntPtr,(LevelElevator Lift,string World,int Revision,string Scene,int NativeFloor)> departureOrigins=new();
 static string LiftKey(LevelElevator lift){var p=lift.transform.position;return FormattableString.Invariant($"lift|{Quantize(p.x,100)},{Quantize(p.y,100)},{Quantize(p.z,100)}");}
 static bool GeneratedDeparture(LevelElevator lift){for(var p=lift.transform.parent;p;p=p.parent)if(p.name=="GeneratedLevel")return true;return false;}
 [HideFromIl2Cpp]
 internal void CaptureDepartureOrigin(LevelElevator lift){
  // Native movement later detaches the End-module cabin. This entry callback
  // runs early enough to preserve its generation provenance before movement.
  if(SharedRun&&lift&&GeneratedDeparture(lift)){departureOrigins[lift.Pointer]=(lift,world,DungeonSeedPatch.Revision,lift.gameObject.scene.name,DungeonSeedPatch.BuiltFloor>=0?DungeonSeedPatch.BuiltFloor:floor);DiscoverLootSubtree(lift.transform);}
 }
 [HideFromIl2Cpp]
 bool ElevatorDepartureReady(LevelElevator selected)=>ElevatorPresenceReady(selected);
 [HideFromIl2Cpp]
 bool ElevatorTransactionsReady(LevelElevator selected)=>ElevatorPresenceReady(selected);
 [HideFromIl2Cpp]
 public bool ElevatorButtonRequest(ElevatorButton button){
  if(Replay||executingRemoteButton||!Connected)return true;
  if(Down||PartyWiped){detail="A surviving teammate must use the elevator.";return false;}

  if(!button.elevator||!button.elevator.enabled||!button.elevator.controller){detail="Enter the elevator through its entry trigger first.";return false;}
  // Refuse BEFORE native Interact consumes its one-way activated latch.
  if(!ElevatorTransactionsReady(button.elevator))return false;
  if(Network!.Hosting)return true;
  var request=Message("buttonreq");request.ObjectId=SpatialKey(button.transform);request.Sequence=++buttonSequence;Network.Send(request);return false;
 }
 [HideFromIl2Cpp]
 bool ReceiveElevatorRequest(Packet p){
  if(p.Kind!="buttonreq"||Network?.Hosting!=true)return false;
  if(!PacketWorld(p)||!remotes.TryGetValue(p.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0||downPlayers.Contains(p.PlayerId)||!buttonRequests.Accept(p.PlayerId,p.Sequence))return true;
  var button=Object.FindObjectsOfType<ElevatorButton>().FirstOrDefault(b=>SpatialKey(b.transform)==p.ObjectId);
  if(!button||!button.elevator||!ElevatorTransactionsReady(button.elevator))return true;
  executingRemoteButton=true;try{button.Interact();}finally{executingRemoteButton=false;}return true;
 }
}
[HarmonyPatch(typeof(ElevatorButton),nameof(ElevatorButton.Interact))]
static class SharedElevatorButton {static bool Prefix(ElevatorButton __instance)=>Together.Current?.ElevatorButtonRequest(__instance)??true;}
[HarmonyPatch(typeof(LevelElevator),nameof(LevelElevator.PlayStartupAnim))]
static class RememberNativeDeparture {static void Prefix(LevelElevator __instance)=>Together.Current?.CaptureDepartureOrigin(__instance);}
