using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Demo.Scripts.Runtime.Character;
using DungeonArchitect;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FractureTogether;

[BepInPlugin("local.fracture.together", "Fracture Point Multiplayer", "0.3.0")]
public sealed class Plugin : BasePlugin
{
    public static Plugin Instance = null!;
    public ConfigEntry<string> Address = null!, Room = null!, Nick = null!, Auto = null!;
    public ConfigEntry<int> Port = null!;
    public ConfigEntry<KeyCode> MenuKey=null!;
    public ConfigEntry<bool> MenuBlur=null!, SessionLogs=null!;
    GameplayErrorListener? gameplayErrorListener;
    public static readonly KeyCode[] MenuKeys={KeyCode.F11,KeyCode.F12,KeyCode.Home,KeyCode.End};
    public ConfigEntry<bool> SharedSeed = null!, Diagnostics = null!, PasswordProtection = null!;
    public string Build = "";
    public PersonalProfile Profile = null!;
    public override void Load()
    {
        Instance = this;
        MenuKey=Config.Bind("Interface","MenuKey",KeyCode.F11,"Open/close the mod menu. Choose F11, F12, Home, End or a custom keyboard key in the menu. F6/F7/F9/F10 are reserved; Escape cancels capture. Modifier-only keys and Fn are unsupported.");
        if(!ModMenuHotkeys.IsValid(MenuKey.Value))MenuKey.Value=KeyCode.F11;
        MenuBlur=Config.Bind("Interface","MenuBlur",true,"Soft background blur only while the mod menu is open. Disable to reduce menu GPU cost.");
        MenuBackgroundBlur.Initialize(MenuBlur);
        Address = Config.Bind("Connection", "HostAddress", "127.0.0.1", "Host LAN/VPN IPv4 or DNS name.");
        Port = Config.Bind("Connection", "Port", 27841, new ConfigDescription("TCP port", new AcceptableValueRange<int>(1024,65535)));
        PasswordProtection = Config.Bind("Connection", "PasswordProtection", false, "Require a password when hosting. Off creates an open LAN/VPN lobby.");
        Room = Config.Bind("Connection", "RoomCode", "", "Optional lobby password, 1-64 characters when protection is enabled. Legacy storage key.");
        Nick = Config.Bind("Connection", "Nickname", "Player", "Name shown to other players, at most 20 displayed Unicode characters.");
        Nick.Value = NicknameRules.Clean(Nick.Value);
        Auto = Config.Bind("Connection", "AutoStart", "Off", "Off, Host or Join. Default Off.");
        SharedSeed = Config.Bind("World", "SharedDungeonSeed", true, "Use session/floor seed for new builds while connected. Join before leaving the hub.");
        Diagnostics = Config.Bind("Debug", "WriteStatus", false, "Write status.json to BepInEx/FractureTogether once per second.");
        var native = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
        Build = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native)));
        if(Build != "E1247B9A7FC5A9B80C223F65D779FF8C0D755353B02E5A8C1E9BAAC4A7C3312B") {
            Log.LogError("Unsupported GameAssembly; this plugin is pinned to the analyzed build and has not been loaded."); return;
        }
        Build += ":"+Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Plugin).Assembly.Location)));
        SessionLogs=Config.Bind("Diagnostics","SessionLog",true,"Bounded background JSONL logs for manual testing. No passwords, network addresses, nickname or save contents. Restart after changing.");
        GameplayLog.Failure=(phase,type)=>Log.LogWarning("Gameplay logger "+phase+" failed ("+type+").");
        GameplayLog.Start(Path.Combine(Paths.BepInExRootPath,"FractureTogether","Logs"),Build,SessionLogs.Value);
        if(SessionLogs.Value){gameplayErrorListener=new GameplayErrorListener();BepInEx.Logging.Logger.Listeners.Add(gameplayErrorListener);}
        try { Profile=new PersonalProfile(Config.Bind("Saves","PersonalProfile","Personal","Local protected profile. Created once from THIS computer's own saves and PlayerPrefs. Never received from the host. ASCII letters/numbers/_/-. Changing requires restart.").Value);
        LootEconomy.Load();Performance.Initialize();ModSettings.Initialize(Config);
        var settingsVersion=Config.Bind("Compatibility","SettingsVersion",0,"Configuration migration version.");
        // Config entries clamp legacy values to the same harder-only bounds used
        // by the menu and wire validator. Preserve an already harder rule.
        if(settingsVersion.Value<5){settingsVersion.Value=5;Config.Save();}
        new Harmony("local.fracture.together").PatchAll(typeof(Plugin).Assembly);
        AddComponent<Together>(); }
        catch(Exception e) { GameplayLog.Fault("startup",e);File.WriteAllText(Path.Combine(Paths.BepInExRootPath,"profile-error.txt"),e.ToString()); Log.LogFatal("Save protection failed. Closing the test game before gameplay: "+e); Application.Quit(); return; }
        Log.LogInfo("Fracture Point Multiplayer 0.3.0 loaded. F6 host / F7 join / F11 settings / F9 disconnect. Personal saves remain on this computer.");
    }
}

