namespace FractureTogether;

// Native-free bounds for an authored selector decision. No prefab path, asset ID,
// transform, RNG seed override or client-selected variant can be sent here.
public static class WorldVariantWire
{
 public const int MaxChoices=64,MaxSelectors=512;
 public static bool Hash(string? value){if(value==null||value.Length!=64)return false;foreach(var c in value)if(c is not (>= '0' and <= '9' or >= 'A' and <= 'F'))return false;return true;}
 public static bool Valid(Packet p){
  if(p.Kind is not ("variantreq" or "worldvariant"))return false;
  if(p.Seed<=0||p.Scene!="URP_OfficeCorridor"||p.Floor<0||p.VariantGeneration is <1 or >1000000||!Hash(p.World)||!Hash(p.ObjectId)||!Hash(p.MachineId)||p.Count is <1 or >MaxChoices||p.Sequence<=0||p.ItemId<0||p.ItemId>=p.Count||p.Pose is not {Length:0})return false;
  return p.Kind=="variantreq"?p.Direction==0&&p.ItemId==0&&!p.Success:p.Direction is -1 or 0 or 1;
 }
}
