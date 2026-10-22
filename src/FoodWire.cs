namespace FractureTogether;

// Purchases and consumption are distinct host-serialized transactions. Only
// approved spawn/pose messages carry coordinates; clients never select a prefab,
// a healing amount, an item count, or the player who receives a grant.
public static class FoodWire
{
 // Enable only when the release snapshot contains all five transport routes
 // and the FoodSync lifecycle/pickup hooks. Partial integration stays native.
 public const bool Integrated=true;
 public const int MaximumLive=16,MaximumTransactions=512,MaximumPrice=10000;
 static bool Vendor(string value)=>value!=null&&value.Length is >0 and <=128;
 static bool Food(string value)=>value!=null&&value.Length is >=6 and <=96&&value.StartsWith("food:",StringComparison.Ordinal);
 static bool Pose(float[] value){
  if(value==null||value.Length!=7)return false;
  for(var i=0;i<3;i++)if(!float.IsFinite(value[i])||Math.Abs(value[i])>1000000)return false;
  return UnitFrame.Rotation(value,3);
 }
 public static bool Valid(Packet p){
  if(p==null||p.Sequence<=0||p.Category is <0 or >1||p.ObjectId==null||p.MachineId==null||p.Pose==null||p.ItemId!=0||p.Amount!=0||p.TargetId is <-1 or >1000000)return false;
  switch(p.Kind){
   case "foodreq":return p.TargetId==-1&&p.Count==0&&p.MachineId.Length==0&&p.Pose.Length==0&&(p.Category==0?Vendor(p.ObjectId):Food(p.ObjectId));
   case "foodgrant":
    if(p.TargetId<0||p.Pose.Length!=0||p.Count<0||p.Count>MaximumPrice||p.Category==1&&p.Count!=0)return false;
    if(!p.Success)return p.Count==0&&(p.Category==0?Vendor(p.MachineId)&&(p.ObjectId.Length==0||Food(p.ObjectId)):Food(p.ObjectId)&&(p.MachineId.Length==0||Vendor(p.MachineId)));
    return Food(p.ObjectId)&&Vendor(p.MachineId);
   case "foodack":return p.TargetId==-1&&p.Count==0&&p.Pose.Length==0&&Food(p.ObjectId)&&Vendor(p.MachineId);
   case "foodstate":return p.TargetId>=0&&p.Count==0&&Food(p.ObjectId)&&Vendor(p.MachineId)&&(p.Category==0?Pose(p.Pose):p.Pose.Length==0);
   case "foodpose":return p.Category==0&&p.TargetId==-1&&p.Count==0&&Food(p.ObjectId)&&p.MachineId.Length==0&&Pose(p.Pose);
   default:return false;
  }
 }
}
