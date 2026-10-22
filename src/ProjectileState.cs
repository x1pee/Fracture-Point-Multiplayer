namespace FractureTogether;

public sealed class ProjectileFrame
{
 public string Key {get;set;}="";
 public string Prefab {get;set;}="";
 public int Owner {get;set;}=-1;
 public long Request {get;set;}
 public float[] Pose {get;set;}=Array.Empty<float>();
 public float[] Velocity {get;set;}=Array.Empty<float>();
 public float[] Spin {get;set;}=Array.Empty<float>();
 public float Fuse {get;set;}
 public float DamageScale {get;set;}=1;
 public float InstantKill {get;set;}
 public bool Exploded {get;set;}
 public bool Valid()=>Key!=null&&Key.Length is >0 and <=64&&Prefab!=null&&Prefab.Length is >0 and <=128&&Owner>=-1&&Owner<=1000000&&Request>=0&&Pose!=null&&Pose.Length==7&&Pose.Take(3).All(x=>float.IsFinite(x)&&Math.Abs(x)<=1000000)&&UnitFrame.Rotation(Pose,3)&&Vector(Velocity,2500)&&Vector(Spin,2500)&&float.IsFinite(Fuse)&&Fuse>=0&&Fuse<=60&&float.IsFinite(DamageScale)&&DamageScale>=1&&DamageScale<=101&&float.IsFinite(InstantKill)&&InstantKill>=0&&InstantKill<=100;
 static bool Vector(float[]? values,float maximum)=>values!=null&&values.Length==3&&values.All(x=>float.IsFinite(x)&&Math.Abs(x)<=maximum);
}
public static class ProjectileRules
{
 // Human instruction: grenade friendly fire off; self damage and hostile damage stay native.
 public static bool HurtsPlayer(int owner,int victim)=>owner<0||owner==victim;
}
