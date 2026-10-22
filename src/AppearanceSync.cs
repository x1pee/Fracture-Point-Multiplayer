using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

// Clothing is reliable state, separate from disposable 20 Hz movement snapshots.
// A pose coalesced during loading must never erase a newly selected outfit.
public sealed partial class Together
{
 readonly AppearanceLedger appearances=new();
 OutfitEntry[]? sentAppearance;long appearanceSequence;float appearanceAt;
 void ResetAppearance(){appearances.Reset();sentAppearance=null;appearanceSequence=0;appearanceAt=0;}
 void SendAppearance(){
  if(!Connected||Network==null)return;
  var outfit=AvatarAppearance.Capture();if(outfit.Length==0)return;
  if(ReferenceEquals(outfit,sentAppearance)&&Time.unscaledTime<appearanceAt)return;
  Network.Send(new Packet{Kind="appearance",Sequence=++appearanceSequence,Outfit=outfit});
  sentAppearance=outfit;appearanceAt=Time.unscaledTime+5;
 }
 [HideFromIl2Cpp]
 bool ReceiveAppearance(Packet p){
  if(p.Kind=="joined"){
   sentAppearance=null;appearanceAt=0;
   if(Network?.Hosting==true)foreach(var entry in appearances.Snapshot())
    if(entry.PlayerId!=p.PlayerId)Network.Relay(new Packet{Kind="appearance",PlayerId=entry.PlayerId,Sequence=entry.Sequence,Outfit=entry.Outfit},target:p.PlayerId);
   return true;
  }
  if(p.Kind!="appearance")return false;
  if(!Connected||Network==null||p.PlayerId==Network.LocalId||!appearances.Apply(p.PlayerId,p.Sequence,p.Outfit))return true;
  if(!remotes.TryGetValue(p.PlayerId,out var avatar)){avatar=new RemoteAvatar(p.PlayerId);remotes[p.PlayerId]=avatar;}
  avatar.Appearance=appearances.Get(p.PlayerId);
  if(Network.Hosting)Network.Relay(p,p.PlayerId);
  return true;
 }
}
