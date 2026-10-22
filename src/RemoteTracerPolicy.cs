namespace FractureTogether;

// Straight cosmetic motion only. Parameters come from the cached authored
// firearm/trail; no projectile, collision, damage or per-frame asset lookup.
internal static class RemoteTracerPolicy
{
 internal const float MaxSeconds=5,MaxDistance=300;
 internal static bool ValidAsset(float speed,float startWidth,float endWidth,float lifetime)=>
  float.IsFinite(speed)&&speed>0&&speed<=100000&&float.IsFinite(startWidth)&&startWidth>0&&startWidth<=1&&
  float.IsFinite(endWidth)&&endWidth>=0&&endWidth<=1&&float.IsFinite(lifetime)&&lifetime>0&&lifetime<=MaxSeconds;
 internal static bool Duration(float distance,float speed,float lifetime,out float seconds){
  seconds=0;
  if(!float.IsFinite(distance)||distance<=.0001f||distance>MaxDistance||!float.IsFinite(speed)||speed<=0||speed>100000||!float.IsFinite(lifetime)||lifetime<=0||lifetime>MaxSeconds)return false;
  var value=distance/speed+lifetime;if(!float.IsFinite(value)||value>MaxSeconds)return false;seconds=value;return true;
 }
 internal static bool Sample(float elapsed,float distance,float speed,float lifetime,out float head,out float tail){
  head=tail=0;
  if(!float.IsFinite(elapsed)||elapsed<0||!Duration(distance,speed,lifetime,out var duration)||elapsed>=duration)return false;
  head=Math.Min(speed*elapsed,distance);tail=Math.Min(speed*Math.Max(0,elapsed-lifetime),distance);return true;
 }
}