public sealed partial class Together : MonoBehaviour
{
    public Together(IntPtr ptr) : base(ptr) { }
    public static Together? Current;
    public Session? Network;
    public bool Replay;
    public bool Connected => Network?.Connected == true;
    public static bool HasInputFocus=>InputBridge.Active;
    public bool SameWorld => Connected && RulesReady && remotes.Count == Network!.PlayerCount-1 && remotes.Values.All(r=>PeerMatches(r));
    public int PartySize => Network?.PlayerCount ?? 1;
    readonly Dictionary<int,RemoteAvatar> remotes=new();
    readonly PeerTimeline peerTimeline=new();
    bool PeerMatches(RemoteAvatar r) => r.State!=null && r.State.Scene==scene && r.State.Floor==floor && r.State.World==world && Time.unscaledTime-r.LastSeen<3;
    public string scene = "", world = "", status = "Offline", detail = "";
    ConnectionIssue? connectionIssue;
    public int floor = -1;
    public int SessionFloor=-1;
    FPSController? player;
    internal FPSController? LocalController=>player;
    LevelElevator? elevator;

    readonly Dictionary<string, Door> doors = new();
    readonly Dictionary<int,string> doorIds = new();
    readonly Dictionary<int,string> initialDoorLocations = new();
    readonly Dictionary<int,string> initialDoorKeys = new();
    readonly Dictionary<string,Packet> openedDoors = new();
    float doorSnapshotAt;
    string locationEpoch = "";
    float scanAt, sendAt, diagAt;
    long sequence;
    int doorSent, doorReceived, elevatorReceived;
    int pendingElevator,pendingFloor;string pendingScene="",pendingLiftKey="";float pendingElevatorAt;
    long elevatorSequence,lastElevatorSequence;string elevatorBusyWorld="";float elevatorBusyAt;
    string lastWorldLog = "";
    bool floorLayoutReady;
    Dungeon? cachedDungeon;bool discoveryDirty=true;int discoveryRevision=-1;
    internal void InvalidateWorldDiscovery(){discoveryDirty=true;floorLayoutReady=false;scanAt=0;}
    bool panel;
    bool autoPending=true;
    string address = "", code = "", nick = "";bool usePassword;
    CursorLockMode previousLock;
    bool previousCursor;
    PlayerInput? pausedInput;

