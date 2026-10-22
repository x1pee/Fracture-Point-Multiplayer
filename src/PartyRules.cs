namespace FractureTogether;

public static class PartyRules
{
 public static int Size(int players)=>Math.Clamp(players,1,4);
 public static double Length(int players)=>1+.5*(Size(players)-1);
 public static double Enemies(int players)=>1+.8*(Size(players)-1);
 public static int LootValue(int players)=>players<=1?1:2*(Size(players)-1);
 public static int ScaleCount(int basis,double multiplier)=>basis<=0?0:checked((int)Math.Ceiling(basis*multiplier-1e-6));
}

// One authoritative claim per world object. No local inventory changes before a grant.
public sealed class SharedLootLedger
{
 readonly Dictionary<string,int> owners=new(StringComparer.Ordinal);
 public bool TryClaim(string key,int player){if(owners.ContainsKey(key))return false;owners.Add(key,player);return true;}
 public bool IsTaken(string key)=>owners.ContainsKey(key);
 public void Reset()=>owners.Clear();
 public IEnumerable<KeyValuePair<string,int>> Entries=>owners;
}
