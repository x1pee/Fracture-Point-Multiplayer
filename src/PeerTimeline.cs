namespace FractureTogether;

// IDs are never reused within one session. A delayed position cannot resurrect a departed peer.
public sealed class PeerTimeline
{
 readonly HashSet<int> departed=new();readonly Dictionary<int,long> sequences=new();
 public bool Accept(int id,long sequence){if(id<0||sequence<=0||departed.Contains(id)||sequence<=sequences.GetValueOrDefault(id))return false;sequences[id]=sequence;return true;}
 public void Left(int id){departed.Add(id);sequences.Remove(id);}
 public void Reset(){departed.Clear();sequences.Clear();}
}
