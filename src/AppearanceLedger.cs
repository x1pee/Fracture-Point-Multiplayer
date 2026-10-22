using System.Collections;

namespace FractureTogether;

// One Unity-thread consumer. Pose snapshots never overwrite this reliable cache.
// IDs are authenticated by Session and are never reused until the session resets.
public sealed class AppearanceLedger
{
 public const int Capacity=64;
 const int MaximumPlayerId=1000000;
 readonly Dictionary<int,(long Sequence,OutfitEntry[] Outfit)> entries=new();
 // Fixed memory rather than an ever-growing set under reconnect churn. Keep
 // exact tombstones even after more than 64 distinct players have departed.
 readonly BitArray departed=new(MaximumPlayerId+1);
 public int Count=>entries.Count;
 public bool Apply(int id,long seq,OutfitEntry[] outfit){
  if(id<0||id>MaximumPlayerId||seq<=0||departed[id]||!OutfitEntry.Validate(outfit)||outfit.Length==0)return false;
  var known=entries.TryGetValue(id,out var previous);
  if(known&&seq<=previous.Sequence||!known&&entries.Count>=Capacity)return false;
  entries[id]=(seq,Copy(outfit));return true;
 }
 public OutfitEntry[] Get(int id)=>entries.TryGetValue(id,out var value)?Copy(value.Outfit):Array.Empty<OutfitEntry>();
 public IEnumerable<(int PlayerId,long Sequence,OutfitEntry[] Outfit)> Snapshot(){
  // Callers enumerate on the same consumer thread and do not mutate the ledger
  // during replay. Returned DTOs are detached from both caller and cache.
  foreach(var pair in entries)yield return(pair.Key,pair.Value.Sequence,Copy(pair.Value.Outfit));
 }
 public void Left(int id){if(id<0||id>MaximumPlayerId)return;entries.Remove(id);departed[id]=true;}
 public void Reset(){entries.Clear();departed.SetAll(false);}
 static OutfitEntry[] Copy(OutfitEntry[] source){var result=new OutfitEntry[source.Length];for(var i=0;i<source.Length;i++)result[i]=new OutfitEntry{Name=source[i].Name,Enabled=source[i].Enabled};return result;}
}
