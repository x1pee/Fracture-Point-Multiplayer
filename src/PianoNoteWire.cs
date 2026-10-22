namespace FractureTogether;

// Reliable, transient note events. Audio clips, pitch and volume stay local to
// the authored piano; no audio samples or historical song snapshots go on wire.
public static class PianoNoteWire
{
 public const int MaximumNote=127;
 public static bool Valid(Packet p){
  if(p.Kind is not ("pianoreq" or "pianonote")||p.Sequence<=0||p.ItemId<0||p.ItemId>MaximumNote||p.ObjectId==null||p.ObjectId.Length is <1 or >256||p.Pose==null||p.Pose.Length!=3)return false;
  for(var i=0;i<3;i++)if(!float.IsFinite(p.Pose[i])||Math.Abs(p.Pose[i])>1000000)return false;
  return p.Kind=="pianoreq"?p.TargetId==-1:p.TargetId is >=0 and <=1000000;
 }
}
