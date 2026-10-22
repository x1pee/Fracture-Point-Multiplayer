namespace FractureTogether;

public sealed class EnemyAnimationFrame
{
 public int[] States {get;set;}=Array.Empty<int>();
 public float[] Times {get;set;}=Array.Empty<float>();
 public float[] Weights {get;set;}=Array.Empty<float>();
 public int[] ParameterHashes {get;set;}=Array.Empty<int>();
 public float[] ParameterValues {get;set;}=Array.Empty<float>();
 public bool Valid()=>States!=null&&Times!=null&&Weights!=null&&ParameterHashes!=null&&ParameterValues!=null&&States.Length<=8&&Times.Length==States.Length&&Weights.Length==States.Length&&Times.All(x=>float.IsFinite(x)&&Math.Abs(x)<=1000000)&&Weights.All(x=>float.IsFinite(x)&&x>=0&&x<=1)&&ParameterHashes.Length<=64&&ParameterHashes.Length==ParameterValues.Length&&ParameterValues.All(x=>float.IsFinite(x)&&Math.Abs(x)<=1000000);
}

// Bone indices are the pinned prefab's PuppetMaster muscle order, never instance IDs.
public sealed class EnemyBodyFrame
{
 public int EnemyId {get;set;}
 public int Offset {get;set;}
 public float[] Pose {get;set;}=Array.Empty<float>();
 public bool Valid(){
  if(EnemyId is <1 or >1000000||Offset is <0 or >63||Pose==null||Pose.Length is <7 or >56||Pose.Length%7!=0||Offset+Pose.Length/7>64)return false;
  for(var i=0;i<Pose.Length;i+=7){for(var n=0;n<3;n++)if(!float.IsFinite(Pose[i+n])||Math.Abs(Pose[i+n])>1000000)return false;var length=0f;for(var n=3;n<7;n++){var v=Pose[i+n];if(!float.IsFinite(v)||Math.Abs(v)>1.001f)return false;length+=v*v;}if(length is <.99f or >1.01f)return false;}return true;
 }
}
