using HarmonyLib;
using EnemyAI;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;
namespace FractureTogether;

// Native explosive simulation runs on the host. Clients only own their throw inventory.
public sealed partial class Together
{
 sealed class ProjectileEntry {
  public Grenade Actor=null!;public string Key="",Prefab="",Epoch="";public int Owner=-1;public long Request,Sequence;
  public float Born,ExplodedAt=-1,RetryAt,DamageScale=1,InstantKill,RetainUntil;public bool NativeComplete,DirectSent;public IntPtr Iterator;public ProjectileFrame? Frame;public Ballistics.Weapon? OwnedWeapon;
 }
 readonly Dictionary<string,ProjectileEntry> projectiles=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,ProjectileEntry> projectileActors=new();
 readonly Dictionary<string,Grenade> projectilePrefabs=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,string> projectileTemplates=new();
 readonly Dictionary<(int,long),string> throwReceipts=new();
 readonly Dictionary<int,float> lastThrows=new();
 readonly Dictionary<IntPtr,(int Owner,float Scale,float Instant,float Until)> fragmentOwners=new();
 readonly HashSet<long> projectileCredits=new();
 readonly HashSet<string> projectileTombstones=new(StringComparer.Ordinal);
 readonly Dictionary<IntPtr,Grenade> orphanProjectiles=new();
 sealed class DeferredProjectile {public ProjectileFrame Frame=null!;public long Sequence;public float Expires,RetryAt;}
 readonly Dictionary<string,DeferredProjectile> deferredProjectiles=new(StringComparer.Ordinal);readonly Queue<string> deferredProjectileKeys=new();
 string projectileEpoch="";long projectileId,throwSequence,projectileSequence,projectileCreditSequence;float projectileSendAt,projectileCatalogueRefreshAt;int projectilePrefabReports,projectilePrefabFinalReports;
 static int projectileCloneDepth;
 static GameObject? silentExplosionAudio,emptyExplosionVisual;
 static void EnsureExplosionEffects(Grenade actor){
  // Some native destructible prefabs omit their audio object. Native MoveNext
  // dereferences it before applying NPC damage; repair only this runtime instance.
  if(!actor.AudioPrefab||!actor.AudioPrefab.GetComponent<AudioSource>()){
   if(!silentExplosionAudio){silentExplosionAudio=new GameObject("Coop silent explosion audio fallback");var audio=silentExplosionAudio.AddComponent<AudioSource>();audio.playOnAwake=false;audio.volume=0;Object.DontDestroyOnLoad(silentExplosionAudio);}
   actor.AudioPrefab=silentExplosionAudio;
  }
  if(!actor.Explosion){if(!emptyExplosionVisual){emptyExplosionVisual=new GameObject("Coop empty explosion visual fallback");Object.DontDestroyOnLoad(emptyExplosionVisual);}actor.Explosion=emptyExplosionVisual;}
 }
 string ProjectileEpoch=>Network==null?"":Network.Seed+"|"+UnityEngine.SceneManagement.SceneManager.GetActiveScene().name+"|"+DungeonSeedPatch.BuiltFloor;
 [ThreadStatic] internal static int? ProjectileOwner;
 [ThreadStatic] internal static int projectileDamageDepth;
 [HideFromIl2Cpp]
 public object ProjectileDiagnostics()=>projectiles.Values.Select(e=>new{e.Key,e.Prefab,e.Owner,e.Request,e.ExplodedAt,e.NativeComplete,e.DirectSent,e.RetainUntil,audio=e.Actor&&e.Actor.AudioPrefab,visual=e.Actor&&e.Actor.Explosion,weapon=e.Actor&&e.Actor.fragmentBallistics?e.Actor.fragmentBallistics.Pointer.ToInt64():0,active=e.Actor&&e.Actor.gameObject.activeInHierarchy,pose=e.Actor?e.Actor.transform.position.ToString():null}).ToArray();
 void ClearProjectiles(){
  var bullets=Ballistics.BulletHandler.instance;if(bullets&&bullets.Bullets!=null&&fragmentOwners.Count>0){var count=bullets.Bullets.Count;for(var i=0;i<count;i++){var bullet=bullets.Bullets.Dequeue();if(bullet.parentWeapon!=null&&fragmentOwners.ContainsKey(bullet.parentWeapon.Pointer)){if(bullet.BulletTrans)bullet.BulletTrans.gameObject.SetActive(false);}else bullets.Bullets.Enqueue(bullet);}}
  foreach(var e in projectiles.Values){if(e.Actor){e.Actor.StopAllCoroutines();Object.Destroy(e.Actor.gameObject);}if(e.OwnedWeapon)Object.Destroy(e.OwnedWeapon.gameObject);}
  foreach(var g in orphanProjectiles.Values)if(g)Object.Destroy(g.gameObject);orphanProjectiles.Clear();
  projectiles.Clear();projectileActors.Clear();throwReceipts.Clear();lastThrows.Clear();fragmentOwners.Clear();projectileCredits.Clear();projectileTombstones.Clear();deferredProjectiles.Clear();deferredProjectileKeys.Clear();projectileEpoch="";projectileCatalogueRefreshAt=0;projectilePrefabReports=projectilePrefabFinalReports=0;
  // Sequence counters are session-monotonic, including after a floor transition.
 }
 static string GrenadeSignature(Grenade g)=>FormattableString.Invariant($"{g.name.Replace("(Clone)","").Trim()}|{(int)g.type}|{g.fragmentCount}|{g.timeToExplode:R}|{g.killDistance:R}|{(g.fragmentBallistics?g.fragmentBallistics.name.Replace("(Clone)","").Trim():"")}");
 static GameObject? ProjectilePrefabObject(Grenade g){if(!g)return null;var source=g.gameObject;return source&&!source.scene.IsValid()?source:null;}
 void BuildProjectileCatalogue(bool refresh=false){
  if(projectilePrefabs.Count>0&&!refresh)return;
  if(Time.unscaledTime<projectileCatalogueRefreshAt)return;projectileCatalogueRefreshAt=Time.unscaledTime+2;
  // Native assets can unload between floors while their managed wrappers and
  // dictionary keys survive. Remove dead values before selecting replacements.
  foreach(var old in projectilePrefabs.ToArray())if(!ProjectilePrefabObject(old.Value)){projectilePrefabs.Remove(old.Key);if(old.Value is not null)projectileTemplates.Remove(old.Value.Pointer);}
  foreach(var g in Resources.FindObjectsOfTypeAll<Grenade>())if(ProjectilePrefabObject(g)){
   var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(GrenadeSignature(g))))[..32];
   if(!projectilePrefabs.TryGetValue(key,out var prior)||!prior)projectilePrefabs[key]=g;projectileTemplates[g.Pointer]=key;
  }
 }
 bool TryProjectilePrefab(string key,out Grenade prefab){
  if(projectilePrefabs.TryGetValue(key,out prefab!)&&ProjectilePrefabObject(prefab))return true;
  if(prefab is not null)projectileTemplates.Remove(prefab.Pointer);projectilePrefabs.Remove(key);BuildProjectileCatalogue(true);
  return projectilePrefabs.TryGetValue(key,out prefab!)&&ProjectilePrefabObject(prefab);
 }
 string ProjectileTemplate(Grenade g){BuildProjectileCatalogue();if(projectileTemplates.TryGetValue(g.Pointer,out var known)&&TryProjectilePrefab(known,out _))return known;
  var signature=GrenadeSignature(g);var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..32];
  return TryProjectilePrefab(key,out _)?key:"";
 }
 void RememberFragments(ProjectileEntry e){if(!e.Actor||!e.Actor.fragmentBallistics)return;
  // Environmental explosion prefabs can reference the same external Weapon. Give each
  // detonation an independent native weapon so delayed bullets retain their true owner.
  if(Network?.Hosting==true&&!e.Actor.fragmentBallistics.transform.IsChildOf(e.Actor.transform)&&!e.OwnedWeapon){
   var source=e.Actor.fragmentBallistics;var copy=Object.Instantiate(source.gameObject,e.Actor.transform.position,e.Actor.transform.rotation);
   foreach(var script in copy.GetComponentsInChildren<MonoBehaviour>(true))if(!script.TryCast<Ballistics.Weapon>()){script.StopAllCoroutines();script.enabled=false;}
   foreach(var c in copy.GetComponentsInChildren<Collider>(true))c.enabled=false;foreach(var r in copy.GetComponentsInChildren<Renderer>(true))r.enabled=false;foreach(var b in copy.GetComponentsInChildren<Rigidbody>(true))b.isKinematic=true;
   var weapon=copy.GetComponent<Ballistics.Weapon>();if(!weapon){Object.Destroy(copy);return;}weapon.PhysicalBulletSpawnPoint=weapon.transform;weapon.VisualSpawnPoint=weapon.transform;e.Actor.fragmentBallistics=weapon;e.OwnedWeapon=weapon;
  }
  if(e.OwnedWeapon)e.OwnedWeapon.transform.SetPositionAndRotation(e.Actor.transform.position,e.Actor.transform.rotation);
  var life=e.Actor.fragmentBallistics.LifeTimeOfBullets;var keep=float.IsFinite(life)?Math.Max(45,life+2):45;e.RetainUntil=Time.unscaledTime+keep;
  fragmentOwners[e.Actor.fragmentBallistics.Pointer]=(e.Owner,e.DamageScale,e.InstantKill,e.RetainUntil);
 }
 ProjectileEntry? RegisterProjectile(Grenade actor,int owner,long request=0,string? serverKey=null,float scale=1,float instant=0,string? templateKey=null){
  if(projectileEpoch!=ProjectileEpoch){ClearProjectiles();projectileEpoch=ProjectileEpoch;}
  if(projectileActors.TryGetValue(actor.Pointer,out var old))return old;
  if(projectiles.Count>=256)return null;var prefab=templateKey??ProjectileTemplate(actor);if(prefab=="")return null;
  var entry=new ProjectileEntry{Actor=actor,Key=serverKey??((Network?.Hosting==true?"H":"L")+(++projectileId)),Prefab=prefab,Epoch=ProjectileEpoch,Owner=owner,Request=request,Born=Time.unscaledTime,DamageScale=scale,InstantKill=instant};
  projectiles[entry.Key]=entry;projectileActors[actor.Pointer]=entry;RememberFragments(entry);return entry;
 }
 static Vector3 Vector(float[] a)=>new(a[0],a[1],a[2]);
 ProjectileFrame CaptureProjectile(ProjectileEntry e,bool initial=false){
  var g=e.Actor;var t=g.transform;var p=t.position;var q=t.rotation;var body=g.GetComponent<Rigidbody>();var velocity=body?body.velocity:Vector3.zero;var spin=body?body.angularVelocity:Vector3.zero;
  if(initial&&body&&!body.isKinematic)velocity+=body.GetAccumulatedForce(Time.fixedDeltaTime)/Math.Max(.0001f,body.mass)*Time.fixedDeltaTime;
  return new ProjectileFrame{Key=e.Key,Prefab=e.Prefab,Owner=e.Owner,Request=e.Request,Pose=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w},Velocity=new[]{velocity.x,velocity.y,velocity.z},Spin=new[]{spin.x,spin.y,spin.z},Fuse=Math.Clamp(g.timeToExplode-(Time.unscaledTime-e.Born),0,60),DamageScale=e.DamageScale,InstantKill=e.InstantKill,Exploded=e.ExplodedAt>=0};
 }
 static void FreezeProjectile(Grenade g){g.StopAllCoroutines();g.enabled=false;foreach(var rocket in g.GetComponentsInChildren<RocketMove>(true))rocket.enabled=false;
  foreach(var c in g.GetComponentsInChildren<Collider>(true))c.enabled=false;foreach(var b in g.GetComponentsInChildren<Rigidbody>(true))b.isKinematic=true;
 }
 [HideFromIl2Cpp]
 internal bool ProjectileStart(Grenade actor){
  if(projectileCloneDepth>0)return false;if(!SharedRun||Network==null)return true;
  if(projectileEpoch!=ProjectileEpoch){ClearProjectiles();projectileEpoch=ProjectileEpoch;}
  if(Network.Hosting){var entry=RegisterProjectile(actor,-1);if(entry!=null)RememberFragments(entry);return true;}
  FreezeProjectile(actor);if(!projectileActors.ContainsKey(actor.Pointer)&&orphanProjectiles.Count<256)orphanProjectiles[actor.Pointer]=actor;return false;
 }
 [HideFromIl2Cpp]
 internal void CaptureOwnThrow(HashSet<IntPtr> before){
  if(!SharedRun||Network==null)return;
  foreach(var actor in Object.FindObjectsOfType<Grenade>())if(!before.Contains(actor.Pointer)&&!projectileActors.ContainsKey(actor.Pointer)){
   var entry=RegisterProjectile(actor,Network.LocalId,Network.Hosting?0:++throwSequence,scale:1+Math.Clamp(PlayerPrefs.GetFloat("MoreDamage",0),0,10000)/100,instant:Math.Clamp(PlayerPrefs.GetFloat("InstantKill",0),0,100));
   if(entry==null){if(!Network.Hosting)FreezeProjectile(actor);continue;}
   if(!Network.Hosting){entry.Frame=CaptureProjectile(entry,true);FreezeProjectile(actor);SendThrow(entry);}
  }
 }
 void SendThrow(ProjectileEntry e){if(e.Frame==null||Network==null)return;var p=Message("grenreq");p.Sequence=e.Request;p.Projectiles=new[]{e.Frame};Network.Send(p);e.RetryAt=Time.unscaledTime+1;}
 Grenade? CloneProjectile(Grenade prefab,ProjectileFrame frame){
  // A cached managed Grenade reference can outlive its native component. The
  // Unity liveness check must precede Component.gameObject (guest809 crash).
  var source=ProjectilePrefabObject(prefab);if(!source)return null;
  projectileCloneDepth++;try{var p=frame.Pose;var clone=Object.Instantiate(source,Vector(p),new Quaternion(p[3],p[4],p[5],p[6]));var actor=clone?clone.GetComponent<Grenade>():null;if(!actor&&clone)Object.Destroy(clone);return actor;}finally{projectileCloneDepth--;}
 }
 void ProjectilePrefabFailure(string key,string prefab,string reason){var final=reason is "retry_expired" or "retry_capacity" or "actor_capacity";if(final?projectilePrefabFinalReports++>=32:projectilePrefabReports++>=32)return;if(!GameplayLog.Operational)return;GameplayLog.Record("projectile_prefab_unavailable",new{reason,key=DiagnosticObjectKey(key),prefab=DiagnosticObjectKey(prefab),pending=deferredProjectiles.Count});}
 void DeferProjectile(ProjectileFrame frame,long sequence){
  if(projectileTombstones.Contains(frame.Key))return;
  if(deferredProjectiles.TryGetValue(frame.Key,out var old)){if(sequence>old.Sequence){old.Frame=frame;old.Sequence=sequence;}return;}
  if(deferredProjectiles.Count>=256||deferredProjectileKeys.Count>=512){if(frame.Exploded&&projectileTombstones.Count<8192)projectileTombstones.Add(frame.Key);ProjectilePrefabFailure(frame.Key,frame.Prefab,"retry_capacity");return;}
  deferredProjectiles[frame.Key]=new(){Frame=frame,Sequence=sequence,Expires=Time.unscaledTime+12,RetryAt=Time.unscaledTime+.5f};deferredProjectileKeys.Enqueue(frame.Key);ProjectilePrefabFailure(frame.Key,frame.Prefab,"waiting_for_live_asset");
 }
 void DeferredProjectilesTick(){
  if(Network?.Hosting!=false)return;var count=Math.Min(4,deferredProjectileKeys.Count);var now=Time.unscaledTime;
  for(var i=0;i<count&&deferredProjectileKeys.Count>0;i++){
   var key=deferredProjectileKeys.Dequeue();if(!deferredProjectiles.TryGetValue(key,out var pending))continue;
   if(now>=pending.Expires){deferredProjectiles.Remove(key);if(pending.Frame.Exploded&&projectileTombstones.Count<8192)projectileTombstones.Add(key);ProjectilePrefabFailure(key,pending.Frame.Prefab,"retry_expired");continue;}
   if(now<pending.RetryAt){deferredProjectileKeys.Enqueue(key);continue;}
   if(ApplyReplicaProjectile(pending.Frame,pending.Sequence))deferredProjectiles.Remove(key);else{pending.RetryAt=now+.5f;deferredProjectileKeys.Enqueue(key);}
  }
 }
 void ProjectileTick(){
  if(!SharedRun||Network==null){if(projectiles.Count>0||orphanProjectiles.Count>0||projectileEpoch!="")ClearProjectiles();return;}
  var epoch=ProjectileEpoch;if(projectileEpoch!=epoch){ClearProjectiles();projectileEpoch=epoch;}
  DeferredProjectilesTick();
  foreach(var key in fragmentOwners.Where(x=>x.Value.Until<Time.unscaledTime).Select(x=>x.Key).ToArray())fragmentOwners.Remove(key);
  foreach(var e in projectiles.Values.ToArray()){
   if(e.Epoch!=epoch||e.ExplodedAt<0&&Time.unscaledTime-e.Born>90||e.ExplodedAt>=0&&(Network.Hosting?e.NativeComplete&&Time.unscaledTime>e.RetainUntil:Time.unscaledTime-e.ExplodedAt>8)){if(e.Actor){projectileActors.Remove(e.Actor.Pointer);e.Actor.StopAllCoroutines();Object.Destroy(e.Actor.gameObject);}if(e.OwnedWeapon)Object.Destroy(e.OwnedWeapon.gameObject);projectiles.Remove(e.Key);continue;}
   if(!Network.Hosting&&e.Request>0&&e.Owner==Network.LocalId&&e.Key.StartsWith("L")&&e.Frame!=null&&Time.unscaledTime>=e.RetryAt&&SameWorld)SendThrow(e);
  }
  if(!Network.Hosting||!Connected||!SameWorld||Time.unscaledTime<projectileSendAt)return;projectileSendAt=Time.unscaledTime+.05f;
  var frames=new List<ProjectileFrame>();foreach(var e in projectiles.Values){if(e.ExplodedAt>=0&&Time.unscaledTime-e.ExplodedAt>8)continue;if(e.Actor&&e.ExplodedAt<0)e.Frame=CaptureProjectile(e);if(e.Frame!=null){e.Frame.Exploded=e.ExplodedAt>=0;frames.Add(e.Frame);}}
  var sequence=++projectileSequence;for(var offset=0;offset<frames.Count;offset+=8){var p=Message("projectile");p.Sequence=sequence;p.Category=offset/8;p.Projectiles=frames.Skip(offset).Take(8).ToArray();Network.Send(p);}
 }
 void ProjectileLateTick(){if(!SharedRun||Network?.Hosting!=false)return;foreach(var e in projectiles.Values)if(e.Actor&&e.Frame is {} f&&e.ExplodedAt<0){var p=f.Pose;e.Actor.transform.SetPositionAndRotation(Vector(p),new Quaternion(p[3],p[4],p[5],p[6]));}}
 [HideFromIl2Cpp]
 bool ReceiveProjectile(Packet p){
  if(p.Kind is "grenreq" or "projectile"&&SharedRun&&PacketWorld(p)&&projectileEpoch!=ProjectileEpoch){ClearProjectiles();projectileEpoch=ProjectileEpoch;}
  if(p.Kind=="grenreq"&&Network?.Hosting==true){
   if(!SharedRun||!PacketWorld(p)||!remotes.TryGetValue(p.PlayerId,out var peer)||!PeerMatches(peer)||peer.State!.Health<=0)return true;
   var f=p.Projectiles[0];if(throwReceipts.ContainsKey((p.PlayerId,p.Sequence)))return true;
   if(throwReceipts.Count>=2048||projectiles.Count>=256||lastThrows.TryGetValue(p.PlayerId,out var last)&&Time.unscaledTime-last<.25f)return true;
   BuildProjectileCatalogue();if(!TryProjectilePrefab(f.Prefab,out var prefab)||Vector3.Distance(Vector(f.Pose),Vector(peer.State.Pose))>6)return true;
   // Neither nested Owner nor Request nor Key from the guest conveys authority.
   f.Owner=p.PlayerId;f.Request=p.Sequence;f.Key="S"+(++projectileId);f.Fuse=Math.Min(f.Fuse,Math.Clamp(prefab.timeToExplode,0,60));
   var g=CloneProjectile(prefab,f);if(!g)return true;g.timeToExplode=f.Fuse;var body=g.GetComponent<Rigidbody>();if(body){body.velocity=Vector(f.Velocity);body.angularVelocity=Vector(f.Spin);}
   var e=RegisterProjectile(g,p.PlayerId,p.Sequence,f.Key,f.DamageScale,f.InstantKill,f.Prefab);if(e==null){Object.Destroy(g.gameObject);return true;}e.Frame=CaptureProjectile(e);throwReceipts[(p.PlayerId,p.Sequence)]=e.Key;lastThrows[p.PlayerId]=Time.unscaledTime;return true;
  }
  if(p.Kind=="projectile"&&Network?.Hosting==false){
   if(!SharedRun||!PacketWorld(p))return true;BuildProjectileCatalogue();
   foreach(var f in p.Projectiles)if(ApplyReplicaProjectile(f,p.Sequence))deferredProjectiles.Remove(f.Key);else DeferProjectile(f,p.Sequence);
   return true;
  }
  if(p.Kind=="projectilecredit"&&Network?.Hosting==false){if(PacketWorld(p)&&p.Sequence>0&&p.Amount>0&&p.Amount<=1000000&&projectileCredits.Count<8192&&projectileCredits.Add(p.Sequence)){var vampire=PlayerPrefs.GetFloat("Vampirism",0);if(vampire>0)Object.FindObjectOfType<PlayerHealthManager>()?.AddHealthAccumulator(p.Amount*vampire/100);}return true;}
  return false;
 }
 bool ApplyReplicaProjectile(ProjectileFrame f,long sequence){
    if(projectileTombstones.Contains(f.Key))return true;
    if(!projectiles.TryGetValue(f.Key,out var e)){
     e=projectiles.Values.FirstOrDefault(x=>x.Owner==Network.LocalId&&f.Owner==Network.LocalId&&x.Request>0&&x.Request==f.Request);
     if(e!=null){projectiles.Remove(e.Key);e.Key=f.Key;projectiles[e.Key]=e;}
     else{if(projectiles.Count>=256){ProjectilePrefabFailure(f.Key,f.Prefab,"actor_capacity");return true;}if(!TryProjectilePrefab(f.Prefab,out var prefab))return false;var g=orphanProjectiles.Values.FirstOrDefault(x=>x&&ProjectileTemplate(x)==f.Prefab&&Vector3.Distance(x.transform.position,Vector(f.Pose))<2);if(g)orphanProjectiles.Remove(g.Pointer);else g=CloneProjectile(prefab,f);if(!g)return false;FreezeProjectile(g);e=RegisterProjectile(g,f.Owner,f.Request,f.Key,f.DamageScale,f.InstantKill,f.Prefab);if(e==null){Object.Destroy(g.gameObject);return false;}}
    }
    if(sequence<=e.Sequence)return true;e.Sequence=sequence;e.Frame=f;
    if(f.Exploded&&e.ExplodedAt<0){if(e.Actor){var p0=f.Pose;e.Actor.transform.SetPositionAndRotation(Vector(p0),new Quaternion(p0[3],p0[4],p0[5],p0[6]));}e.ExplodedAt=Time.unscaledTime;if(projectileTombstones.Count<8192)projectileTombstones.Add(f.Key);ShowProjectileExplosion(e);if(e.Actor)e.Actor.gameObject.SetActive(false);}
   return true;
 }
 static void ShowProjectileExplosion(ProjectileEntry e){
  if(!e.Actor)return;Together.Current?.ApplyProjectileFeedback(e.Actor);var parent=new GameObject("Coop explosion visual");parent.SetActive(false);parent.transform.position=e.Actor.transform.position;
  projectileCloneDepth++;try{
   foreach(var prefab in new[]{e.Actor.Explosion,e.Actor.AudioPrefab}){if(!prefab)continue;var fx=Object.Instantiate(prefab,parent.transform);foreach(var b in fx.GetComponentsInChildren<MonoBehaviour>(true))Object.DestroyImmediate(b);foreach(var c in fx.GetComponentsInChildren<Collider>(true))c.enabled=false;foreach(var b in fx.GetComponentsInChildren<Rigidbody>(true))b.isKinematic=true;fx.SetActive(true);}
   parent.SetActive(true);foreach(var ps in parent.GetComponentsInChildren<ParticleSystem>(true))ps.Play();foreach(var audio in parent.GetComponentsInChildren<AudioSource>(true))if(audio.clip&&!audio.isPlaying)audio.Play();Object.Destroy(parent,8);
  }finally{projectileCloneDepth--;}
 }
 [HideFromIl2Cpp]
 internal bool BeginExplosion(Grenade actor,IntPtr iterator,out ProjectileCombatScope? scope){
  scope=null;if(projectileCloneDepth>0)return false;if(!SharedRun||Network==null)return true;if(!Network.Hosting)return false;
  var e=projectileActors.GetValueOrDefault(actor.Pointer)??RegisterProjectile(actor,-1);if(e==null||e.Epoch!=ProjectileEpoch||e.Iterator!=IntPtr.Zero&&e.Iterator!=iterator)return false;
  EnsureExplosionEffects(actor);
  e.Iterator=iterator;scope=new ProjectileCombatScope(e.Owner,e.DamageScale,e.InstantKill){ExplosionActor=actor};RememberFragments(e);
  if(e.ExplodedAt<0){e.Frame=CaptureProjectile(e);e.ExplodedAt=Time.unscaledTime;e.Frame.Exploded=true;
  }return true;
 }
 [HideFromIl2Cpp]
 internal void CompleteExplosion(Grenade actor,bool running,bool fault=false){if(!projectileActors.TryGetValue(actor.Pointer,out var e))return;if(!running||fault)e.NativeComplete=true;
  if(!fault&&!e.DirectSent&&Network?.Hosting==true){e.DirectSent=true;var at=e.Frame!=null?Vector(e.Frame.Pose):actor.transform.position;foreach(var peer in remotes.Values)if(PeerMatches(peer)&&peer.State!.Health>0&&ProjectileRules.HurtsPlayer(e.Owner,peer.Id)&&Vector3.Distance(Vector(peer.State.Pose),at)<2.3f)DeliverHurt(peer.Id,30);}
 }
 [HideFromIl2Cpp]
 internal ProjectileCombatScope? FragmentScope(Ballistics.Weapon weapon){if(weapon==null||!fragmentOwners.TryGetValue(weapon.Pointer,out var owner)||owner.Until<Time.unscaledTime)return null;return new ProjectileCombatScope(owner.Owner,owner.Scale,owner.Instant);}
 [HideFromIl2Cpp]
 internal void CaptureExplosionChain(GameObject effect,int owner,float scale,float instant){
  if(!SharedRun||Network?.Hosting!=true||!effect||!effect.scene.IsValid())return;
  // Native ExplodingObject.Explode only activates this authored effect root.
  // Inspect that root after activation, including any synchronous new children,
  // instead of running two whole-scene censuses for every broken pane.
  foreach(var g in effect.GetComponentsInChildren<Grenade>())if(g&&!projectileActors.ContainsKey(g.Pointer))RegisterProjectile(g,owner,scale:scale,instant:instant);
 }
 [HideFromIl2Cpp]
 internal void CreditProjectileDamage(EnemyHealth enemy,float before,int owner){
  if(Network?.Hosting!=true||owner<0||owner==Network.LocalId)return;var amount=Math.Clamp(before-enemy.health,0,before);if(amount>0){var p=Message("projectilecredit");p.Sequence=++projectileCreditSequence;p.Amount=amount;Network.SendTo(p,owner);}
  if((enemy.dead||enemy.health<=0)&&enemyIds.TryGetValue(enemy.Pointer,out var id)){var p=Message("killcredit");p.Sequence=id;p.ItemId=(int)enemy.enemyType;Network.SendTo(p,owner);}
 }
}

