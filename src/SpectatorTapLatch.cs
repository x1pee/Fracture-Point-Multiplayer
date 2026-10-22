namespace FractureTogether;

// One accepted press stays consumed while Win32/Unity report that same edge
// in adjacent frames. The caller owns focus/modal and initial-release guards.
public sealed class SpectatorTapLatch
{
 bool blocked;
 int consumedFrame;
 public bool Take(bool down,bool held,int frame)
 {
  if(blocked)
  {
   // A release must be observed beyond both possible delivery frames. Never
   // treat the rearming sample itself as another press, even if down is set.
   if((long)frame>(long)consumedFrame+1&&!held)blocked=false;
   return false;
  }
  if(!down)return false;
  blocked=true;consumedFrame=frame;return true;
 }
 public void Reset(){blocked=false;consumedFrame=0;}
}
