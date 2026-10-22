namespace FractureTogether;

// Pure rules shared by the Unity adapter and the isolated QA harness.
internal static class PersonalCombatMusicPolicy
{
 internal const float MaximumDistance=60,FreshSeconds=2;
 internal static bool DamageBridge(bool localAlive,bool lostHealth,bool freshNearbyEnemyTargetingLocal)=>localAlive&&lostHealth&&freshNearbyEnemyTargetingLocal;
 internal static bool Threat(bool localAlive,bool enemyAlive,bool hostile,bool engaged,int targetPeer,int localPeer,float distanceSquared,bool visible)=>
  localAlive&&enemyAlive&&hostile&&engaged&&localPeer>=0&&targetPeer==localPeer&&float.IsFinite(distanceSquared)&&distanceSquared>=0&&distanceSquared<=MaximumDistance*MaximumDistance&&visible;
}

internal sealed class PersonalCombatFreshness
{
 long sequence;float changedAt;bool baseline,advanced;
 internal bool Observe(long value,float now){
  if(value<=0||!float.IsFinite(now)){Reset();return false;}
  if(!baseline||value<sequence||now<changedAt){baseline=true;advanced=false;sequence=value;changedAt=now;return false;}
  if(value>sequence){sequence=value;changedAt=now;advanced=true;}
  return advanced&&now-changedAt<=PersonalCombatMusicPolicy.FreshSeconds;
 }
 internal void Reset(){sequence=0;changedAt=0;baseline=advanced=false;}
}