    void Awake()
    {
        Current = this;useGUILayout=false;InitializeHudHint();
        GameplayLog.Record("game_started",new{unity=Application.unityVersion,gpu=SystemInfo.graphicsDeviceName,width=Screen.width,height=Screen.height,protocol=Session.Protocol});
        InitSpectator();address = Plugin.Instance.Address.Value; code = Plugin.Instance.Room.Value; nick = Plugin.Instance.Nick.Value;usePassword=Plugin.Instance.PasswordProtection.Value;
        Application.runInBackground = true;
    }
    public void StartSession(bool host)
    {
        connectionIssue=null;connectionUiError="";
        connectionMode=host?0:1;
        if(scene!="Example_Scene_Night 1"){ShowConnectionIssue(ConnectionErrors.Get("MP-020","local"));return;}
        StopSession();
        ResetRules();ResetPartyDeath();
        SessionFloor=-1;
        buttonRequests.Reset();buttonSequence=elevatorSequence=lastElevatorSequence=0;elevatorBusyWorld="";
        try {
            try{Plugin.Instance.Profile.SnapshotBeforeConnection();}catch{throw new ConnectionException(ConnectionErrors.Get("MP-022","save"));}
            DungeonSeedPatch.Reset();RunPlayers=1;
            if(host&&usePassword&&(string.IsNullOrWhiteSpace(code)||code.Length>64)||!host&&code.Length>64)throw new ConnectionException(ConnectionErrors.Get("MP-021","local"));
            var name=NicknameRules.Clean(nick);nick=name;
            Network = new Session(Plugin.Instance.Build, host&&!usePassword?"":code, name);
            Network.Diagnostic=GameplayLog.Record;
            if(host) Network.Host(Plugin.Instance.Port.Value); else Network.Join(address.Trim(), Plugin.Instance.Port.Value);
            status = host ? "HOST: waiting for guest" : "JOIN: connecting";detail="";
            Plugin.Instance.Address.Value = address.Trim(); Plugin.Instance.Room.Value = code; Plugin.Instance.Nick.Value = name;Plugin.Instance.PasswordProtection.Value=usePassword;
            Plugin.Instance.Config.Save();
            scanAt = 0;
        } catch(Exception e) { GameplayLog.Fault("connect",e);Network?.Dispose();Network=null;ShowConnectionIssue(ConnectionErrors.From(e,host?"host_start":"connect")); }
    }
    [HideFromIl2Cpp]
    void ShowConnectionIssue(ConnectionIssue issue){
        connectionIssue=issue;status=issue.Code+": "+issue.Title;detail=issue.Hint+(issue.Versions.Length>0?"\n"+issue.Versions:"");connectionUiError="";
        GameplayLog.Record("connection_ui_failure",new{code=issue.Code,phase=issue.Phase,versions=issue.Versions});
        try{Plugin.Instance.Log.LogWarning(status+" "+detail);}catch{/* The connection error card remains usable when logging is unavailable. */}settingsTab=0;SetPanel(true);
    }
    public void StopSession()
    {
        Plugin.Instance.Profile.Flush();
        if(Network!=null)GameplayLog.Record("session_stopping",new{role=Network.Hosting?"host":"guest",localId=Network.LocalId,scene,floor,Down,PartyWiped,quitting});
        var recover=Network!=null&&SharedRun&&!PartyWiped&&!quitting;
        // A guest may have completed a native pickup just before losing its
        // ACK. Quarantine uncertain host grants before drop identities vanish.
        if(Network?.Hosting==true)foreach(var owner in reservations.Values.Select(g=>g.TargetId).Distinct().ToArray())LootPeerLeft(owner);
        WorldVariantSync.Reset();PianoReset();VendingReset(Network?.Hosting==true);FoodReset(Network?.Hosting==true);DropReset();ClearShotEffects();ClearEnemyShotEffects();PartySpawnPlacement.Reset();BossDoorReset();
        ClearEnemies();
        Network?.Dispose(); Network = null; sequence = 0;
        pendingElevator=0;
        foreach(var r in remotes.Values) r.Dispose(); remotes.Clear();
        ResetAppearance();
        ResetLocalAnimationState();
        peerTimeline.Reset();
        buttonRequests.Reset();vendingRequests.Reset();vendingEpoch="";pendingVending="";vendingOrders.Clear();
        rulesAcknowledged.Clear();
        if(Down&&!PartyWiped)ReviveLocal();
        StopSpectator();RunPlayers=1;SessionFloor=-1;
        reservations.Clear();requestedLoot.Clear();pendingVendingSequence=0;
        status = "Offline";
        if(recover)BeginDisconnectRecovery();
    }
    void Update()
    {
        try { Tick(); }
        catch(Exception e) { GameplayLog.Fault("tick",e);detail = e.GetType().Name + ": " + e.Message; Plugin.Instance.Log.LogError(e); StopSession(); }
    }
    void Tick()
    {
        Plugin.Instance.Profile.Tick();
        try{GameplayDiagnosticsTick();}catch(Exception error){GameplayLog.Fault("diagnostics_snapshot",error);}
        Performance.Tick();
        MenuInputTick();
        MenuBackgroundBlur.Tick(Ease(panelOpenAmount));
        RulesTick();PartyDeathTick();PersonalSaveTick();DisconnectRecoveryTick();
        if(!Plugin.Instance.Profile.Healthy){StopSession();detail="Personal save storage failed. Session stopped to protect progress.";return;}
        UpdateMenuHotkeys();
        SpectatorInputTick();
        var keyboard=Keyboard.current;
        if(Time.unscaledTime >= scanAt) { scanAt = Time.unscaledTime + 1; ScanWorld(); }
        if(autoPending&&scene=="Example_Scene_Night 1"&&player){
            autoPending=false;
            if(Plugin.Instance.Auto.Value.Equals("Host",StringComparison.OrdinalIgnoreCase))StartSession(true);
            else if(Plugin.Instance.Auto.Value.Equals("Join",StringComparison.OrdinalIgnoreCase))StartSession(false);
        }
        WorldVariantSync.Tick(this);
        WorldBallisticSafetyTick();
        EnemyTick();
        EnemyShotTick();
        PartySpawnPlacement.Tick();
        UnitTick();
        ProjectileTick();
        PresentationTick();
        ShotTick();
        UpdateSharedLoot();DropTick();
        VendingTick();FoodTick();
        WorldTriggerTick();BossDoorTick();
        // Keep the failed session and its first issue alive while queued reliable
        // packets establish whether this was a native party wipe or a disconnect.
        var receivedSession=Network;
        ConnectionIssue? pendingFailure=null;
        var endingWipe=PartyWiped;var endingQuit=quitting;
        var receiveBudget=64;var receiveFailed=false;
        if(receivedSession?.Hosting==false)receivedSession.Failures.TryPeek(out pendingFailure);
        if(receivedSession?.Hosting==true)while(receivedSession.Failures.TryDequeue(out _)){}
        if(receivedSession!=null&&ReferenceEquals(Network,receivedSession)) {
            while(receivedSession.Events.TryDequeue(out var message)) {
                status = message; Plugin.Instance.Log.LogInfo(message);
            }
            try {
                while(receiveBudget>0&&ReferenceEquals(Network,receivedSession)&&receivedSession.TryReceive(out var p)) {
                    receiveBudget--;Receive(p);
                    endingWipe|=PartyWiped;endingQuit|=quitting;
                }
            } catch(Exception error) {
                if(!receivedSession.Hosting&&pendingFailure==null)receivedSession.Failures.TryPeek(out pendingFailure);
                if(pendingFailure==null)throw;
                receiveFailed=true;endingWipe|=PartyWiped;endingQuit|=quitting;
                GameplayLog.Fault("connection_failure_receive",error);
            }
        }
        if(receivedSession?.Hosting==false) {
            // EOF can arrive after the first peek, including immediately before
            // a queued local 'left' has already stopped this session.
            if(pendingFailure==null)receivedSession.Failures.TryPeek(out pendingFailure);
            if(pendingFailure!=null) {
                endingWipe|=PartyWiped;endingQuit|=quitting;
                if(Network!=null&&!ReferenceEquals(Network,receivedSession)) {
                    receivedSession.Failures.TryDequeue(out _);
                    GameplayLog.Record("connection_failure_superseded",new{code=pendingFailure.Code,phase=pendingFailure.Phase});
                } else if(receiveBudget>0||Network==null||endingWipe||endingQuit||receiveFailed) {
                    // A full batch may have a wipe behind it. Keep the issue
                    // queued until the next tick rather than exceed 64 receives.
                    receivedSession.Failures.TryDequeue(out _);
                    var intentionalEnding=endingWipe||endingQuit;
                    if(intentionalEnding)GameplayLog.Record("connection_expected_close",new{code=pendingFailure.Code,phase=pendingFailure.Phase,PartyWiped=endingWipe,quitting=endingQuit});
                    if(ReferenceEquals(Network,receivedSession))StopSession();
                    if(!intentionalEnding)ShowConnectionIssue(pendingFailure);
                }
            }
        }
        if(Network != null) {
            if(Connected && Time.unscaledTime >= sendAt) {
                sendAt = Time.unscaledTime + .05f;
                if(player && player!.gameObject.activeInHierarchy) {
                    var pos = player.transform.position;
                    var camera = player.Camera;
                    var yaw = player.transform.eulerAngles.y;
                    var pitch = camera ? camera.eulerAngles.x : 0;
                    Network.Send(new Packet { Kind="state", Scene=scene, Floor=floor, World=world, Name=Plugin.Instance.Nick.Value,
                        Pose=new[]{pos.x,pos.y,pos.z,yaw,pitch,player.GetComponent<CharacterController>()?.height ?? 1.8f,player.GetComponent<CharacterController>()?.center.y ?? 0},
                        Health=Down?0:player.Health ? player.Health.Health : 0, MaxHealth=player.Health ? player.Health.MaxHealth : 0, WeaponId=CurrentHeldWeaponId(), Stance=CurrentStance, Motion=CurrentMotion, AimState=CurrentAimState, ThrowSequence=CurrentThrowSequence, ReloadSequence=LocalReloadCapture.Sequence, ReloadKind=LocalReloadCapture.Kind, ReloadElapsed=LocalReloadCapture.Elapsed, Sequence=++sequence,
                        View=CaptureLocalView(),Firing=!Down&&keyboard != null && Mouse.current != null && Mouse.current.leftButton.isPressed && !panel && !player.IsPauseMenuOpened });
                    SendAppearance();
                } else Network.Send(new Packet { Kind="ping" });
            }
        }
        RenderRemote();
        if(pendingElevator!=0){
            if(!Connected||scene!=pendingScene||floor!=pendingFloor||Time.unscaledTime-pendingElevatorAt>45)pendingElevator=0;
            else if(SameWorld){var lift=Object.FindObjectsOfType<LevelElevator>().FirstOrDefault(e=>LiftKey(e)==pendingLiftKey);if(lift&&!lift.isMoving){var direction=pendingElevator;pendingElevator=0;if(direction==1)lift.MoveToNextFloor();else lift.ReturnToHub();}}
        }
        ResendDoors();
        if(Plugin.Instance.Diagnostics.Value && Time.unscaledTime >= diagAt) {
            diagAt=Time.unscaledTime+1;
            try{WriteDiagnostics();}
            catch(Exception error){diagAt=Time.unscaledTime+30;try{GameplayLog.Fault("status_write",error);}catch{}try{Plugin.Instance.Log.LogWarning("Status diagnostics unavailable ("+error.GetType().Name+"). Network session remains active.");}catch{}}
        }
    }
    void ScanWorld()
    {
        var activeScene=SceneManager.GetActiveScene().name;var changed=scene!=activeScene;
        if(changed){Plugin.Instance.Profile.Flush();FoodReset(Network?.Hosting==true);scene=activeScene;player=null;elevator=null;cachedDungeon=null;discoveryDirty=true;discoveryRevision=-1;floorLayoutReady=false;if(scene=="MainMenu"&&Network!=null)StopSession();}
        var raid=scene=="URP_OfficeCorridor";var playable=raid||scene=="Example_Scene_Night 1";
        if(playable&&!player)player=Object.FindObjectOfType<FPSController>();
        if(raid&&!elevator)elevator=Object.FindObjectOfType<LevelElevator>();
        floor=raid?(DungeonSeedPatch.Latest?DungeonSeedPatch.BuiltFloor:PlayerPrefs.GetInt("currentFloor",0)):-1;
        if(Network!=null&&RunPlayers>1)Network.LobbyOpen=false;
        if(raid&&DungeonSeedPatch.Latest)cachedDungeon=DungeonSeedPatch.Latest;
        // The entrance street is authored, with no Dungeon or generated model.
        // Probe it once per discovery revision; a later native build invalidates
        // the cache through Latest/Revision and restores generated-floor scans.
        var authoredStreet=raid&&floor==0&&!DungeonSeedPatch.Latest&&!DungeonSeedPatch.Building;
        if(raid&&!cachedDungeon&&(!authoredStreet||discoveryDirty||discoveryRevision!=DungeonSeedPatch.Revision))cachedDungeon=Object.FindObjectsOfType<Dungeon>().OrderByDescending(d=>d.ActiveModel.TryCast<DungeonArchitect.Builders.Snap.SnapModel>()?.modules?.Length??0).FirstOrDefault();
        var dungeon=raid?cachedDungeon:null;
        var dungeonSeed = dungeon && dungeon!.Config ? dungeon.Config.Seed : 0;
        var epoch=scene+"|"+floor+"|"+dungeonSeed;
        if(!discoveryDirty&&epoch==locationEpoch&&discoveryRevision==DungeonSeedPatch.Revision&&(!raid||floorLayoutReady||authoredStreet))return;
        if(DungeonSeedPatch.Building)return;
        discoveryDirty=false;discoveryRevision=DungeonSeedPatch.Revision;
        if(epoch!=locationEpoch) { initialDoorLocations.Clear();initialDoorKeys.Clear();openedDoors.Clear(); locationEpoch=epoch; }
        foreach(var key in initialLootKeys.Where(p=>!p.Value.Object).Select(p=>p.Key).ToArray())initialLootKeys.Remove(key);
        var found = raid?Object.FindObjectsOfType<Door>():Array.Empty<Door>();
        doors.Clear(); doorIds.Clear();
        var fingerprint = new List<string>();
        foreach(var d in found) {
            if(!d || !d.gameObject.scene.IsValid()) continue;
            var instanceId=d.GetInstanceID();
            if(!initialDoorKeys.TryGetValue(instanceId,out var id)){id=SpatialKey(d.transform);initialDoorKeys[instanceId]=id;}
            if(doors.ContainsKey(id)) continue;
            if(!initialDoorLocations.TryGetValue(instanceId,out var location)) {
                var p=d.transform.position;
                location=FormattableString.Invariant($"{MathF.Round(p.x,1)},{MathF.Round(p.y,1)},{MathF.Round(p.z,1)}");
                initialDoorLocations[instanceId]=location;
            }
            doors[id] = d; doorIds[instanceId] = id; fingerprint.Add(id+"@"+location);
        }
        fingerprint.Sort(StringComparer.Ordinal);
        // Gameplay can destroy doors. World identity comes from the immutable room layout.
        fingerprint.Clear();
        var model=dungeon?dungeon.ActiveModel.TryCast<DungeonArchitect.Builders.Snap.SnapModel>():null;
        floorLayoutReady=model?.modules?.Length>0;
        if(model?.modules!=null)foreach(var module in model.modules){var b=module.WorldBounds;var m=module.WorldTransform;fingerprint.Add(string.Join(",",new[]{b.center.x,b.center.y,b.center.z,b.size.x,b.size.y,b.size.z,m.m00,m.m02,m.m20,m.m22}.Select(x=>Quantize(x,100))));}
        fingerprint.Sort(StringComparer.Ordinal);
        world = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(epoch+"|"+string.Join(";",fingerprint))))[..16];
        if(world != lastWorldLog) {
            lastWorldLog = world;
            Plugin.Instance.Log.LogInfo($"World: scene={scene} floor={floor} fingerprint={world} doors={doors.Count} player={(bool)player}");
        }
    }
    static string ObjectKey(Transform t)
    {
        var path = new List<string>();
        for(var p=t; p && path.Count<32; p=p.parent) path.Add(p.name+"["+p.GetSiblingIndex()+"]");
        path.Reverse();
        return string.Join("/",path);
    }
    internal static string SpatialKey(Transform t){var p=t.position;var r=t.eulerAngles;return FormattableString.Invariant($"{t.name}|{Quantize(p.x,100)},{Quantize(p.y,100)},{Quantize(p.z,100)}|{QuantizeAngle(r.x)},{QuantizeAngle(r.y)},{QuantizeAngle(r.z)}");}
    internal static int Quantize(float value,int scale)=>(int)MathF.Round(value*scale,MidpointRounding.AwayFromZero);
    static int QuantizeAngle(float value)=>(Quantize(Mathf.Repeat(value,360),10)+3600)%3600;
    [HideFromIl2Cpp]
    void Receive(Packet p)
    {
        LogGameplayPacket(p);
        if(WorldVariantSync.Receive(this,p))return;
        if(ReceiveAppearance(p))return;
        if(ReceiveRules(p)||ReceivePartyDeath(p)||ReceiveElevatorRequest(p)||ReceiveVending(p)||ReceiveFood(p)||ReceivePiano(p)||ReceiveBossDoor(p)||ReceiveWorldTrigger(p))return;
        if(p.Kind=="scene"&&Network?.Hosting==false){
            if(p.ItemId==1){RunPlayers=p.PlayerCount;SessionFloor=p.Floor;}Replay=true;
            try{Object.FindObjectOfType<SceneLoadingManager>().LoadScene(p.ItemId);}finally{Replay=false;}
            return;
        }
        if(ReceiveDrop(p)||ReceiveSharedLoot(p))return;
        if(ReceiveEnemy(p))return;
        if(ReceiveUnit(p))return;
        if(ReceiveProjectile(p)||ReceiveShot(p)||ReceiveEnemyShot(p))return;
        if(p.Kind == "state") {
            if(!Connected||p.Pose.Length is not (6 or 7) || p.PlayerId==Network!.LocalId||!peerTimeline.Accept(p.PlayerId,p.Sequence)) return;
            if(!remotes.TryGetValue(p.PlayerId,out var r)) {r=new RemoteAvatar(p.PlayerId);remotes[p.PlayerId]=r;}
            r.State=p;r.LastSeen=Time.unscaledTime;
            if(Network.Hosting) Network.Relay(p,p.PlayerId);
        } else if(p.Kind=="left") {
            appearances.Left(p.PlayerId);
            FoodPeerLeft(p.PlayerId);
            if(Network?.Hosting==true)LootPeerLeft(p.PlayerId);
            peerTimeline.Left(p.PlayerId);
            if(remotes.Remove(p.PlayerId,out var r))r.Dispose();
            if(Network?.Hosting==false&&p.PlayerId==0){StopSession();}
            else if(Network?.Hosting==true&&!Network.Connected&&SharedRun)StopSession();
        } else if(p.Kind == "door" && Connected && RulesReady && p.World==world && p.Scene==scene && p.Floor==floor && p.Direction is 0 or 1 && doors.TryGetValue(p.ObjectId, out var door) && door && door.door) {
            if(Network!.Hosting){if(!remotes.TryGetValue(p.PlayerId,out var sender)||!PeerMatches(sender))return;var pose=sender.State!.Pose;if(Vector3.Distance(door.transform.position,new Vector3(pose[0],pose[1],pose[2]))>7)return;openedDoors[p.ObjectId]=p;}
            Replay=true;
            // Local breach prediction already ran the guest's native animation.
            // Still acknowledge an already-open door to its original requester.
            try { if(!door.opened&&!door.isOpen)door.OpenDoorFast(p.Direction,p.ByHand,false); doorReceived++; if(Network!.Hosting)Network.Send(p); }
            finally { Replay=false; }
        } else if(p.Kind == "elevator" && Network?.Hosting==false && elevator && p.Scene==scene && p.Floor==floor && p.World==world&&p.Sequence>lastElevatorSequence) {
            var selectedLift=Object.FindObjectsOfType<LevelElevator>().FirstOrDefault(e=>LiftKey(e)==p.ObjectId);
            if(!selectedLift){detail="Matching elevator not found; transition blocked.";return;}
            if(p.Direction==1&&p.Pose.Length==3)RememberArrival(new Vector3(p.Pose[0],p.Pose[1],p.Pose[2]));
            lastElevatorSequence=p.Sequence;
            elevator=selectedLift;InvalidateWorldDiscovery();
            Replay=true;
            try { if(p.Direction==1) selectedLift.MoveToNextFloor(); else if(p.Direction==-1) selectedLift.ReturnToHub(); elevatorReceived++; }
            finally { Replay=false; }
        }
    }
    public void DoorOpened(Door door, int direction, bool byHand)
    {
        if(!door || Replay || !Connected || Network!.Hosting==false || !doorIds.TryGetValue(door.GetInstanceID(),out var id)) return;
        var packet=new Packet {Kind="door",Scene=scene,Floor=floor,World=world,ObjectId=id,Direction=Math.Sign(direction),ByHand=byHand};
        openedDoors[id]=packet;if(!SameWorld)return;Network.Send(packet);
        doorSent++;
    }
    public bool DoorRequest(Door door,int direction,bool byHand,bool byPlayer){
        if(Replay||!Connected)return true;
        if(!door)return false;
        if(door.opened||door.isOpen)return true;
        if(door&&!doorIds.ContainsKey(door.GetInstanceID())){var key=SpatialKey(door.transform);doorIds[door.GetInstanceID()]=key;doors[key]=door;}
        if(Network!.Hosting){
            if(byPlayer&&!RulesReady)return false;
            if(doorIds.TryGetValue(door.GetInstanceID(),out var key))openedDoors[key]=new Packet{Kind="door",Scene=scene,Floor=floor,World=world,ObjectId=key,Direction=Math.Sign(direction),ByHand=byHand};
            return true;
        }
        if(!RulesReady||!remotes.TryGetValue(0,out var host)||!PeerMatches(host)){detail="Door blocked: waiting for the host's matching world";return false;}
        if(doorIds.TryGetValue(door.GetInstanceID(),out var id))Network.Send(new Packet{Kind="door",Scene=scene,Floor=floor,World=world,ObjectId=id,Direction=Math.Sign(direction),ByHand=byHand});
        // Blocking here cancelled the native running-breach interaction and
        // removed the guest's momentum while the host round trip completed.
        return byPlayer;
    }
    void ResendDoors(){
        if(Network?.Hosting!=true||!Connected||!RulesReady||Time.unscaledTime<doorSnapshotAt)return;doorSnapshotAt=Time.unscaledTime+2;
        foreach(var pair in doors)if(pair.Value&&pair.Value.isOpen&&!openedDoors.ContainsKey(pair.Key)){
            var d=pair.Value;var dir=!d.door||Quaternion.Angle(d.door.localRotation,d.openForwardRotation)<=Quaternion.Angle(d.door.localRotation,d.openBackwardRotation)?0:1;
            openedDoors[pair.Key]=new Packet{Kind="door",Scene=scene,Floor=floor,World=world,ObjectId=pair.Key,Direction=dir,ByHand=true};
        }
        foreach(var p in openedDoors.Values){p.Scene=scene;p.Floor=floor;p.World=world;Network.Send(p);}
    }
    public bool ElevatorMove(int direction,LevelElevator selected)
    {
        if(returningAfterDisconnect&&!Replay)return false;
        if(Replay || !Connected) return true;
        if(Network?.Hosting != true) { detail="Only the host can move the elevator during a session"; return false; }
        if(selected.isMoving){detail="Elevator is already moving.";return false;}
        if(direction==1&&!ElevatorDepartureReady(selected))return false;

        if(!ElevatorTransactionsReady(selected))return false;
        pendingElevator=0;
        elevator=selected;InvalidateWorldDiscovery();
        if(direction==1){elevatorBusyWorld=world;elevatorBusyAt=Time.unscaledTime;}
        if(direction==1){RunPlayers=PartySize;Network!.Send(new Packet{Kind="run",Scene=scene,Floor=floor,World=world,PlayerCount=RunPlayers});}
        var anchor=selected.EnterCollider?selected.EnterCollider.transform.TransformPoint(selected.EnterCollider.center)-Vector3.up*.6f:selected.transform.position;
        if(direction==1)RememberArrival(anchor);
        Network!.Send(new Packet {Kind="elevator",Scene=scene,Floor=floor,World=world,ObjectId=LiftKey(selected),Pose=new[]{anchor.x,anchor.y,anchor.z},Direction=direction,Sequence=++elevatorSequence});
        return true;
    }
    void RenderRemote()
    {
        foreach(var r in remotes.Values)r.Render(PeerMatches(r) && player && r.State?.Health>0);
    }
    void SetPanel(bool value)
    {
        if(panel==value)return;
        OnPanelVisibilityChanged(value);
        panel=value;panelDragging=false;
        UnityEngine.EventSystems.EventSystem.current?.currentInputModule?.DeactivateModule();
        if(!value)nativeUiReleaseGuard=true;
        BeginPanelInputTransition(value);
    }
    void OnGUI()
    {
        DrawModHud();
        if(!PanelVisualVisible&&!PartyWiped&&(!player||!player!.IsPauseMenuOpened&&!player.SecondPauseMenu))
            foreach(var r in remotes.Values)r.DrawLabel();
        DrawSpectatorHud();
        if(!PanelVisualVisible) return;
        DrawModPanel();
        var ev=Event.current;if(ev!=null&&(ev.isMouse||ev.isKey||ev.type==EventType.ScrollWheel))ev.Use();
    }
    void WriteDiagnostics()
    {
        var path=Path.Combine(Paths.BepInExRootPath,"FractureTogether"); Directory.CreateDirectory(path);
        var pos=player ? player!.transform.position : Vector3.zero;
        var file=Path.Combine(path,"status.json");var temp=file+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(new {
            status,detail,scene,floor,world,player=(bool)player,nativeLoading=Object.FindObjectOfType<SceneLoadingManager>()?._isLoading??false,position=new[]{pos.x,pos.y,pos.z},
            connected=Connected,hosting=Network?.Hosting,received=Network?.ReceivedCount,doorSent,doorReceived,elevatorReceived,
            partySize=PartySize,localId=Network?.LocalId,syncedEnemies=SyncedEnemyCount,Down,PartyWiped,rulesReady=RulesReady,Rules,SessionFloor,elevatorMoving=elevator?elevator.isMoving:false,remotes=remotes.Values.Select(r=>new{r.Id,r.State,r.LastSeen,visible=r.Visible,characterModel=r.HasCharacterModel}).ToArray(),
            sameWorld=SameWorld, doors=doors.Keys.ToArray(),drops=DropDiagnostics(),personalSavePending=PersonalSavePending
        },new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temp,file,true);
    }
    void OnDestroy()
    {
        quitting=true;
        if(panel) SetPanel(false); StopSession();

        MenuBackgroundBlur.Shutdown();
        if(Current==this) Current=null;
    }
    void OnApplicationQuit(){quitting=true;try{MenuBackgroundBlur.Shutdown();Plugin.Instance.Profile.Flush();}finally{GameplayLog.Record("game_quit");GameplayLog.Stop();}}
    public bool SceneChange(int index,SceneLoadingManager loader,bool immediate=false){
        Plugin.Instance.Profile.Flush();
        if(PartyWiped)return true;
        if(loader._isLoading){detail="Scene transition is already in progress.";return false;}
        if(!PersonalSceneReady(index,loader,Replay,immediate))return false;
        if(!Connected||Replay||index is not (1 or 2)){
            if(index==2||(index==1&&!Connected)){RunPlayers=1;SessionFloor=index==2?-1:SessionFloor;if(Network!=null)Network.LobbyOpen=true;}
            return true;
        }
        if(Network?.Hosting!=true){detail="Only the host starts or ends the shared run";return false;}
        RunPlayers=index==2?1:PartySize;SessionFloor=index==1?PlayerPrefs.GetInt("currentFloor",0):-1;Network.LobbyOpen=index==2;Network.Send(new Packet{Kind="scene",ItemId=index,PlayerCount=RunPlayers,Floor=SessionFloor,VariantGeneration=WorldVariantSync.SceneGeneration(this,index)});return true;
    }
}

