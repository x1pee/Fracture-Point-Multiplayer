using Demo.Scripts.Runtime.Character;
using UnityEngine.InputSystem;
namespace FractureTogether;

public sealed partial class Together
{
 FPSMovement? presentationMovement;PlayerInput? presentationInput;IntPtr presentationOwner;
 void PresentationTick(){
  LocalReloadCapture.Tick(player);
  RemoteShotAssets.Tick(player);
  if(!player){presentationMovement=null;presentationInput=null;presentationOwner=IntPtr.Zero;return;}
  if(presentationOwner==player!.Pointer&&presentationMovement)return;
  presentationOwner=player.Pointer;presentationMovement=player.GetComponent<FPSMovement>();presentationInput=player.GetComponent<PlayerInput>();
 }
 int CurrentStance=>presentationMovement?Math.Clamp((int)presentationMovement!.PoseState,0,2):-1;
 // Inventory/pause disables native input without necessarily replacing the
 // movement enum from the previous walking frame. Send the actual idle state.
 int CurrentMotion=>Down||PartyWiped||!player||!player!.enabled||!player.gameObject.activeInHierarchy||player.IsPauseMenuOpened||PanelInputBlocked||player.Health&&(player.Health.died||player.Health.Health<=0)||presentationInput&&(!presentationInput!.enabled||!presentationInput.inputIsActive)?0:presentationMovement?Math.Clamp((int)presentationMovement!.MovementState,0,4):-1;
 int CurrentAimState=>player?Math.Clamp((int)player!._aimState,0,3):0;
}
