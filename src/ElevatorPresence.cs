using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.SceneManagement;
using NumericVector=System.Numerics.Vector3;

namespace FractureTogether;

public sealed partial class Together
{
 // The shell mesh encloses the actual cabin. EnterCollider is only the narrow
 // doorway barrier and is toggled by native movement; it is not a presence box.
 // Resolve geometry only on a button/movement request, never in a frame tick.
 internal static bool LiftContains(LevelElevator selected,Vector3 bodyCenter){
  if(!selected||!selected.gameObject.scene.IsValid())return false;
  var shell=selected.transform.Find("Elevator");
  if(!shell||shell.parent!=selected.transform)return false;
  var collider=shell.GetComponent<MeshCollider>();var mesh=collider?collider.sharedMesh:null;
  if(!mesh||mesh.name!="Elevator")return false;
  var bounds=mesh.bounds;var point=shell.InverseTransformPoint(bodyCenter);var inverse=shell.worldToLocalMatrix;
  var norms=new NumericVector(
   RowNorm(inverse.m00,inverse.m01,inverse.m02),
   RowNorm(inverse.m10,inverse.m11,inverse.m12),
   RowNorm(inverse.m20,inverse.m21,inverse.m22));
  return ElevatorPresenceRules.ContainsLocal(ToNumeric(point),ToNumeric(bounds.center),ToNumeric(bounds.extents),norms);
 }
 static NumericVector ToNumeric(Vector3 p)=>new(p.x,p.y,p.z);
 static float RowNorm(float a,float b,float c)=>(float)Math.Sqrt((double)a*a+(double)b*b+(double)c*c);

 [HideFromIl2Cpp]
 bool SelectedElevatorReady(LevelElevator selected){
  if(!selected||!selected.enabled||!selected.gameObject.activeInHierarchy||selected.isMoving||
     selected.gameObject.scene!=SceneManager.GetActiveScene()||selected.gameObject.scene.name!=scene||!selected.controller){
   detail="Elevator is not ready in this world.";return false;
  }
  return true;
 }
 [HideFromIl2Cpp]
 bool LocalElevatorPresence(LevelElevator selected){
  if(!player||!player!.gameObject.activeInHierarchy||!player.Health||player.Health.died||player.Health.Health<=0||Down||Spectating||PartyWiped){
   detail="A surviving teammate must use the elevator.";return false;
  }
  var capsule=player.GetComponent<CharacterController>();
  if(!capsule||!LiftContains(selected,capsule.transform.TransformPoint(capsule.center))){
   detail="Enter this elevator before pressing its button.";return false;
  }
  return true;
 }
 internal static bool RemoteElevatorCenter(Packet state,out Vector3 center){
  center=default;var pose=state.Pose;
  // Pose[6] is the transmitted capsule center.Y. A six-element legacy pose
  // cannot prove body membership. The existing wire omits center.X/Z.
  if(pose==null||pose.Length!=7)return false;
  if(!float.IsFinite(pose[0])||!float.IsFinite(pose[1])||!float.IsFinite(pose[2])||!float.IsFinite(pose[6]))return false;
  center=new Vector3(pose[0],pose[1]+pose[6],pose[2]);
  return float.IsFinite(center.x)&&float.IsFinite(center.y)&&float.IsFinite(center.z);
 }
 [HideFromIl2Cpp]
 bool ElevatorPresenceReady(LevelElevator selected){
  if(!Connected||Network==null)return true;
  if(!RulesReady||DungeonSeedPatch.Building||PersonalSavePending||returningAfterDisconnect){
   detail="Waiting for the shared world transition to finish.";return false;
  }
  if(!SelectedElevatorReady(selected))return false;
  if(!Network.Hosting)return LocalElevatorPresence(selected);
  var peers=Network.ConnectedPeerIds;var roster=new int[peers.Length+1];roster[0]=Network.LocalId;Array.Copy(peers,0,roster,1,peers.Length);
  var members=new List<ElevatorPresenceRules.MemberSnapshot>(roster.Length);var cabin=selected.GetInstanceID();var now=Time.unscaledTime;
  var health=player?player!.Health:null;var localDown=Down||downPlayers.Contains(Network.LocalId);
  var localAlive=health&&!health!.died&&health.Health>0&&player!.gameObject.activeInHierarchy;
  var localDead=health&&health!.died;
  var localInside=false;
  if(localAlive){var capsule=player!.GetComponent<CharacterController>();localInside=capsule&&LiftContains(selected,capsule!.transform.TransformPoint(capsule.center));}
  members.Add(new(Network.LocalId,0,player&&player!.gameObject.scene==selected.gameObject.scene,localAlive,localDown,localDead,localInside?cabin:null));
  for(var i=0;i<peers.Length;i++){
   var id=peers[i];var down=downPlayers.Contains(id);
   if(!remotes.TryGetValue(id,out var peer)||peer.State==null){members.Add(new(id,float.PositiveInfinity,false,false,down,false,null));continue;}
   var state=peer.State;var age=now-peer.LastSeen;var matches=state.Scene==scene&&state.Floor==floor&&state.World==world;
   var fresh=float.IsFinite(age)&&age>=0&&age<=ElevatorPresenceRules.MaximumStateAge;
   // An explicit Down survives stale poses. A contradictory fresh living pose
   // waits for the next state rather than silently excluding a living outsider.
   var alive=state.Health>0&&(!down||fresh&&matches);
   // State packets contain HP, not an authoritative native died flag. Zero HP
   // alone may be a transient/unknown state; wait for the host's Down fact.
   const bool dead=false;
   var inside=alive&&matches&&fresh&&RemoteElevatorCenter(state,out var center)&&LiftContains(selected,center);
   members.Add(new(id,age,matches,alive,down,dead,inside?cabin:null));
  }
  var result=ElevatorPresenceRules.Evaluate(Network.PlayerCount,roster,members,cabin,PartyWiped);
  if(result.Allowed){
   // Network membership changes on a transport thread. Recheck exact IDs after
   // geometry work so a same-count leave/join cannot reuse the departed pose.
   var current=Network.ConnectedPeerIds;var unchanged=current.Length==peers.Length&&Network.PlayerCount==roster.Length;
   for(var i=0;unchanged&&i<current.Length;i++)unchanged=current[i]==peers[i];
   if(unchanged)return true;
   detail="Waiting for the updated teammate list.";return false;
  }
  detail=result.Failure==ElevatorPresenceRules.PresenceFailure.OutsideSelectedCabin?
   "All surviving teammates must enter this elevator.":"Waiting for every teammate's current elevator state.";
  return false;
 }
}