[HarmonyPatch(typeof(SceneLoadingManager),nameof(SceneLoadingManager.LoadScene))]
static class SceneSyncPatch {static bool Prefix(SceneLoadingManager __instance,int sceneIndex)=>Together.Current?.SceneChange(sceneIndex,__instance)??true;}
[HarmonyPatch(typeof(SceneLoadingManager),nameof(SceneLoadingManager.LoadSceneImmediately))]
static class ImmediateSceneSyncPatch {static bool Prefix(SceneLoadingManager __instance,int index)=>Together.Current?.SceneChange(index,__instance,true)??true;}

[HarmonyPatch(typeof(Door),nameof(Door.OpenDoorFast))]
static class DoorPatch
{
    static bool Prefix(Door __instance,int direction,bool byHand,bool byPlayer,out bool __state){__state=__instance&&!__instance.opened&&!__instance.isOpen;var allow=Together.Current?.DoorRequest(__instance,direction,byHand,byPlayer)??true;__state&=allow;return allow;}
    static void Postfix(Door __instance,int direction,bool byHand,bool __state){if(__state)Together.Current?.DoorOpened(__instance,direction,byHand);}
}
[HarmonyPatch(typeof(LevelElevator),nameof(LevelElevator.MoveToNextFloor))]
static class NextFloorPatch { static bool Prefix(LevelElevator __instance) => Together.Current?.ElevatorMove(1,__instance) ?? true; }

