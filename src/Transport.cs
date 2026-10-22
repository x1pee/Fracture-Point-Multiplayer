using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FractureTogether;

public sealed class EnemyFrame
{
    public int Id {get;set;}
    public string Prefab {get;set;}="";
    public float[] Pose {get;set;}=Array.Empty<float>();
    public float Health {get;set;}
    public bool Dead {get;set;}
    public int Animation {get;set;}
    public float AnimationTime {get;set;}
    public bool Engaged {get;set;}
    public int TargetPeerId {get;set;}=-1;
    public EnemyAnimationFrame? Visual {get;set;}
}

public sealed class UnitFrame
{
 public string Key {get;set;}="";
 public float[] Pose {get;set;}=Array.Empty<float>();
 public float[] Turret {get;set;}=Array.Empty<float>();
 public float Health {get;set;}
 public float Maximum {get;set;}
 public bool Active {get;set;}
 public bool Dead {get;set;}
 public bool Valid(){
  if(Key==null||Key.Length is <1 or >128||Pose==null||Pose.Length!=7||Pose.Take(3).Any(x=>!float.IsFinite(x)||Math.Abs(x)>1000000)||!Rotation(Pose,3)||Turret==null||Turret.Length is not (0 or 8))return false;
  if(Turret.Length==8&&(!Rotation(Turret,0)||!Rotation(Turret,4)))return false;
  return float.IsFinite(Health)&&Health>=0&&Health<=10000000&&float.IsFinite(Maximum)&&Maximum>0&&Maximum<=10000000&&Health<=Maximum*1.01f;
 }
 internal static bool Rotation(float[] pose,int offset){var length=0f;for(var i=offset;i<offset+4;i++){var value=pose[i];if(!float.IsFinite(value)||Math.Abs(value)>1.001f)return false;length+=value*value;}return length is >=.99f and <=1.01f;}
}

