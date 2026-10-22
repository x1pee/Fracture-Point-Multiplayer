using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;
public sealed partial class Together
{
 bool returningAfterDisconnect,quitting;
 internal bool Recovering=>returningAfterDisconnect;
 UnityEngine.InputSystem.PlayerInput? recoveryInput;bool restoreRecoveryInput;
 void CancelDisconnectRecovery(){
  returningAfterDisconnect=false;
  ReleasePersonalInput(recoveryInput,restoreRecoveryInput);
  recoveryInput=null;restoreRecoveryInput=false;
 }
 // Transfer the restore obligation to whichever UI/death state currently owns input.
 void ReleasePersonalInput(UnityEngine.InputSystem.PlayerInput? input,bool restore){
  if(!restore||!input||!player||quitting||PartyWiped||PlayingNativeDeath)return;
  var current=player.GetComponent<UnityEngine.InputSystem.PlayerInput>();if(!current||current.Pointer!=input.Pointer)return;
  if(Down){deathInput=input;restoreDeathInput=true;input.DeactivateInput();return;}
  if(panel){if(!pausedInput)pausedInput=input;input.DeactivateInput();return;}
  input.ActivateInput();
 }
 void BeginDisconnectRecovery(){
  if(quitting||returningAfterDisconnect||!player||PartyWiped)return;
  returningAfterDisconnect=true;
  recoveryInput=player.GetComponent<UnityEngine.InputSystem.PlayerInput>();restoreRecoveryInput=recoveryInput&&recoveryInput.inputIsActive;
  if(recoveryInput)recoveryInput.DeactivateInput();player._playerInput=Vector2.zero;
  detail="Connection closed. Saving your own inventory before returning home.";
 }
 void DisconnectRecoveryTick(){
  if(!returningAfterDisconnect||PersonalSavePending)return;
  if(PartyWiped||quitting){CancelDisconnectRecovery();return;}
  var loader=Object.FindObjectOfType<SceneLoadingManager>();if(!loader||loader._isLoading)return;
  // The shared barrier resumes the exact replay request only after disk readback.
  if(PersonalSceneReady(2,loader,true)){
   CancelDisconnectRecovery();var previous=Replay;Replay=true;try{loader.LoadScene(2);}finally{Replay=previous;}
  }
 }
}
