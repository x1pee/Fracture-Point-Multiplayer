namespace FractureTogether;

// Pure presentation limits. None of these values changes a local weapon,
// ballistic direction, native random state or received network state.
static class RemoteFireMotionPolicy
{
 internal const int CurveSteps=128;
 internal const float MaximumSeconds=2,MaximumRotation=90,MaximumTranslation=1;
 internal static float Bound(float value,float limit)=>float.IsFinite(value)&&float.IsFinite(limit)&&limit>0?Math.Clamp(value,-limit,limit):0;
 internal static bool Accept(long sequence,long previous,float now)=>sequence>0&&sequence>previous&&float.IsFinite(now)&&now>=0;
 internal static float Unit(long sequence,int weaponId,int lane){
  unchecked{
   var value=(uint)sequence^(uint)(sequence>>32)^((uint)weaponId*0x9E3779B9u)^((uint)(lane+1)*0x85EBCA6Bu);
   value^=value>>16;value*=0x7FEB352Du;value^=value>>15;value*=0x846CA68Bu;value^=value>>16;
   return (value>>8)*(1f/16777216f);
  }
 }
 internal static float Range(float first,float second,float unit,float limit){
  if(!float.IsFinite(first)||!float.IsFinite(second)||!float.IsFinite(unit))return 0;
  first=Bound(first,limit);second=Bound(second,limit);
  return Bound(first+(second-first)*Math.Clamp(unit,0,1),limit);
 }
 internal static bool TimeAt(float elapsed,float rate,float length,out float time){
  time=0;
  if(!float.IsFinite(elapsed)||elapsed<0||elapsed>=MaximumSeconds||!float.IsFinite(rate)||rate<=0||rate>20||!float.IsFinite(length)||length<=0||length>MaximumSeconds)return false;
  time=elapsed*rate;return float.IsFinite(time)&&time<length;
 }
 internal static float Curve(float[] values,int axis,float time,float length){
  if(axis<0||axis>=6||values.Length!=6*(CurveSteps+1)||!float.IsFinite(time)||time<0||!float.IsFinite(length)||length<=0||time>=length)return 0;
  var cursor=time/length*CurveSteps;var index=Math.Min(CurveSteps-1,(int)cursor);var blend=cursor-index;var offset=axis*(CurveSteps+1)+index;
  return Bound(values[offset]+(values[offset+1]-values[offset])*blend,4);
 }
}
