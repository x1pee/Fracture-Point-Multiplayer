using BepInEx.Logging;
using UnityEngine;

namespace FractureTogether;

// Only scalar snapshots and existing caches: no scene scans or per-frame disk IO.
public sealed partial class Together
{
 float gameLogAt,gameLogFrameAt,gameLogSlowAt,gameLogStateAt;
 int gameLogFrames,gameLogBackground,gameLogOver33,gameLogOver50,gameLogOver100,gameLogOver250;
 double gameLogFrameSum;float gameLogWorst;
 string gameLogScene="",gameLogWorld="";int gameLogFloor=int.MinValue,gameLogFlags=-1;
 void GameplayDiagnosticsTick(){
  if(!Plugin.Instance.SessionLogs.Value)return;
  var now=Time.realtimeSinceStartup;var ms=Time.unscaledDeltaTime*1000f;
  var focused=Application.isFocused&&InputBridge.Active;
  if(focused&&float.IsFinite(ms)&&ms>0){
   gameLogFrames++;gameLogFrameSum+=ms;gameLogWorst=Math.Max(gameLogWorst,ms);
   if(ms>33.333f)gameLogOver33++;if(ms>50)gameLogOver50++;if(ms>100)gameLogOver100++;if(ms>250)gameLogOver250++;
   if(ms>=100&&now>=gameLogSlowAt){gameLogSlowAt=now+1;GameplayLog.Record("frame_spike",new{ms,frame=Time.frameCount,scene,floor,menu=PanelVisualVisible,connected=Connected});}
  }else gameLogBackground++;
  if(now>=gameLogFrameAt){
   if(gameLogFrameAt>0)GameplayLog.Record("frame_window",new{focusedFrames=gameLogFrames,backgroundFrames=gameLogBackground,meanMs=gameLogFrames>0?gameLogFrameSum/gameLogFrames:0,fps=gameLogFrameSum>0?1000*gameLogFrames/gameLogFrameSum:0,worstMs=gameLogWorst,over33ms=gameLogOver33,over50ms=gameLogOver50,over100ms=gameLogOver100,over250ms=gameLogOver250,menu=PanelVisualVisible});
   gameLogFrameAt=now+10;gameLogFrames=gameLogBackground=gameLogOver33=gameLogOver50=gameLogOver100=gameLogOver250=0;gameLogFrameSum=0;gameLogWorst=0;
  }
  if(now<gameLogStateAt)return;gameLogStateAt=now+.25f;
  var flags=(Connected?1:0)|(Down?2:0)|(PartyWiped?4:0)|(Spectating?8:0)|(PersonalSavePending?16:0)|(Recovering?32:0)|(panel?64:0)|(SameWorld?128:0);
  if(flags!=gameLogFlags||scene!=gameLogScene||world!=gameLogWorld||floor!=gameLogFloor){
   gameLogFlags=flags;gameLogScene=scene;gameLogWorld=world;gameLogFloor=floor;
   GameplayLog.Record("game_state",new{scene,floor,world,connected=Connected,sameWorld=SameWorld,Down,PartyWiped,Spectating,savePending=PersonalSavePending,recovering=Recovering,menu=panel});
  }
  if(now<gameLogAt)return;gameLogAt=now+5;
  var health=player?player!.Health:null;
  GameplayLog.Record("sync_health",new{scene,floor,world,role=Network==null?"offline":Network.Hosting?"host":"guest",connected=Connected,sameWorld=SameWorld,rulesReady=RulesReady,partySize=PartySize,scalingPlayers=RunPlayers,requestedFloorLength=Rules.Length(RunPlayers),floorLength=PartyScaling.AppliedLength,coopRoomTrim=.67,enemiesFactor=Rules.Enemies(RunPlayers),lootFactor=LootFactor,victorHpFactor=BossHealthScaling.Multiplier(RunPlayers),localId=Network?.LocalId??-1,received=Network?.ReceivedCount??0,focused,width=Screen.width,height=Screen.height,targetFps=Application.targetFrameRate,vsync=QualitySettings.vSyncCount,graphics=Performance.Active,sharp=Performance.NativeResolution.Value,menu=PanelVisualVisible,blur=MenuBackgroundBlur.Active,hp=health?health!.Health:-1,maxHp=health?health!.MaxHealth:0,nativePause=player&&player!.IsPauseMenuOpened,peers=remotes.Count,doors=doors.Count,enemies=SyncedEnemyCount,pickupsPending=reservations.Count+pendingLootClaims.Count,dropsPending=newDrops.Count,vendingPending=vendingOrders.Count,triggersPending=pendingTriggers.Count,elevatorPending=pendingElevator,savePending=PersonalSavePending,shots=ShotDiagnostics(),fracture=GlassFractureDiagnostics.Diagnostics(),speech=EnemySpeechDiagnostics(),ballistics=NativeBulletSafety.Diagnostics(),spawn=PartySpawnPlacement.Diagnostics()});
  foreach(var peer in remotes.Values){var state=peer.State;GameplayLog.Record("peer_health",new{id=peer.Id,ageSeconds=Math.Max(0,Time.unscaledTime-peer.LastSeen),matchesWorld=PeerMatches(peer),visible=peer.Visible,scene=state?.Scene,floor=state?.Floor,world=state?.World,sequence=state?.Sequence,hp=state?.Health,maxHp=state?.MaxHealth,hasModel=peer.HasCharacterModel});}
 }
 static void LogGameplayPacket(Packet p){
  if(!Plugin.Instance.SessionLogs.Value||p.Kind is not ("joined" or "left" or "scene" or "elevator" or "lootgrant" or "loottaken" or "lootreject" or "lootreq" or "lootack" or "playerdown" or "wipe" or "revive" or "vendgrant" or "vendspawn" or "dropspawn" or "dropreject" or "triggerreq" or "triggerdone"))return;
  GameplayLog.Record("game_event",new{kind=p.Kind,peer=p.PlayerId,target=p.TargetId,sequence=p.Sequence,floor=p.Floor,objectKey=DiagnosticObjectKey(p.ObjectId),item=p.ItemId,count=p.Count,success=p.Success});
 }
 static string DiagnosticObjectKey(string value){if(value.Length==0)return "";var hash=14695981039346656037UL;foreach(var ch in value){hash^=ch;hash=unchecked(hash*1099511628211UL);}return hash.ToString("X16");}
}

// BepInEx retains the detailed original error in LogOutput.log. This companion
// event deliberately omits free-form messages, addresses and configuration.
sealed class GameplayErrorListener:ILogListener
{
 public LogLevel LogLevelFilter=>LogLevel.Error|LogLevel.Fatal;
 long next;public void LogEvent(object sender,LogEventArgs e){
  if((e.Level&(LogLevel.Error|LogLevel.Fatal))==0)return;
  var now=Environment.TickCount64;var due=Interlocked.Read(ref next);if(now<due||Interlocked.CompareExchange(ref next,now+2000,due)!=due)return;
  if(e.Data is Exception error)GameplayLog.Fault("loader",error);
  else GameplayLog.Record("loader_error",new{source=e.Source.SourceName,level=e.Level.ToString(),detailsIn="BepInEx/LogOutput.log"});
 }
 public void Dispose(){}
}
