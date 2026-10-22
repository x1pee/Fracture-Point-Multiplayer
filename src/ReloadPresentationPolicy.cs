namespace FractureTogether;

// Pure visual snapshot policy. A stopped generation cannot restart from an
// older/coalesced packet; continuing snapshots do not restart the clip clock.
public enum ReloadChange { None, Started, Stopped }
public sealed class ReloadPresentationPolicy
{
 public const float MaxElapsed=30;
 long sequence=-1;int weapon=-1,kind;float anchorAt,anchorElapsed,lastReported;bool terminal;
 public bool Active=>kind!=0;
 public int Kind=>kind;
 public long Sequence=>sequence;
 public float Elapsed(float now)=>Active&&float.IsFinite(now)?Math.Clamp(anchorElapsed+Math.Max(0,now-anchorAt),0,MaxElapsed):0;
 public static bool Valid(long sequence,int kind,float elapsed,int weaponId)=>sequence>=0&&kind is >=0 and <=2&&float.IsFinite(elapsed)&&elapsed>=0&&elapsed<=MaxElapsed&&(kind==0?elapsed==0:sequence>0&&weaponId>=0&&weaponId<=100000);
 // Durations are the actual FPSAnimationAsset.blendTime fields. This is a
 // continuous visual envelope, not a claim to reproduce its playable mixer.
 public static float BlendWeight(float elapsed,float remaining,float blendIn,float blendOut){
  if(!float.IsFinite(elapsed)||!float.IsFinite(remaining)||!float.IsFinite(blendIn)||!float.IsFinite(blendOut)||elapsed<0||remaining<0||blendIn<0||blendOut<0)return 0;
  return Math.Min(blendIn==0?1:Math.Clamp(elapsed/blendIn,0,1),blendOut==0?1:Math.Clamp(remaining/blendOut,0,1));
 }
 public ReloadChange Observe(long incomingSequence,int incomingKind,float incomingElapsed,int incomingWeapon,float now){
  if(!Valid(incomingSequence,incomingKind,incomingElapsed,incomingWeapon)||!float.IsFinite(now)||now<0||incomingSequence<sequence)return ReloadChange.None;
  var wasActive=Active;
  if(incomingSequence==sequence){
   if(incomingWeapon!=weapon){kind=0;terminal=true;return wasActive?ReloadChange.Stopped:ReloadChange.None;}
   if(incomingKind==0){kind=0;terminal=true;return wasActive?ReloadChange.Stopped:ReloadChange.None;}
   if(terminal||incomingKind!=kind||incomingElapsed<=lastReported)return ReloadChange.None;
   var current=Elapsed(now);lastReported=incomingElapsed;anchorElapsed=Math.Max(current,incomingElapsed);anchorAt=now;return ReloadChange.None;
  }
  sequence=incomingSequence;weapon=incomingWeapon;kind=incomingKind;terminal=incomingKind==0;anchorAt=now;anchorElapsed=lastReported=incomingElapsed;
  return Active?ReloadChange.Started:wasActive?ReloadChange.Stopped:ReloadChange.None;
 }
 public void Reset(){sequence=-1;weapon=-1;kind=0;anchorAt=anchorElapsed=lastReported=0;terminal=false;}
 public bool Cancel(){var active=Active;kind=0;terminal=true;return active;}
 // ThrowSequence is history, whereas Kind describes the CURRENT action.
 // Consume an old unseen throw without replay when the same snapshot already
 // reports an accepted later reload. A real subsequent throw publishes Kind0.
 public bool TakeThrow(long incoming,ref long observed){if(incoming<=observed)return false;observed=incoming;return !Active;}
}
