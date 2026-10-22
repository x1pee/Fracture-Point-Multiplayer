namespace FractureTogether;

// Host-only cosmetic emission, never an instruction to create a bullet/damage.
public sealed class EnemyShotFrame
{
 public int EnemyId {get;set;}
 public float[] Ray {get;set;}=Array.Empty<float>();
 public bool Valid(){
  if(EnemyId is <1 or >1000000||Ray==null||Ray.Length!=6)return false;
  for(var i=0;i<Ray.Length;i++)if(!float.IsFinite(Ray[i])||Math.Abs(Ray[i])>1000000)return false;
  var x=Ray[3]-Ray[0];var y=Ray[4]-Ray[1];var z=Ray[5]-Ray[2];var square=x*x+y*y+z*z;
  return square>.0001f&&square<=300*300;
 }
}
