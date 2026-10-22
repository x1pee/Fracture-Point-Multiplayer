using BepInEx.Configuration;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

static class ModSettings
{
 internal static ConfigEntry<float> Friendly=null!,FloorStep=null!,EnemyStep=null!;
 internal static ConfigEntry<int> Revive=null!,LootTwo=null!,LootThree=null!,LootFour=null!;
 internal static void Initialize(ConfigFile config){
  Friendly=config.Bind("Coop","FriendlyFirePercent",7f,new ConfigDescription("Percent of weapon damage dealt to teammates. Standard 7; can only be made harder.",new AcceptableValueRange<float>(7,100)));
  Revive=config.Bind("Coop","ReviveHealthPercent",20,new ConfigDescription("Own maximum HP restored on the next floor. Standard 20; can only be reduced.",new AcceptableValueRange<int>(1,20)));
  FloorStep=config.Bind("Coop","ExtraFloorLengthPerPlayer",.5f,new ConfigDescription("Additional floor length per extra player before co-op room reduction. Standard 0.5; higher values may cause lag or crashes. Applies to the next run.",new AcceptableValueRange<float>(.5f,1)));
  EnemyStep=config.Bind("Coop","ExtraEnemiesPerPlayer",.8f,new ConfigDescription("Additional enemies per extra player. Standard 0.8; higher values may cause lag or crashes. Applies to the next run.",new AcceptableValueRange<float>(.8f,2)));
  LootTwo=config.Bind("Coop","LootValueTwoPlayers",2,new ConfigDescription("Sale multiplier for newly acquired shared loot with two players. Standard 2; can only be reduced.",new AcceptableValueRange<int>(1,2)));
  LootThree=config.Bind("Coop","LootValueThreePlayers",4,new ConfigDescription("Sale multiplier for newly acquired shared loot with three players. Standard 4; can only be reduced.",new AcceptableValueRange<int>(1,4)));
  LootFour=config.Bind("Coop","LootValueFourPlayers",6,new ConfigDescription("Sale multiplier for newly acquired shared loot with four players.",new AcceptableValueRange<int>(1,6)));
 }
 internal static ModRules Read(int revision=1)=>new(){Revision=revision,FriendlyFire=Friendly.Value/100,ReviveHealthPercent=Revive.Value,FloorStep=FloorStep.Value,EnemyStep=EnemyStep.Value,LootTwo=LootTwo.Value,LootThree=LootThree.Value,LootFour=LootFour.Value};
}

public sealed partial class Together
{
 public ModRules Rules=new();bool receivedRules;float rulesAt;readonly Dictionary<int,int> rulesAcknowledged=new();
 bool RulesReady=>Network==null||(Network.Hosting?remotes.Keys.All(id=>rulesAcknowledged.GetValueOrDefault(id)==Rules.Revision):receivedRules);
 void ResetRules(){Rules=ModSettings.Read();receivedRules=false;rulesAcknowledged.Clear();rulesAt=0;}
 void RulesTick(){if(Network?.Hosting==true&&Connected&&Time.unscaledTime>=rulesAt){rulesAt=Time.unscaledTime+1;Network.Send(new Packet{Kind="rules",Rules=Rules});}}
 [HideFromIl2Cpp]
 bool ReceiveRules(Packet p){
  if(p.Kind=="rules"&&Network?.Hosting==false){if(p.Rules!=null&&p.Rules.Revision>=Rules.Revision){Rules=p.Rules;receivedRules=true;Network.Send(new Packet{Kind="rulesack",Sequence=Rules.Revision});}return true;}
  if(p.Kind=="rulesack"&&Network?.Hosting==true){if(p.Sequence==Rules.Revision)rulesAcknowledged[p.PlayerId]=Rules.Revision;return true;}
  return false;
 }
 void ApplyRuleSettings(){Rules=ModSettings.Read(Rules.Revision+1);rulesAcknowledged.Clear();rulesAt=0;Plugin.Instance.Config.Save();}
}
