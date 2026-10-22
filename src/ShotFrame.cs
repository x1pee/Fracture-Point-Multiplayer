namespace FractureTogether;

// Ephemeral cosmetic event. Coordinates describe a ray already clipped by the
// real shooter's weapon mask; this DTO never requests a bullet or damage.
public sealed class ShotFrame
{
 public int WeaponId {get;set;}=-1;
 public float[] Ray {get;set;}=Array.Empty<float>();
 public bool Valid(){
  if(WeaponId<0||WeaponId>100000||Ray==null||Ray.Length!=6)return false;
  foreach(var value in Ray)if(!float.IsFinite(value)||Math.Abs(value)>1000000)return false;
  var dx=Ray[3]-Ray[0];var dy=Ray[4]-Ray[1];var dz=Ray[5]-Ray[2];var distance=dx*dx+dy*dy+dz*dz;
  return distance>.0001f&&distance<=250*250;
 }
}
