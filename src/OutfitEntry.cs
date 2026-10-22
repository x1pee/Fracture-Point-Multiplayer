namespace FractureTogether;

// Plain wire data: this file is also linked into the transport-only test project.
public sealed class OutfitEntry
{
 public string Name {get;set;}="";
 public bool Enabled {get;set;}
 public static bool Validate(OutfitEntry[]? outfit){
  if(outfit==null||outfit.Length>128)return false;
  var names=new HashSet<string>(StringComparer.Ordinal);
  foreach(var item in outfit)if(item==null||item.Name==null||item.Name.Length is <1 or >96||item.Name.Any(char.IsControl)||!names.Add(item.Name))return false;
  return true;
 }
}