internal sealed class ProjectileCombatScope:IDisposable {
 internal Grenade? ExplosionActor;
 readonly int? owner=Together.ProjectileOwner;readonly int credit=Together.DamageCreditPeer;readonly bool suppress=PersonalProfile.SuppressCombatPerks;readonly float? bonus=PersonalProfile.NetworkDamageBonus,instant=PersonalProfile.NetworkInstantKill;
 internal ProjectileCombatScope(int source,float scale,float chance){Together.ProjectileOwner=source;Together.DamageCreditPeer=source==Together.Current?.Network?.LocalId?0:source;PersonalProfile.SuppressCombatPerks=source!=Together.Current?.Network?.LocalId;PersonalProfile.NetworkDamageBonus=(scale-1)*100;PersonalProfile.NetworkInstantKill=chance;}
 public void Dispose(){Together.ProjectileOwner=owner;Together.DamageCreditPeer=credit;PersonalProfile.SuppressCombatPerks=suppress;PersonalProfile.NetworkDamageBonus=bonus;PersonalProfile.NetworkInstantKill=instant;}
}
[HarmonyPatch(typeof(Grenade),"Start")]
static class CoopGrenadeStart {static bool Prefix(Grenade __instance)=>Together.Current?.ProjectileStart(__instance)??true;}
[HarmonyPatch]
static class CoopExplosionIterator {
 static MethodBase TargetMethod()=>typeof(Grenade).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic).First(t=>t.Name.StartsWith("_Explode_d__")||t.Name.Contains("<Explode>d__")).GetMethod("MoveNext")!;
 static bool Prefix(object __instance,out ProjectileCombatScope? __state){__state=null;var property=__instance.GetType().GetProperties().FirstOrDefault(p=>p.PropertyType==typeof(Grenade));var g=property?.GetValue(__instance) as Grenade;return g==null||Together.Current==null||Together.Current.BeginExplosion(g,((Il2CppObjectBase)__instance).Pointer,out __state);}
 static Exception? Finalizer(ProjectileCombatScope? __state,Exception? __exception){try{if(__exception!=null&&__state?.ExplosionActor is {} g)Together.Current?.CompleteExplosion(g,false,true);}finally{__state?.Dispose();}return __exception;}
 static void Postfix(bool __result,ProjectileCombatScope? __state){if(__state?.ExplosionActor is {} g)Together.Current?.CompleteExplosion(g,__result);}
}
[HarmonyPatch]
static class CoopLocalThrowCapture {
 static MethodBase TargetMethod()=>typeof(Demo.Scripts.Runtime.Character.FPSController).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic).First(t=>t.Name.Contains("WaitAndSpawnGrenade")).GetMethod("MoveNext")!;
 static void Prefix(out HashSet<IntPtr> __state)=>__state=Object.FindObjectsOfType<Grenade>().Select(g=>g.Pointer).ToHashSet();
 static Exception? Finalizer(HashSet<IntPtr> __state,Exception? __exception){if(__exception==null)Together.Current?.CaptureOwnThrow(__state);return __exception;}
}
[HarmonyPatch(typeof(Demo.Scripts.Runtime.Character.FPSController),"OnThrowGrenade")]
static class CoopThrowAllowed {static bool Prefix(){var c=Together.Current;return c?.SharedRun!=true||(!c.Down&&!c.PartyWiped&&!c.Recovering&&c.SameWorld);}}
[HarmonyPatch]
static class ProjectileDamageCredit {
 internal struct State {public bool Active;public int Owner;public float Before;}
 static IEnumerable<MethodBase> TargetMethods()=>typeof(EnemyHealth).GetMethods().Where(m=>m.Name==nameof(EnemyHealth.TakeDamage)&&m.DeclaringType==typeof(EnemyHealth));
 static void Prefix(EnemyHealth __instance,out State __state){__state=new(){Active=Together.ProjectileOwner.HasValue&&Together.projectileDamageDepth==0,Owner=Together.ProjectileOwner??-1,Before=Math.Max(0,__instance.health)};Together.projectileDamageDepth++;}
 static Exception? Finalizer(EnemyHealth __instance,State __state,Exception? __exception){Together.projectileDamageDepth--;if(__state.Active)Together.Current?.CreditProjectileDamage(__instance,__state.Before,__state.Owner);return __exception;}
}
[HarmonyPatch(typeof(ExplodingObject),"Explode")]
static class CoopExplosiveChain {
 internal struct State {public GameObject? Effect;public int Owner,Actor;public float Scale,Instant;public bool Timed,Pure;public long NativeAt;public double BeforeMs;}
 static void Prefix(ExplodingObject __instance,out State __state){
  __state=default;var c=Together.Current;
  // Native Explode returns immediately after its first call. Repeated bullet
  // health writes must not run two scene censuses around that native no-op.
  if(c?.SharedRun!=true||c.Network==null||!__instance||__instance.exploded)return;
  var started=System.Diagnostics.Stopwatch.GetTimestamp();__state.Actor=__instance.GetInstanceID();
  __state.Pure=GlassFractureDiagnostics.IsVerifiedPureGlass(__instance);
  if(c.Network.Hosting&&!__state.Pure){
   __state.Effect=__instance.Explosion;
   __state.Owner=Together.ProjectileOwner??(Together.PlayerBullet?c.Network.LocalId:Together.DamageCreditPeer>0?Together.DamageCreditPeer:-1);
   __state.Scale=1+(PersonalProfile.NetworkDamageBonus??(__state.Owner==c.Network.LocalId?PlayerPrefs.GetFloat("MoreDamage",0):0))/100;
   __state.Instant=PersonalProfile.NetworkInstantKill??(__state.Owner==c.Network.LocalId?PlayerPrefs.GetFloat("InstantKill",0):0);
  }
  __state.NativeAt=System.Diagnostics.Stopwatch.GetTimestamp();__state.BeforeMs=(__state.NativeAt-started)*1000d/System.Diagnostics.Stopwatch.Frequency;__state.Timed=true;
 }
 static Exception? Finalizer(State __state,Exception? __exception){
  if(!__state.Timed)return __exception;
  var afterAt=System.Diagnostics.Stopwatch.GetTimestamp();var nativeMs=(afterAt-__state.NativeAt)*1000d/System.Diagnostics.Stopwatch.Frequency;
  if(__state.Effect)Together.Current?.CaptureExplosionChain(__state.Effect,__state.Owner,__state.Scale,__state.Instant);
  GlassFractureDiagnostics.RecordExplosion(__state.Actor,__state.Pure,__state.BeforeMs,nativeMs,(System.Diagnostics.Stopwatch.GetTimestamp()-afterAt)*1000d/System.Diagnostics.Stopwatch.Frequency);
  return __exception;
 }
}
