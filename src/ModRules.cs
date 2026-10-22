namespace FractureTogether;

public sealed class ModRules
{
 public int Revision {get;set;}=1;
 public float FriendlyFire {get;set;}=.07f;
 public int ReviveHealthPercent {get;set;}=20;
 public float FloorStep {get;set;}=.5f;
 public float EnemyStep {get;set;}=.8f;
 public int LootTwo {get;set;}=2;
 public int LootThree {get;set;}=4;
 public int LootFour {get;set;}=6;
 public bool Valid()=>Revision>0&&float.IsFinite(FriendlyFire)&&FriendlyFire>=.07f&&FriendlyFire<=1&&ReviveHealthPercent>=1&&ReviveHealthPercent<=20&&float.IsFinite(FloorStep)&&FloorStep>=.5f&&FloorStep<=1&&float.IsFinite(EnemyStep)&&EnemyStep>=.8f&&EnemyStep<=2&&LootTwo>=1&&LootTwo<=2&&LootThree>=1&&LootThree<=4&&LootFour>=1&&LootFour<=6;
 public double Length(int players)=>1+FloorStep*(PartyRules.Size(players)-1);
 public double Enemies(int players)=>1+EnemyStep*(PartyRules.Size(players)-1);
 public int Loot(int players)=>PartyRules.Size(players) switch{2=>LootTwo,3=>LootThree,4=>LootFour,_=>1};
 public int ReviveHp(int maximum)=>Math.Max(1,(int)Math.Ceiling(Math.Max(1,maximum)*ReviveHealthPercent/100d));
}
