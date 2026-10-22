using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;
using HarmonyLib;
using System.Reflection;

namespace FractureTogether;

// Process-local native hooks cover compiled IL2CPP callers as well as managed wrappers.
// No network message can select a profile, change a preference, or write a save file.
public sealed class PersonalProfile
{
    [ThreadStatic] public static bool SuppressCombatPerks;
    [ThreadStatic] internal static float? NetworkDamageBonus;
    [ThreadStatic] internal static float? NetworkInstantKill;
    static bool ReplicaKill=>Together.ApplyingEnemyState||Together.ApplyingUnitState||Together.DamageCreditPeer!=0;
    static bool HiddenPerk(string key)=>(SuppressCombatPerks&&(key is "MoreDamage" or "Vampirism" or "InstantKill"))||(ReplicaKill&&key=="KillRegenEffect");
    public sealed class Pref
    {
        public int? Int { get; set; }
        public float? Float { get; set; }
        public string? Text { get; set; }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr PathGetter();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntGetter(IntPtr key,int fallback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float FloatGetter(IntPtr key,float fallback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr StringGetter(IntPtr key,IntPtr fallback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool IntSetter(IntPtr key,int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool FloatSetter(IntPtr key,float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool StringSetter(IntPtr key,IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return:MarshalAs(UnmanagedType.I1)] delegate bool HasGetter(IntPtr key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void KeyAction(IntPtr key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void EmptyAction();
    readonly List<INativeDetour> hooks=new();
    readonly List<Delegate> roots=new();
    readonly object gate=new();
    readonly FileStream profileLock;
    readonly Dictionary<string,Pref> prefs;
    readonly string prefsFile;
    static PersonalProfile Active=null!;
    readonly Harmony patcher=new("local.fracture.together.personalprofile");
    bool dirty;
    long sodasFlushAt;
    long writes,writeTicks;readonly Dictionary<string,int> writeKeys=new();
    public object WriteMetrics(){lock(gate)return new{writes,totalMs=writeTicks*1000.0/System.Diagnostics.Stopwatch.Frequency,keys=writeKeys.OrderByDescending(p=>p.Value).Take(20).ToArray()};}
    public string Name { get; }
    public string DirectoryPath { get; }
    public string OriginalPath { get; }
    public bool Healthy { get; private set; }=true;
    public int PrefCount => prefs.Count;
    public PersonalProfile(string name)
    {
        if(!Regex.IsMatch(name,@"^[A-Za-z0-9_-]{1,40}$")) throw new IOException("Profile name must contain 1-40 ASCII letters, numbers, _ or -");
        Name=name;
        OriginalPath=Application.persistentDataPath;
        var root=Path.Combine(Path.GetDirectoryName(OriginalPath)!,"FractureTogether-Profiles");
        Directory.CreateDirectory(root);
        DirectoryPath=Path.Combine(root,name);
        profileLock=new FileStream(Path.Combine(root,name+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        prefsFile=Path.Combine(DirectoryPath,"PersonalPlayerPrefs.json");
        try {
            if(!Directory.Exists(DirectoryPath)) Initialize();
            if(!File.Exists(Path.Combine(DirectoryPath,"profile-origin.json")) || !File.Exists(prefsFile))
                throw new IOException("Incomplete personal profile. Original saves were not touched; restore the profile backup before continuing.");
            prefs=JsonSerializer.Deserialize<Dictionary<string,Pref>>(File.ReadAllText(prefsFile)) ?? throw new IOException("Invalid personal preferences");
            if(prefs.Any(p=>p.Value==null || (p.Value.Int==null && p.Value.Float==null && p.Value.Text==null) || (p.Value.Float.HasValue && !float.IsFinite(p.Value.Float.Value))))
                throw new IOException("Invalid entries in personal preferences; refusing to load default progress");
            var initial=Path.Combine(DirectoryPath+".initial-backup","PersonalPlayerPrefs.json");
            if(File.Exists(initial)) {
                var originalPrefs=JsonSerializer.Deserialize<Dictionary<string,Pref>>(File.ReadAllText(initial))!;
                foreach(var critical in new[]{"Bank","Reputation","MaxHealth"})
                    if(originalPrefs.ContainsKey(critical) && !prefs.ContainsKey(critical)) throw new IOException("Missing personal progress key: "+critical);
            }
            Active=this;
            Patch(typeof(Application),"get_persistentDataPath",Type.EmptyTypes,nameof(PathPrefix));
            Patch(typeof(PlayerPrefs),"GetInt",new[]{typeof(string),typeof(int)},nameof(IntPrefix));
            Patch(typeof(PlayerPrefs),"GetFloat",new[]{typeof(string),typeof(float)},nameof(FloatPrefix));
            Patch(typeof(PlayerPrefs),"GetString",new[]{typeof(string),typeof(string)},nameof(StringPrefix));
            Patch(typeof(PlayerPrefs),"TrySetInt",new[]{typeof(string),typeof(int)},nameof(SetIntPrefix));
            Patch(typeof(PlayerPrefs),"TrySetFloat",new[]{typeof(string),typeof(float)},nameof(SetFloatPrefix));
            Patch(typeof(PlayerPrefs),"TrySetSetString",new[]{typeof(string),typeof(string)},nameof(SetStringPrefix));
            Patch(typeof(PlayerPrefs),"HasKey",new[]{typeof(string)},nameof(HasPrefix));
            Patch(typeof(PlayerPrefs),"DeleteKey",new[]{typeof(string)},nameof(DeletePrefix));
            Patch(typeof(PlayerPrefs),"Save",Type.EmptyTypes,nameof(SavePrefix));
            // DeleteAll is deliberately refused; a menu reset must never erase personal progress.
            var deleteAll=IL2CPP.il2cpp_resolve_icall("UnityEngine.PlayerPrefs::DeleteAll");
            if(deleteAll==IntPtr.Zero) throw new IOException("Missing DeleteAll protection binding");
            Install(deleteAll,(EmptyAction)(()=>Plugin.Instance.Log.LogWarning("Blocked PlayerPrefs.DeleteAll in protected personal profile")));
            if(Path.GetFullPath(Application.persistentDataPath)!=Path.GetFullPath(DirectoryPath)) throw new IOException("Save path redirection self-check failed");
            Plugin.Instance.Log.LogInfo($"Personal profile {Name}: {DirectoryPath}; {prefs.Count} local preferences; original profile read only");
        } catch {
            patcher.UnpatchSelf();
            for(var i=hooks.Count-1;i>=0;i--) hooks[i].Dispose();
            hooks.Clear(); profileLock.Dispose(); throw;
        }
    }
    void Patch(Type type,string method,Type[] args,string prefix)
    {
        var target=AccessTools.Method(type,method,args) ?? throw new MissingMethodException(type.FullName,method);
        patcher.Patch(target,prefix:new HarmonyMethod(typeof(PersonalProfile),prefix));
    }
    static bool PathPrefix(ref string __result) {__result=Active.DirectoryPath.Replace('\\','/'); return false;}
    static bool IntPrefix(string __0,int __1,ref int __result) {
        var together=Together.Current;
        // The host selects the shared run's floor, without overwriting this
        // participant's saved solo floor or any other personal progress.
        if(__0=="currentFloor"&&together?.Network?.Hosting==false&&together.RunPlayers>1&&together.SessionFloor>=0){__result=together.SessionFloor;return false;}
        lock(Active.gate) __result=ReplicaKill&&(__0 is "EnemiesKilled" or "ENDLESS_MODE")?0:Active.prefs.TryGetValue(__0,out var p)?p.Int??__1:__1; return false;
    }
    static bool FloatPrefix(string __0,float __1,ref float __result) {if(__0=="MoreDamage"&&NetworkDamageBonus.HasValue){__result=NetworkDamageBonus.Value;return false;}if(__0=="InstantKill"&&NetworkInstantKill.HasValue){__result=NetworkInstantKill.Value;return false;}lock(Active.gate) __result=HiddenPerk(__0)?__1:Active.prefs.TryGetValue(__0,out var p)?p.Float??__1:__1; return false;}
    static bool StringPrefix(string __0,string __1,ref string __result) {lock(Active.gate) __result=Active.prefs.TryGetValue(__0,out var p)?p.Text??__1:__1; return false;}
    static bool SetIntPrefix(string __0,int __1,ref bool __result) {
        var together=Together.Current;
        if(__0=="currentFloor"&&together?.Network?.Hosting==false&&together.RunPlayers>1){__result=__1>=0&&__1<=100000;if(__result)together.SessionFloor=__1;return false;}
        __result=ReplicaKill&&__0=="EnemiesKilled"||Active.Set(__0,new Pref{Int=__1});return false;
    }
    static bool SetFloatPrefix(string __0,float __1,ref bool __result) {__result=float.IsFinite(__1)&&Active.Set(__0,new Pref{Float=__1});return false;}
    static bool SetStringPrefix(string __0,string __1,ref bool __result) {__result=Active.Set(__0,new Pref{Text=__1});return false;}
    static bool HasPrefix(string __0,ref bool __result) {if((__0=="MoreDamage"&&NetworkDamageBonus.HasValue)||(__0=="InstantKill"&&NetworkInstantKill.HasValue)){__result=true;return false;}var together=Together.Current;if(__0=="currentFloor"&&together?.Network?.Hosting==false&&together.RunPlayers>1&&together.SessionFloor>=0){__result=true;return false;}lock(Active.gate)__result=!HiddenPerk(__0)&&Active.prefs.ContainsKey(__0);return false;}
    static bool DeletePrefix(string __0) { lock(Active.gate){Active.dirty|=Active.prefs.Remove(__0);Active.Flush();} return false;}
    static bool SavePrefix() {Active.Flush();return false;}
    void Initialize()
    {
        var stage=DirectoryPath+".initializing-"+Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        var files=new Dictionary<string,string>();
        foreach(var file in Directory.EnumerateFiles(OriginalPath,"*",SearchOption.AllDirectories)) {
            if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Unexpected linked file in source saves");
            var relative=Path.GetRelativePath(OriginalPath,file);
            if(relative.EndsWith(".log",StringComparison.OrdinalIgnoreCase)) continue;
            var target=Path.Combine(stage,relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var before=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            File.Copy(file,target,false);
            if(before!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))) || before!=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))))
                throw new IOException("Source saves changed while copying; initialization aborted");
            files[relative]=before;
        }
        var snapshot=new Dictionary<string,Pref>();
        var rawRegistry=new List<object>();
        using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Kyrylo Burlaka\Fracture Point Release",false)) {
            if(key!=null) foreach(var rawName in key.GetValueNames()) {
                var name=Regex.Replace(rawName,@"_h[0-9]+$","");
                rawRegistry.Add(new {name=rawName,kind=key.GetValueKind(rawName).ToString(),value=key.GetValue(rawName)});
                if(!PlayerPrefs.HasKey(name)) continue;
                // Query Unity itself: float encoding in the registry is engine-specific.
                var i=PlayerPrefs.GetInt(name,int.MinValue);
                var f=PlayerPrefs.GetFloat(name,float.NaN);
                const string absent="FT_ABSENT_8d87ac36";
                var s=PlayerPrefs.GetString(name,absent);
                snapshot[name]=new Pref {Int=i==int.MinValue?null:i,Float=float.IsFinite(f)?f:null,Text=s==absent?null:s};
            }
        }
        File.WriteAllText(Path.Combine(stage,"PersonalPlayerPrefs.json"),JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(stage,"original-registry-snapshot.json"),JsonSerializer.Serialize(rawRegistry,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(stage,"profile-origin.json"),JsonSerializer.Serialize(new {version=1,profile=Name,source=OriginalPath,created=DateTime.UtcNow,files},new JsonSerializerOptions{WriteIndented=true}));
        Directory.Move(stage,DirectoryPath);
        // Keep an immutable initial snapshot, separate from all active game files.
        var backup=DirectoryPath+".initial-backup";
        if(!Directory.Exists(backup)) {
            Directory.CreateDirectory(backup);
            foreach(var file in Directory.EnumerateFiles(DirectoryPath,"*",SearchOption.AllDirectories)) {
                var dest=Path.Combine(backup,Path.GetRelativePath(DirectoryPath,file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file,dest,false);
            }
        }
    }
    void Install<T>(IntPtr address,T callback) where T:Delegate
    {
        File.AppendAllText(Path.Combine(BepInEx.Paths.BepInExRootPath,"profile-trace.txt"),typeof(T).Name+" "+address.ToString("X")+Environment.NewLine);
        roots.Add(callback);
        hooks.Add(INativeDetour.CreateAndApply(address,callback,out T original));
        roots.Add(original);
    }
    static string Key(IntPtr ptr)=>IL2CPP.Il2CppStringToManaged(ptr)??"";
    T Read<T>(IntPtr key,Func<Pref,T?> selector,T fallback) where T:struct
    {
        try {lock(gate) return prefs.TryGetValue(Key(key),out var p)?selector(p)??fallback:fallback;}
        catch(Exception e){Fault(e);return fallback;}
    }
    bool Set(string name,Pref value)
    {
        try { lock(gate) { if(prefs.TryGetValue(name,out var old)&&old.Int==value.Int&&old.Float==value.Float&&old.Text==value.Text) return Healthy;
            prefs[name]=value;writeKeys[name]=writeKeys.GetValueOrDefault(name)+1; dirty=true;
            // This achievement counter is readable immediately. Coalesce only its
            // durable write, bounded from the first change rather than the last.
            if(name=="SodasBought"&&value.Int.HasValue&&!value.Float.HasValue&&value.Text==null&&Healthy) {
                if(sodasFlushAt==0)sodasFlushAt=System.Diagnostics.Stopwatch.GetTimestamp()+System.Diagnostics.Stopwatch.Frequency;
            } else Flush();
            return Healthy; } }
        catch(Exception e){Fault(e);return false;}
    }
    // Called every frame, including offline; explicit save/lifecycle barriers use
    // Flush directly. No background writer and no deferred inventory preferences.
    public void Tick()
    {
        lock(gate) {
            if(Healthy&&sodasFlushAt!=0&&System.Diagnostics.Stopwatch.GetTimestamp()>=sodasFlushAt)Flush();
        }
    }
    public void Flush()
    {
        try { lock(gate) {
            if(!dirty) return;
            var started=System.Diagnostics.Stopwatch.GetTimestamp();
            var temp=prefsFile+".tmp";
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)) {
                JsonSerializer.Serialize(stream,prefs); stream.Flush(true);
            }
            if(File.Exists(prefsFile)) File.Replace(temp,prefsFile,prefsFile+".previous",true);
            else File.Move(temp,prefsFile);
            dirty=false;sodasFlushAt=0;
            writes++;writeTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
        } } catch(Exception e){Fault(e);}
    }
    void Fault(Exception e)
    {
        Healthy=false;
        try{Plugin.Instance.Log.LogError("Personal profile write failed; no fallback to original saves: "+e.Message);}catch{/* A failed diagnostic sink must not escape the save-failure path. */}
    }
    public void SnapshotBeforeConnection()
    {
        Flush(); if(!Healthy) throw new IOException("Personal profile storage is unhealthy");
        var root=DirectoryPath+".session-backups"; Directory.CreateDirectory(root);
        var folder=Path.Combine(root,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")); Directory.CreateDirectory(folder);
        foreach(var file in Directory.EnumerateFiles(DirectoryPath,"*",SearchOption.AllDirectories)) {
            var dest=Path.Combine(folder,Path.GetRelativePath(DirectoryPath,file)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file,dest,false);
        }
    }
}
