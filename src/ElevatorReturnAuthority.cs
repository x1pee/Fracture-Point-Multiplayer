using HarmonyLib;
using System.Reflection;

namespace FractureTogether;

// The native Down-button inlines ReturnToHub's async startup. Its first
// MoveNext is the common authority boundary for both native callers and the
// public wrapper. No public ReturnToHub authority prefix may run alongside it.
[HarmonyPatch]
static class ElevatorReturnAuthority
{
 static PropertyInfo owner=null!,state=null!,builder=null!;
 static MethodBase TargetMethod(){
  const BindingFlags flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
  var machine=typeof(LevelElevator).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic)
   .Single(t=>t.Name.StartsWith("_ReturnToHub_d__",StringComparison.Ordinal)||t.Name.Contains("<ReturnToHub>d__",StringComparison.Ordinal));
  var properties=machine.GetProperties(flags);
  owner=properties.Single(p=>p.PropertyType==typeof(LevelElevator)&&p.CanRead);
  state=properties.Single(p=>p.Name=="__1__state"&&p.PropertyType==typeof(int)&&p.CanRead&&p.CanWrite);
  builder=properties.Single(p=>p.PropertyType==typeof(Il2CppSystem.Runtime.CompilerServices.AsyncVoidMethodBuilder)&&p.CanRead&&p.CanWrite);
  return machine.GetMethod("MoveNext",flags)??throw new MissingMethodException(machine.FullName,"MoveNext");
 }
 static bool Prefix(object __instance){
  var current=Together.Current;
  // Solo play (including an empty host lobby) owns its native return flow.
  // Bypass before inspecting the IL2CPP async state, not only in ElevatorMove.
  // A disconnected co-op player still waits for the personal-save recovery.
  if(current==null||current.Replay||(!current.Connected&&!current.Recovering))return true;
  try{
   if(state.GetValue(__instance) is not int phase)throw new InvalidOperationException("Native elevator return state unavailable");
   // The pinned native method resumes with state0/1. Check authority only at
   // its initial state-1; awaited continuations must not re-send travel.
   if(phase!=-1)return phase!=-2;
   var selected=owner.GetValue(__instance) as LevelElevator;
   if(selected&&current.ElevatorMove(-1,selected))return true;
  }catch(Exception error){GameplayLog.Fault("native_elevator_return_authority",error);}
  CompleteBlockedInitial(__instance);
  return false;
 }
 static void CompleteBlockedInitial(object machine){
  try{
   if(state.GetValue(machine) is not int initial||initial!=-1)return;
   // Nothing has awaited or changed the native return state yet. Mark it
   // completed before SetResult, matching the cooperative death cancellation.
   state.SetValue(machine,-2);
   var completion=builder.GetValue(machine) as Il2CppSystem.Runtime.CompilerServices.AsyncVoidMethodBuilder;
   if(completion==null)throw new InvalidOperationException("Native elevator return builder unavailable");
   // Interop exposes a boxed native value-type copy: persist builder changes.
   try{completion.SetResult();}finally{builder.SetValue(machine,completion);}
  }catch(Exception error){GameplayLog.Fault("native_elevator_return_cancel",error);}
 }
}