// The network layer has no Unity dependencies. Only Update may touch game objects.
public sealed class Packet
{
    public string Kind { get; set; } = "";
    public string Build { get; set; } = "";
    public string Code { get; set; } = "";
    public string Scene { get; set; } = "";
    public string World { get; set; } = "";
    public string Name { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public float[] Pose { get; set; } = Array.Empty<float>();
    public float[] View { get; set; } = Array.Empty<float>();
    public int Floor { get; set; } = -1;
    public int Health { get; set; }
    // Zero means the native health component is not available yet. Never infer
    // a 100 HP maximum from an omitted/unknown field.
    public int MaxHealth { get; set; }
    // Native IDs/enums; -1 means unavailable. Visual state only, never damage.
    public int WeaponId { get; set; } = -1;
    public int Stance { get; set; } = -1;
    public int Motion { get; set; } = -1;
    public int AimState { get; set; }
    public long ThrowSequence { get; set; }
    public long ReloadSequence { get; set; }
    public int ReloadKind { get; set; }
    public float ReloadElapsed { get; set; }
    // Host run serial: scene-start/variants only; independent of room readiness.
    public long VariantGeneration { get; set; }
    public int Seed { get; set; }
    public int Direction { get; set; }
    public long Sequence { get; set; }
    public bool ByHand { get; set; }
    public bool Firing { get; set; }
    public int PlayerId { get; set; }
    public int TargetId { get; set; } = -1;
    public int PlayerCount { get; set; } = 1;
    public float Amount { get; set; }
    public int ItemId { get; set; }
    public int Count { get; set; }
    public int Category { get; set; }
    public bool Success { get; set; }
    public EnemyFrame[] Enemies {get;set;}=Array.Empty<EnemyFrame>();
    public UnitFrame[] Units {get;set;}=Array.Empty<UnitFrame>();
    public ProjectileFrame[] Projectiles {get;set;}=Array.Empty<ProjectileFrame>();
    public LootEntry[] Contents {get;set;}=Array.Empty<LootEntry>();
    public ModRules? Rules {get;set;}
    public EnemyBodyFrame? Body {get;set;}
    public ItemDropFrame? Drop {get;set;}
    public ShotFrame? Shot {get;set;}
    public EnemyShotFrame? EnemyShot {get;set;}
    public OutfitEntry[] Outfit {get;set;}=Array.Empty<OutfitEntry>();
}

public sealed class Session : IDisposable
{
    public const string Protocol = "FT/19";
    public const int MaxFrame = 16384;
    public const int MaxPlayers = 4;
    readonly ConcurrentQueue<(Packet Packet,bool Counted)> Incoming = new();
    readonly ConcurrentDictionary<int,Packet> incomingStates = new();
    readonly ConcurrentDictionary<int,Packet> incomingEnemies = new();
    readonly ConcurrentDictionary<string,Packet> incomingSodas = new();
    public readonly ConcurrentQueue<string> Events = new();
    public readonly ConcurrentQueue<ConnectionIssue> Failures = new();
    // Optional observer; may be called on network threads. Never pass packet
    // contents, addresses, nicknames, credentials or exception messages here.
    public Action<string,object?>? Diagnostic;
    readonly CancellationTokenSource cancel = new();
    readonly string build, code, nickname;
    readonly object gate = new();
    readonly Dictionary<int,Link> links = new();
    readonly HashSet<TcpClient> pending = new();
    TcpListener? listener;
    int nextId, received, playerCount=1,rejecting;
    long receivedCount;
    public bool Hosting { get; private set; }
    public bool Connected { get {lock(gate) return links.Count>0;} }
    public int LocalId {get;private set;}
    public int PlayerCount => Volatile.Read(ref playerCount);
    public int[] ConnectedPeerIds {get{lock(gate)return links.Keys.OrderBy(id=>id).ToArray();}}
    public int Seed {get;private set;}
    public volatile bool LobbyOpen=true;
    public long ReceivedCount => Interlocked.Read(ref receivedCount);
    sealed class Link : IDisposable
    {
        public readonly TcpClient Client;
        public readonly int Id;
        public readonly ConcurrentQueue<byte[]> Outgoing = new();
        public readonly ConcurrentDictionary<int,byte[]> States = new();
        public readonly ConcurrentDictionary<int,byte[]> Enemies = new();
        public readonly ConcurrentDictionary<string,byte[]> Sodas = new();
        public readonly SemaphoreSlim Signal = new(0);
        public int Queued,FailureReported;
        public volatile bool Open=true;
        public Link(TcpClient client,int id){Client=client;Id=id;}
        public void Dispose(){Open=false;Client.Dispose();Signal.Release();}
    }
    public Session(string build,string code,string nickname){this.build=build;this.code=code;this.nickname=NicknameRules.Clean(nickname);_=Task.Run(Heartbeat);}
    async Task Heartbeat(){try{while(!cancel.IsCancellationRequested){await Task.Delay(1000,cancel.Token);Send(new Packet{Kind="ping"});}}catch(OperationCanceledException){}catch(Exception e){Report(e);}}
    public void Host(int port)
    {
        if(Diagnostic!=null)Diagnose("connect_start",new{role="host"});
        if(port is <1024 or >65535)throw new ConnectionException(ConnectionErrors.Get("MP-014","host_start"));
        Hosting=true;LocalId=0;Seed=RandomNumberGenerator.GetInt32(1,int.MaxValue);
        try{listener=new TcpListener(IPAddress.Any,port);listener.Start(4);}
        catch(Exception error){Hosting=false;throw new ConnectionException(ConnectionErrors.From(error,"host_start"));}
        if(Diagnostic!=null)Diagnose("host_start",new{port,maxPlayers=MaxPlayers});
        Events.Enqueue("Listening on TCP "+port+" (up to 4 players)");
        _=Task.Run(AcceptLoop);
    }
    public void Join(string host,int port)
    {
        if(Diagnostic!=null)Diagnose("connect_start",new{role="client"});
        if(string.IsNullOrWhiteSpace(host)||host.Length>128||host.Any(char.IsWhiteSpace)||host.Contains('/')||host.Contains('\\')||port is <1024 or >65535){Report(new ConnectionException(ConnectionErrors.Get("MP-014","connect")),"connect");return;}
        _=Task.Run(async()=>{
            var client=new TcpClient();var phase="connect";
            try{lock(gate)pending.Add(client);await client.ConnectAsync(host,port).WaitAsync(TimeSpan.FromSeconds(8),cancel.Token);phase="handshake";await Authenticate(client,false);}
            catch(Exception e){Report(e,phase);client.Dispose();}
            finally{lock(gate)pending.Remove(client);}
        });
    }
    async Task AcceptLoop()
    {
        while(!cancel.IsCancellationRequested){
            try{
                var client=await listener!.AcceptTcpClientAsync().WaitAsync(cancel.Token);
                string rejection;lock(gate){rejection=!LobbyOpen?"MP-006":links.Count+pending.Count>=3?"MP-005":"";if(rejection.Length==0)pending.Add(client);}
                if(rejection.Length>0){if(Interlocked.Increment(ref rejecting)>8){Interlocked.Decrement(ref rejecting);client.Dispose();}else _=RejectBusy(client,rejection);continue;}
                _=Task.Run(async()=>{
                    try{await Authenticate(client,true);}catch(Exception e){Report(e,"handshake");client.Dispose();}
                    finally{lock(gate)pending.Remove(client);}
                });
            }catch(Exception e){Report(e);}
        }
    }
    async Task RejectBusy(TcpClient client,string code){
        try{using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);timeout.CancelAfter(1000);await Write(client.GetStream(),new Packet{Kind="reject",Code=code,Build=Protocol+":"+build},timeout.Token);}
        catch{/* A rejected peer cannot hold an active slot or stop the listener. */}
        finally{client.Dispose();Interlocked.Decrement(ref rejecting);}
    }
    async Task Reject(NetworkStream stream,string code,CancellationToken token){
        await Write(stream,new Packet{Kind="reject",Code=code,Build=Protocol+":"+build},token);
        throw new ConnectionException(ConnectionErrors.Get(code,"handshake"));
    }
    void CheckRejection(Packet packet){
        if(packet.Kind!="reject")return;
        // Only fixed, supported pre-authentication reasons may be presented.
        var code=packet.Code is "MP-001" or "MP-002" or "MP-003" or "MP-004" or "MP-005" or "MP-006" or "MP-013"?packet.Code:"MP-013";
        var issue=code is "MP-001" or "MP-002"?ConnectionErrors.Compatibility(Protocol+":"+build,packet.Build):null;
        throw new ConnectionException(issue??ConnectionErrors.Get(code,"handshake"));
    }
    async Task Authenticate(TcpClient client,bool host)
    {
        client.NoDelay=true;var stream=client.GetStream();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);timeout.CancelAfter(5000);
        var id=0;
        if(host){
            var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await Write(stream,new Packet{Kind="challenge",Build=Protocol+":"+build,Code=nonce,Success=code.Length>0},timeout.Token);
            var hello=await Read(stream,timeout.Token);var expected=Auth(nonce);
            if(hello.Kind!="hello")await Reject(stream,"MP-013",timeout.Token);
            var compatibility=ConnectionErrors.Compatibility(Protocol+":"+build,hello.Build);
            if(compatibility!=null)await Reject(stream,compatibility.Code,timeout.Token);
            if(hello.Code.Length!=expected.Length||!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hello.Code),Encoding.ASCII.GetBytes(expected)))await Reject(stream,"MP-003",timeout.Token);
            id=Interlocked.Increment(ref nextId);
            await Write(stream,new Packet{Kind="welcome",Seed=Seed,PlayerId=id,Name=nickname},timeout.Token);
        }else{
            var challenge=await Read(stream,timeout.Token);
            CheckRejection(challenge);
            if(challenge.Kind!="challenge"||challenge.Code.Length!=48)throw new ConnectionException(ConnectionErrors.Get("MP-013","handshake"));
            var compatibility=ConnectionErrors.Compatibility(Protocol+":"+build,challenge.Build);
            if(compatibility!=null)throw new ConnectionException(compatibility);
            if(challenge.Success&&code.Length==0)throw new ConnectionException(ConnectionErrors.Get("MP-004","handshake"));
            // Open lobbies authenticate the build/nonce using the empty secret,
            // regardless of a stale password retained in the client's settings.
            await Write(stream,new Packet{Kind="hello",Build=Protocol+":"+build,Code=Auth(challenge.Code,challenge.Success?code:""),Name=nickname},timeout.Token);
            var welcome=await Read(stream,timeout.Token);
            CheckRejection(welcome);
            if(welcome.Kind!="welcome"||welcome.Seed<=0||welcome.PlayerId<=0)throw new ConnectionException(ConnectionErrors.Get("MP-013","handshake"));
            LocalId=welcome.PlayerId;Seed=welcome.Seed;
        }
        var link=new Link(client,id);
        lock(gate){if(cancel.IsCancellationRequested||(host&&!LobbyOpen)){client.Dispose();return;}pending.Remove(client);links.Add(id,link);if(host)playerCount=links.Count+1;}
        if(Diagnostic!=null)Diagnose("peer_authenticated",new{peerId=id,localId=LocalId,playerCount=PlayerCount});
        Task? writer=null;var announced=false;
        try{
            // Once registered, every failure owns the same cleanup path,
            // including a full game queue before the writer starts.
            Enqueue(new Packet{Kind="joined",PlayerId=id});announced=true;
            if(host)Roster();
            Events.Enqueue("Peer connected: "+id+"; session seed "+Seed);
            writer=Writer(link,stream);
            var interval=Environment.TickCount64;var messages=0;
            while(!cancel.IsCancellationRequested&&link.Open){
                using var idle=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);idle.CancelAfter(12000);
                var packet=await Read(stream,idle.Token);
                if(Environment.TickCount64-interval>1000){interval=Environment.TickCount64;messages=0;}
                if(++messages>800){if(Diagnostic!=null)Diagnose("rateinvalid",new{peerId=id,count=messages,limit=800});throw new IOException("Packet rate limit");}
                if(host){
                    if(packet.Kind is not ("state" or "appearance" or "shot" or "door" or "ping" or "pianoreq" or "lootreq" or "lootack" or "hitreq" or "unitreq" or "grenreq" or "ready" or "rulesack" or "pvpreq" or "down" or "buttonreq" or "bossdoorreq" or "vendreq" or "vendpaid" or "foodreq" or "foodack" or "triggerreq" or "dropreq" or "variantreq")){if(Diagnostic!=null)Diagnose("packetinvalid",new{peerId=id,reason="client_kind"});throw new IOException("Client sent unauthorized message");}
                    packet.PlayerId=id; // Identity always comes from the authenticated connection.
                }else if(packet.Kind is not ("state" or "appearance" or "shot" or "enemyshot" or "door" or "ping" or "pianonote" or "elevator" or "roster" or "left" or "lootgrant" or "loottaken" or "lootreject" or "lootrebind" or "enemy" or "unit" or "projectile" or "projectilecredit" or "hurt" or "run" or "ready" or "scene" or "killcredit" or "hitresult" or "rules" or "playerdown" or "wipe" or "revive" or "vendgrant" or "vendspawn" or "vendpose" or "foodgrant" or "foodstate" or "foodpose" or "triggerdone" or "bossdoorstate" or "enemybody" or "enemyspeech" or "dropspawn" or "droppose" or "dropreject" or "worldvariant")){if(Diagnostic!=null)Diagnose("packetinvalid",new{peerId=id,reason="host_kind"});throw new IOException("Unknown host message");}
                if(packet.Kind=="roster"){playerCount=packet.PlayerCount;continue;}
                if(packet.Kind=="ping")continue;
                Enqueue(packet);Interlocked.Increment(ref receivedCount);
            }
        }catch(Exception error){ReportLink(link,error,"session");}finally{
            link.Dispose();
            lock(gate){links.Remove(id);if(host)playerCount=links.Count+1;else playerCount=1;}
            if(Diagnostic!=null)Diagnose("peer_disconnected",new{peerId=id,localId=LocalId,playerCount=PlayerCount,announced});
            // Only local lifecycle cleanup bypasses the reliable-message cap.
            // A new departure requires an admitted joined message (one normal
            // slot), so blocked-game churn is bounded by 1024 joins + 3 peers.
            // An unannounced link cannot have delivered gameplay messages.
            if(announced)Incoming.Enqueue((new Packet{Kind="left",PlayerId=id},false));
            if(host){if(announced)Relay(new Packet{Kind="left",PlayerId=id});Roster();}
            if(announced)Events.Enqueue("Peer disconnected: "+id);
            if(writer!=null)try{await writer;}catch{}
        }
    }
    // Snapshot and enqueue under the same gate: concurrent joins must not send an older count after a newer one.
    void Roster(){lock(gate)Relay(new Packet{Kind="roster",PlayerCount=links.Count+1});}
    void Enqueue(Packet p){
        // Loading can block Unity for many seconds. Keep only the latest pose per peer while preserving reliable events.
        if(p.Kind=="state"){if(!incomingStates.ContainsKey(p.PlayerId)&&incomingStates.Count>=64){if(Diagnostic!=null)Diagnose("recvqueueoverflow",new{queue="states",peerId=p.PlayerId,count=incomingStates.Count,limit=64});throw new ConnectionException(ConnectionErrors.Get("MP-017","receive"));}incomingStates[p.PlayerId]=p;return;}
        if(p.Kind is "enemy" or "unit" or "projectile"){incomingEnemies[p.Category+(p.Kind=="projectile"?202:p.Kind=="unit"?101:0)]=p;return;}
        if(p.Kind is "vendpose" or "droppose" or "foodpose" or "enemybody" or "bossdoorstate"){var key=SnapshotKey(p);if(!incomingSodas.ContainsKey(key)&&incomingSodas.Count>=2048){if(Diagnostic!=null)Diagnose("recvqueueoverflow",new{queue="objects",peerId=p.PlayerId,count=incomingSodas.Count,limit=2048});throw new ConnectionException(ConnectionErrors.Get("MP-017","receive"));}incomingSodas[key]=p;return;}
        var queued=Interlocked.Increment(ref received);
        if(queued>1024){Interlocked.Decrement(ref received);if(Diagnostic!=null)Diagnose("recvqueueoverflow",new{queue="reliable",peerId=p.PlayerId,count=queued,limit=1024});throw new ConnectionException(ConnectionErrors.Get("MP-017","receive"));}Incoming.Enqueue((p,true));
    }
    public bool TryReceive(out Packet p){
        if(Incoming.TryDequeue(out var item)){p=item.Packet;if(item.Counted)Interlocked.Decrement(ref received);return true;}
        foreach(var id in incomingStates.Keys)if(incomingStates.TryRemove(id,out p!))return true;
        foreach(var id in incomingEnemies.Keys)if(incomingEnemies.TryRemove(id,out p!))return true;
        foreach(var id in incomingSodas.Keys)if(incomingSodas.TryRemove(id,out p!))return true;
        p=null!;return false;
    }
    public void Send(Packet p){p.PlayerId=LocalId;Relay(p);}
    public void SendTo(Packet p,int target){p.PlayerId=LocalId;Relay(p,-1,target);}
    public void Relay(Packet p,int exclude=-1,int target=-1)
    {
        if(cancel.IsCancellationRequested)return;
        var bytes=JsonSerializer.SerializeToUtf8Bytes(p);if(bytes.Length>MaxFrame){if(Diagnostic!=null)Diagnose("packetinvalid",new{reason="outgoing_size",count=bytes.Length,limit=MaxFrame});throw new IOException("Outgoing packet too large");}
        lock(gate)foreach(var l in links.Values){
            if(l.Id==exclude||(target>=0&&l.Id!=target))continue;
            if(p.Kind=="state"){l.States[p.PlayerId]=bytes;l.Signal.Release();continue;}
            if(p.Kind is "enemy" or "unit" or "projectile"){l.Enemies[p.Category+(p.Kind=="projectile"?202:p.Kind=="unit"?101:0)]=bytes;l.Signal.Release();continue;}
            if(p.Kind is "vendpose" or "droppose" or "foodpose" or "enemybody" or "bossdoorstate"){var key=SnapshotKey(p);if(!l.Sodas.ContainsKey(key)&&l.Sodas.Count>=2048){if(Diagnostic!=null)Diagnose("sendqueueoverflow",new{queue="objects",peerId=l.Id,count=l.Sodas.Count,limit=2048});ReportLink(l,new ConnectionException(ConnectionErrors.Get("MP-017","send")),"send");l.Dispose();continue;}l.Sodas[key]=bytes;l.Signal.Release();continue;}
            var queued=Interlocked.Increment(ref l.Queued);
            if(queued>256){Interlocked.Decrement(ref l.Queued);if(Diagnostic!=null)Diagnose("sendqueueoverflow",new{queue="reliable",peerId=l.Id,count=queued,limit=256});ReportLink(l,new ConnectionException(ConnectionErrors.Get("MP-017","send")),"send");l.Dispose();continue;}
            l.Outgoing.Enqueue(bytes);l.Signal.Release();
        }
    }
    async Task Writer(Link link,NetworkStream stream)
    {
        try{while(link.Open&&!cancel.IsCancellationRequested){await link.Signal.WaitAsync(cancel.Token);while(link.Outgoing.TryDequeue(out var bytes)){
            Interlocked.Decrement(ref link.Queued);using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);deadline.CancelAfter(5000);await WriteBytes(stream,bytes,deadline.Token);
        }
        foreach(var id in link.States.Keys)if(link.States.TryRemove(id,out var state)){
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);deadline.CancelAfter(5000);await WriteBytes(stream,state,deadline.Token);
        }
        foreach(var id in link.Enemies.Keys)if(link.Enemies.TryRemove(id,out var enemies)){
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);deadline.CancelAfter(5000);await WriteBytes(stream,enemies,deadline.Token);
        }
        foreach(var id in link.Sodas.Keys)if(link.Sodas.TryRemove(id,out var soda)){
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);deadline.CancelAfter(5000);await WriteBytes(stream,soda,deadline.Token);
        }
        }}catch(Exception error){ReportLink(link,error,"writer");link.Dispose();}
    }
    string Auth(string nonce,string? secret=null)=>Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret??code),Encoding.ASCII.GetBytes(nonce+":"+Protocol+":"+build)));
    public static async Task<Packet> Read(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await Exact(stream, header, token);
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if(count < 2 || count > MaxFrame) throw new IOException("Invalid frame size");
        var bytes = new byte[count];
        await Exact(stream, bytes, token);
        var p = JsonSerializer.Deserialize<Packet>(bytes) ?? throw new IOException("Empty packet");
        if(p.Kind == null || p.Kind.Length > 24 || p.Build == null || p.Build.Length > 160 || p.Code == null || p.Code.Length > 128 ||
            p.Name == null || p.Name.Length>0&&!NicknameRules.IsValid(p.Name) || p.Scene == null || p.Scene.Length > 160 || p.World == null || p.World.Length > 128 ||
            p.ObjectId == null || p.ObjectId.Length > 1024 || p.MachineId==null || p.MachineId.Length>128 || p.Pose == null || p.Pose.Length > 8 ||
            p.Pose.Any(x => !float.IsFinite(x) || Math.Abs(x) > 1000000) || p.Floor < -1 || p.Floor > 100000 ||
            p.Health < -10000 || p.Health > 10000 || p.MaxHealth < 0 || p.MaxHealth > 10000 || p.WeaponId < -1 || p.WeaponId > 100000 || p.Stance < -1 || p.Stance > 2 || p.Motion < -1 || p.Motion > 4 || p.Direction < -1 || p.Direction > 1 || p.PlayerId<0 || p.PlayerId>1000000 ||
            p.TargetId < -1 || p.TargetId>1000000 || p.PlayerCount<1 || p.PlayerCount>4 || !float.IsFinite(p.Amount) || Math.Abs(p.Amount)>10000000 ||
            p.ItemId<0 || p.ItemId>100000 || p.Count<0 || p.Count>10000 || p.Category<0 || p.Category>100)
            throw new IOException("Invalid packet fields");
        if((p.Kind is "hello" or "welcome" or "state")&&!NicknameRules.IsValid(p.Name))throw new IOException("Invalid nickname: use 1-20 visible Unicode characters.");
        if(p.View==null||p.View.Length is not (0 or 8)||p.View.Length>0&&(p.Kind!="state"||p.View.Any(v=>!float.IsFinite(v)||Math.Abs(v)>1000000)||!UnitFrame.Rotation(p.View,3)||p.View[7]<10||p.View[7]>140))throw new IOException("Invalid spectator view");
        if(p.Kind!="state"&&p.MaxHealth!=0)throw new IOException("Player maximum health in unrelated packet");
        if(p.Kind!="state"&&(p.WeaponId!=-1||p.Stance!=-1||p.Motion!=-1))throw new IOException("Player visual state in unrelated packet");
        if(p.AimState<0||p.AimState>3||p.ThrowSequence<0||p.Kind!="state"&&(p.AimState!=0||p.ThrowSequence!=0))throw new IOException("Invalid player action state");
        if(!ReloadPresentationPolicy.Valid(p.ReloadSequence,p.ReloadKind,p.ReloadElapsed,p.WeaponId)||p.Kind!="state"&&(p.ReloadSequence!=0||p.ReloadKind!=0||p.ReloadElapsed!=0))throw new IOException("Invalid reload state");
        if(p.MaxHealth>0&&p.Health>p.MaxHealth)throw new IOException("Player health exceeds its known maximum");
        if(p.Enemies==null||p.Enemies.Length>12||p.Enemies.Any(e=>e==null||e.Id<=0||e.Id>1000000||e.TargetPeerId < -1||e.TargetPeerId>1000000||e.Prefab==null||e.Prefab.Length>128||e.Pose==null||e.Pose.Length!=4||e.Pose.Any(x=>!float.IsFinite(x)||Math.Abs(x)>1000000)||!float.IsFinite(e.Health)||Math.Abs(e.Health)>10000000||!float.IsFinite(e.AnimationTime)||Math.Abs(e.AnimationTime)>1000000))throw new IOException("Invalid enemy snapshot");
        if(p.Kind!="enemy"&&p.Enemies.Length!=0||p.Enemies.Any(e=>e.Visual!=null&&!e.Visual.Valid()))throw new IOException("Invalid enemy visual data");
        if(!OutfitEntry.Validate(p.Outfit)||p.Kind is not ("state" or "appearance")&&p.Outfit.Length!=0)throw new IOException("Invalid outfit");
        if(p.Kind=="appearance"&&(p.Outfit.Length==0||p.Sequence<=0))throw new IOException("Invalid appearance message");
        if((p.Kind is "pianoreq" or "pianonote")&&!PianoNoteWire.Valid(p))throw new IOException("Invalid piano note");
        if((p.Kind is "foodreq" or "foodack" or "foodgrant" or "foodstate" or "foodpose")&&!FoodWire.Valid(p))throw new IOException("Invalid shared food message");
        if((p.Kind is "bossdoorreq" or "bossdoorstate")&&!BossDoorWire.Valid(p))throw new IOException("Invalid boss door message");
        if(p.Shot!=null&&(p.Kind!="shot"||!p.Shot.Valid())||p.Kind=="shot"&&(p.Shot==null||p.Sequence<=0))throw new IOException("Invalid shot message");
        if(p.EnemyShot!=null&&(p.Kind!="enemyshot"||!p.EnemyShot.Valid())||p.Kind=="enemyshot"&&(p.EnemyShot==null||p.Sequence<=0))throw new IOException("Invalid enemy shot message");
        if(p.VariantGeneration<0||p.VariantGeneration>1000000||p.Kind is not ("scene" or "variantreq" or "worldvariant")&&p.VariantGeneration!=0)throw new IOException("Invalid world variant generation");
        if(p.Kind=="scene"&&p.ItemId==1&&p.VariantGeneration<=0)throw new IOException("Missing world variant generation");
        if(p.Kind is "variantreq" or "worldvariant"&&!WorldVariantWire.Valid(p))throw new IOException("Invalid world variant decision");
        if(p.Kind=="lootreq"&&p.Pose.Length is not (0 or 3))throw new IOException("Invalid loot request position");
        if(p.Kind=="lootrebind"&&(p.Pose.Length!=3||p.Category!=0||p.Count!=0||p.TargetId<1||p.ObjectId==p.MachineId||!LootKey(p.ObjectId)||!LootKey(p.MachineId)))throw new IOException("Invalid canonical loot binding");
        if(p.Body!=null&&(p.Kind!="enemybody"||!p.Body.Valid())||p.Kind=="enemybody"&&(p.Body==null||p.Sequence<=0))throw new IOException("Invalid enemy body");
        if(p.Drop!=null&&(p.Kind is not ("dropreq" or "dropspawn")||!p.Drop.Valid())||p.Kind is "dropreq" or "dropspawn"&&p.Drop==null)throw new IOException("Invalid item drop");
        if(p.Kind is "dropreq" or "dropspawn" or "droppose"&&(p.Pose.Length!=7||!UnitFrame.Rotation(p.Pose,3)||!p.ObjectId.StartsWith("drop:",StringComparison.Ordinal)))throw new IOException("Invalid drop pose");
        if(p.Kind=="enemyspeech"&&(p.ItemId<=0||p.Sequence<=0||p.ObjectId.Length==0||p.Amount<0||p.Amount>1))throw new IOException("Invalid enemy speech");
        if(p.Units==null||p.Units.Length>12||p.Units.Any(u=>u==null||!u.Valid())||p.Units.Select(u=>u.Key).Distinct(StringComparer.Ordinal).Count()!=p.Units.Length||p.Kind=="unit"&&p.Units.Length==0)throw new IOException("Invalid ballistic unit snapshot");
        if(p.Kind!="unit"&&p.Units.Length!=0)throw new IOException("Unit data in unrelated packet");
        if(p.Kind=="unitreq"&&(p.Pose.Length!=3||p.ObjectId.Length==0||p.Sequence<=0||p.Amount<=0||p.Amount>1000000))throw new IOException("Invalid ballistic unit request");
        if(p.Projectiles==null||p.Projectiles.Length>8||p.Projectiles.Any(f=>f==null||!f.Valid())||p.Projectiles.Select(f=>f.Key).Distinct(StringComparer.Ordinal).Count()!=p.Projectiles.Length)throw new IOException("Invalid projectile snapshot");
        if(p.Kind is not ("projectile" or "grenreq")&&p.Projectiles.Length!=0)throw new IOException("Projectile data in unrelated packet");
        if(p.Kind=="grenreq"&&(p.Projectiles.Length!=1||p.Sequence<=0||p.Projectiles[0].Exploded)||p.Kind=="projectile"&&p.Projectiles.Length==0)throw new IOException("Invalid projectile message");
        if(p.Contents==null||p.Contents.Length>128||p.Contents.Any(e=>e==null||e.Type<0||e.Type>8||e.Index<0||e.Index>100000))throw new IOException("Invalid loot contents");
        if(p.Kind is not ("lootgrant" or "lootack" or "loottaken")&&p.Contents.Length!=0)throw new IOException("Loot contents in unrelated packet");
        if(p.Rules!=null&&(p.Kind!="rules"||!p.Rules.Valid()))throw new IOException("Invalid shared rules");
        if(p.Kind=="rules"&&p.Rules==null)throw new IOException("Missing shared rules");
        return p;
    }
    static bool LootKey(string value)=>value.Length==64&&value.All(ch=>ch is >= '0' and <= '9' or >= 'A' and <= 'F');
    static async Task Exact(NetworkStream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while(offset < bytes.Length) { var n = await stream.ReadAsync(bytes.AsMemory(offset), token); if(n == 0) throw new EndOfStreamException(); offset += n; }
    }
    public static Task Write(NetworkStream stream, Packet p, CancellationToken token) => WriteBytes(stream, JsonSerializer.SerializeToUtf8Bytes(p), token);
    static async Task WriteBytes(NetworkStream stream, byte[] bytes, CancellationToken token)
    {
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
    void Diagnose(string eventCode,object? data){var callback=Diagnostic;if(callback==null)return;try{callback(eventCode,data);}catch{/* Observability never changes transport failure or cleanup behavior. */}}
    static string SocketFailure(Exception error){for(Exception? cause=error;cause!=null;cause=cause.InnerException)if(cause is SocketException socket)return socket.SocketErrorCode.ToString();return "";}
    void ReportLink(Link link,Exception error,string phase){if(Interlocked.Exchange(ref link.FailureReported,1)==0)Report(error,phase,link.Id);}
    void Report(Exception e,string phase="network",int peerId=-1){if(!cancel.IsCancellationRequested){var issue=ConnectionErrors.From(e,phase);if(Failures.Count<32)Failures.Enqueue(issue);if(Diagnostic!=null)Diagnose("connection_failure",new{code=issue.Code,phase=issue.Phase,failureType=e.GetType().Name,socketError=SocketFailure(e),peerId,versions=issue.Versions});if(Events.Count<64)Events.Enqueue(issue.Code+": "+issue.Title);}}
    static string SnapshotKey(Packet p)=>p.Kind+":"+(p.Kind=="enemybody"?p.Body!.EnemyId+":"+p.Body.Offset:p.ObjectId);
    public void Dispose(){cancel.Cancel();listener?.Stop();lock(gate){foreach(var l in links.Values)l.Dispose();foreach(var c in pending)c.Dispose();}playerCount=1;}
}
