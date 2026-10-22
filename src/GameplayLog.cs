using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FractureTogether;

// Callers supply stable event IDs and immutable managed scalar/DTO snapshots.
// Never pass Unity objects, nicknames, addresses, passwords or packet payloads.
static class GameplayLog
{
 internal const int QueueCapacity=512,SegmentBytes=2*1024*1024;
 const int MaxRowBytes=64*1024,StopWaitMilliseconds=400;
 static Session? current;
 static string lastFailure="";
 // Called on the writer thread, at most once per failure category/session.
 // Only fixed phase IDs and exception type names are exposed, never messages.
 internal static Action<string,string>? Failure;
 internal static string LastFailure=>Volatile.Read(ref lastFailure);
 internal static int PendingCount=>Volatile.Read(ref current)?.PendingCount??0;
 internal static long DroppedCount=>Volatile.Read(ref current)?.DroppedCount??0;
 internal static bool Operational=>Volatile.Read(ref current)?.Operational==true;

 internal static void Start(string directory,string build,bool enabled)
 {
  Stop();Volatile.Write(ref lastFailure,"");if(!enabled)return;
  var session=new Session(directory,Limit(build,256));
  Volatile.Write(ref current,session);session.Begin();
 }
 internal static void Record(string eventName,object? data=null)=>Volatile.Read(ref current)?.Enqueue(new Row(DateTime.UtcNow,Id(eventName),data));
 internal static void Fault(string scope,Exception error)
 {
  var session=Volatile.Read(ref current);if(session==null||error==null)return;
  var type=Limit(error.GetType().FullName??error.GetType().Name,256);scope=Id(scope);
  if(!session.AcceptFault(scope,type))return;
  string stack;try{stack=Limit(new StackTrace(error,false).ToString(),8192);}catch{stack="<stack unavailable>";}
  // Exception.Message and Exception.ToString are deliberately never accessed.
  session.Enqueue(new Row(DateTime.UtcNow,"fault",new {scope,exceptionType=type,stackTrace=stack}));
 }
 internal static void Stop()
 {
  var session=Interlocked.Exchange(ref current,null);if(session==null)return;
  session.RequestStop();try{session.Worker?.Wait(StopWaitMilliseconds);}catch{ }
 }
 static string Limit(string? value,int limit)=>string.IsNullOrEmpty(value)?"":value.Length<=limit?value:value[..limit];
 static void Failed(string phase,string type)
 {
  Volatile.Write(ref lastFailure,phase+":"+type);
  try{Failure?.Invoke(phase,type);}catch{/* Diagnostics must not fault the writer. */}
 }
 static string Id(string? value)
 {
  if(string.IsNullOrEmpty(value)||value.Length>64)return "invalid.event";
  foreach(var c in value)if(!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' or '/'))return "invalid.event";
  return value;
 }
 sealed record Row(DateTime Utc,string Event,object? Data);
 sealed class Session
 {
  static readonly Regex OwnName=new(@"^(?<session>ft-gameplay-\d{8}T\d{9}Z-[0-9a-f]{8})(?:\.[12])?\.jsonl$",RegexOptions.CultureInvariant);
  static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=12};
  readonly string directory,build;
  readonly Queue<Row> queue=new(QueueCapacity);
  readonly object queueGate=new(),faultGate=new();
  readonly AutoResetEvent wake=new(false);
  readonly FaultSlot[] faultSlots=new FaultSlot[32];
  int count,stopping,disabled,nextFault,faultsInWindow,notices;
  long stopAt,faultWindow,dropped,droppedPending,suppressedPending,serializationPending;
  string sessionId="",path="";FileStream? stream;int segment;
  internal Task? Worker{get;private set;}
  internal int PendingCount=>Volatile.Read(ref count);
  internal long DroppedCount=>Interlocked.Read(ref dropped);
  internal bool Operational=>Volatile.Read(ref disabled)==0&&Volatile.Read(ref stopping)==0;
  struct FaultSlot{internal string? Scope,Type;internal long At;}
  internal Session(string directory,string build){this.directory=directory;this.build=build;}
  internal void Begin()=>Worker=Task.Run(Run);
  void Drop(){Interlocked.Increment(ref dropped);Interlocked.Increment(ref droppedPending);}
  internal void Enqueue(Row row)
  {
   if(!Operational)return;
   if(!Monitor.TryEnter(queueGate)){Drop();return;}
   try{if(!Operational)return;if(queue.Count==QueueCapacity){Drop();return;}queue.Enqueue(row);Volatile.Write(ref count,queue.Count);}
   finally{Monitor.Exit(queueGate);}
   Signal();
  }
  void Signal(){try{wake.Set();}catch(ObjectDisposedException){ }}
  internal void RequestStop(){stopAt=Stopwatch.GetTimestamp();Volatile.Write(ref stopping,1);Signal();}
  internal bool AcceptFault(string scope,string type)
  {
   if(!Operational)return false;
   if(!Monitor.TryEnter(faultGate)){Interlocked.Increment(ref suppressedPending);return false;}
   try{
    var now=Stopwatch.GetTimestamp();if(now-faultWindow>=Stopwatch.Frequency){faultWindow=now;faultsInWindow=0;}
    var slot=-1;
    for(var i=0;i<faultSlots.Length;i++)if(faultSlots[i].Scope==scope&&faultSlots[i].Type==type){slot=i;break;}
    if(faultsInWindow>=8||slot>=0&&now-faultSlots[slot].At<Stopwatch.Frequency*10){Interlocked.Increment(ref suppressedPending);return false;}
    if(slot<0){slot=nextFault;nextFault=(nextFault+1)%faultSlots.Length;}
    faultSlots[slot]=new FaultSlot{Scope=scope,Type=type,At=now};faultsInWindow++;return true;
   }finally{Monitor.Exit(faultGate);}
  }
  bool Take(out Row? row)
  {
   lock(queueGate){if(queue.Count==0){row=null;return false;}row=queue.Dequeue();Volatile.Write(ref count,queue.Count);return true;}
  }
  void Run()
  {
   var phase="initialize";
   try{
    var full=Path.GetFullPath(directory);phase="directory";Directory.CreateDirectory(full);
    phase="retention";Prune(full);
    sessionId="ft-gameplay-"+DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'",System.Globalization.CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N")[..8];
    path=Path.Combine(full,sessionId+".jsonl");phase="open";OpenSegment(true);phase="write";
    var flushAt=Stopwatch.GetTimestamp()+Stopwatch.Frequency*9/10;
    while(true){
     if(Volatile.Read(ref stopping)!=0&&Stopwatch.GetTimestamp()-stopAt>=Stopwatch.Frequency*350/1000)break;
     if(Take(out var row)){Write(row!);}
     else if(Volatile.Read(ref stopping)!=0)break;
     else wake.WaitOne(100);
     if(Stopwatch.GetTimestamp()>=flushAt){Counters();stream!.Flush();flushAt=Stopwatch.GetTimestamp()+Stopwatch.Frequency*9/10;}
    }
    lock(queueGate){var abandoned=queue.Count;queue.Clear();Volatile.Write(ref count,0);Interlocked.Add(ref dropped,abandoned);Interlocked.Add(ref droppedPending,abandoned);}
    Counters();stream!.Flush();
   }catch(Exception error){Volatile.Write(ref disabled,1);Notice(1,phase,error.GetType().FullName??error.GetType().Name);}
   finally{
    Volatile.Write(ref disabled,1);
    lock(queueGate){queue.Clear();Volatile.Write(ref count,0);}
    try{stream?.Dispose();}catch(Exception error){Notice(8,"dispose",error.GetType().FullName??error.GetType().Name);}stream=null;
    wake.Dispose();
   }
  }
  void Write(Row row)
  {
   byte[] bytes;
   try{bytes=JsonSerializer.SerializeToUtf8Bytes(row,Json);if(bytes.Length>MaxRowBytes){Interlocked.Increment(ref serializationPending);Notice(4,"serialize","RowTooLarge");return;}}
   catch(Exception error){Interlocked.Increment(ref serializationPending);Notice(2,"serialize",error.GetType().FullName??error.GetType().Name);return;}
   Append(bytes);
  }
  void Notice(int category,string phase,string type)
  {
   if((Interlocked.Or(ref notices,category)&category)==0)Failed(phase,Limit(type,256));
  }
  void Append(byte[] bytes)
  {
   if(stream!.Position+bytes.Length+1>SegmentBytes){
    stream.Flush();stream.Dispose();stream=null;
    if(File.Exists(Part(2)))File.Delete(Part(2));
    if(File.Exists(Part(1)))File.Move(Part(1),Part(2));
    File.Move(path,Part(1));segment++;OpenSegment(false);
   }
   stream!.Write(bytes,0,bytes.Length);stream.WriteByte((byte)'\n');
  }
  string Part(int index)=>path[..^6]+"."+index+".jsonl";
  void OpenSegment(bool first)
  {
   stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read,32768,FileOptions.SequentialScan);
   // Every retained segment contains build/version, even after the first rolls away.
   Write(new Row(DateTime.UtcNow,first?"session.start":"session.segment",new {version="0.3.0",schema=1,build,sessionId,segment}));
   // Publish the header immediately; this flush runs only on the writer thread.
   stream.Flush();
  }
  void Counters()
  {
   var lost=Interlocked.Exchange(ref droppedPending,0);var suppressed=Interlocked.Exchange(ref suppressedPending,0);var invalid=Interlocked.Exchange(ref serializationPending,0);
   if(lost!=0||suppressed!=0||invalid!=0)Write(new Row(DateTime.UtcNow,"logger.stats",new {dropped=lost,suppressedFaults=suppressed,serializationFailures=invalid}));
  }
  static void Prune(string full)
  {
   // Three older session IDs plus this new one; never touch other filenames.
   var keep=new SortedSet<string>(StringComparer.Ordinal);
   foreach(var file in Directory.EnumerateFiles(full,"ft-gameplay-*.jsonl",SearchOption.TopDirectoryOnly)){
    var match=OwnName.Match(Path.GetFileName(file));if(!match.Success)continue;
    keep.Add(match.Groups["session"].Value);if(keep.Count>3)keep.Remove(keep.Min!);
   }
   foreach(var file in Directory.EnumerateFiles(full,"ft-gameplay-*.jsonl",SearchOption.TopDirectoryOnly)){
    var match=OwnName.Match(Path.GetFileName(file));if(match.Success&&!keep.Contains(match.Groups["session"].Value))File.Delete(file);
   }
  }
 }
}
