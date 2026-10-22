namespace FractureTogether;

public sealed class LootEntry
{
 public int Type {get;set;}
 public int Index {get;set;}
}

public static class LootContents
{
 // Native containers remove only entries that fit. An ACK may retain a sub-multiset,
 // including repeated entries, but may never create new loot.
 public static bool IsRemainder(LootEntry[] original,LootEntry[] remaining)
 {
  if(remaining.Length>original.Length)return false;
  var counts=original.GroupBy(e=>(e.Type,e.Index)).ToDictionary(g=>g.Key,g=>g.Count());
  foreach(var entry in remaining){var key=(entry.Type,entry.Index);if(!counts.TryGetValue(key,out var n)||n==0)return false;counts[key]=n-1;}
  return true;
 }
}