[HarmonyPatch]
static class DungeonSeedPatch
{
    public static Dungeon? Latest;public static int BuiltFloor=-1;public static int Revision;static int building;
    public static bool Building=>building>0;
    internal static void Changed(){Revision++;Together.Current?.InvalidateWorldDiscovery();}
    static int previousFloor=int.MinValue,attempt;
    public static void Reset(){previousFloor=int.MinValue;attempt=0;BuiltFloor=-1;Latest=null;}
    static IEnumerable<MethodBase> TargetMethods() => typeof(Dungeon).GetMethods().Where(m=>m.Name=="Build" && m.GetParameters().Length==1);
    sealed class SavedRandom { public UnityEngine.Random.State State;public DungeonArchitect.Builders.Snap.SnapConfig? Config;public int Budget; }
    static void Prefix(Dungeon __instance, out SavedRandom? __state)
    {
        __state=null;
        building++;Changed();
        Latest=__instance;
        BuiltFloor=PlayerPrefs.GetInt("currentFloor",0);
        var current=Together.Current;
        if(current?.Connected!=true || !Plugin.Instance.SharedSeed.Value) return;
        var floor=BuiltFloor;
        if(floor!=previousFloor){previousFloor=floor;attempt=0;}else attempt++;
        var seed=unchecked(current.Network!.Seed+floor*7919+attempt*101);
        __state=new SavedRandom { State=UnityEngine.Random.state };
        var snap=__instance.Config.TryCast<DungeonArchitect.Builders.Snap.SnapConfig>();
        if(snap&&current.RunPlayers>1){__state.Config=snap;__state.Budget=snap.MaxProcessingPower;snap.MaxProcessingPower=Math.Clamp((int)Math.Min(5000000L,(long)snap.MaxProcessingPower*current.RunPlayers*current.RunPlayers),100000,5000000);Plugin.Instance.Log.LogInfo($"Party layout search budget: {__state.Budget}->{snap.MaxProcessingPower}");}
        __instance.Config.Seed=unchecked((uint)seed);
        UnityEngine.Random.InitState(seed);
        PartyScaling.Begin();
        Plugin.Instance.Log.LogInfo("Building shared dungeon seed "+seed+" floor "+floor);
    }
    static Exception? Finalizer(Exception? __exception, SavedRandom? __state)
    {
        building=Math.Max(0,building-1);Changed();
        if(__state!=null){UnityEngine.Random.state=__state.State;if(__state.Config)__state.Config!.MaxProcessingPower=__state.Budget;PartyScaling.End();}
        return __exception;
    }
}

[HarmonyPatch(typeof(Dungeon),"NotifyPostBuild")]
static class FinishedWorldDiscovery {static void Postfix(Dungeon __instance){DungeonSeedPatch.Latest=__instance;DungeonSeedPatch.BuiltFloor=PlayerPrefs.GetInt("currentFloor",0);DungeonSeedPatch.Changed();}}
[HarmonyPatch(typeof(Dungeon),nameof(Dungeon.DestroyDungeon))]
static class DestroyedWorldDiscovery {static void Postfix()=>DungeonSeedPatch.Changed();}
