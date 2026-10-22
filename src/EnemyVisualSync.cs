using EnemyAI;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
namespace FractureTogether;

// Every layer and persistent parameter is needed: the guards use a weapon layer
// and ChangeWeapon transition in addition to the locomotion state on layer zero.
public sealed partial class Together
{
 sealed class EnemyAnimationCache
 {
  public IntPtr Controller;public string Name="";public int[] Hashes=Array.Empty<int>();public AnimatorControllerParameterType[] Types=Array.Empty<AnimatorControllerParameterType>();public Dictionary<int,AnimatorControllerParameterType> ByHash=new();
 }
 static readonly HashSet<string> unsupportedEnemyParameters=new(StringComparer.Ordinal),enemyAnimationWarnings=new(StringComparer.Ordinal);
 static void EnemyAnimationWarning(string key,Exception? error=null){if(enemyAnimationWarnings.Add(key))Plugin.Instance.Log.LogWarning("NPC visual synchronization using available layer state: "+key+(error==null?"":"; "+error.GetType().Name+": "+error.Message));}
 static void DisableEnemyParameters(EnemyAnimationCache cache,Exception error){unsupportedEnemyParameters.Add(cache.Name);cache.Hashes=Array.Empty<int>();cache.Types=Array.Empty<AnimatorControllerParameterType>();cache.ByHash.Clear();EnemyAnimationWarning(cache.Name+" parameters unavailable",error);}
 [HideFromIl2Cpp]
 static EnemyAnimationCache CachedEnemyAnimation(SyncedEnemy enemy){
  var controller=enemy.Animator!.runtimeAnimatorController;var pointer=controller.Pointer;if(enemy.AnimationCache?.Controller==pointer)return enemy.AnimationCache;
  var cache=new EnemyAnimationCache{Controller=pointer,Name=controller.name};if(!unsupportedEnemyParameters.Contains(cache.Name)){if(EnemyAnimatorParameters.Controllers.TryGetValue(cache.Name,out var parameters)){cache.Hashes=parameters.Select(p=>unchecked((int)p.Hash)).ToArray();cache.Types=parameters.Select(p=>(AnimatorControllerParameterType)p.Type).ToArray();for(var i=0;i<cache.Hashes.Length;i++)cache.ByHash[cache.Hashes[i]]=cache.Types[i];}else EnemyAnimationWarning(cache.Name+" has no pinned parameter metadata");}return enemy.AnimationCache=cache;
 }
 [HideFromIl2Cpp]
 static Animator? EnemyAnimator(EnemyHealth health,GameObject root)=>health.GetComponent<EnemyAI.EnemyAnimation>()?.anim??health.GetComponent<Animator>()??root.GetComponentInChildren<Animator>(true);
 [HideFromIl2Cpp]
 static EnemyAnimationFrame? CaptureEnemyAnimation(SyncedEnemy enemy){
  try{
  var animator=enemy.Animator;
  if(!animator||!animator.runtimeAnimatorController)return null;
  var layers=Math.Min(8,animator.layerCount);var f=new EnemyAnimationFrame{States=new int[layers],Times=new float[layers],Weights=new float[layers]};
  for(var i=0;i<layers;i++){var state=animator.GetCurrentAnimatorStateInfo(i);var weight=animator.GetLayerWeight(i);f.States[i]=state.fullPathHash;f.Times[i]=float.IsFinite(state.normalizedTime)?state.normalizedTime%1:0;f.Weights[i]=float.IsFinite(weight)?Mathf.Clamp01(weight):0;}
  var cache=CachedEnemyAnimation(enemy);if(unsupportedEnemyParameters.Contains(cache.Name))return f;f.ParameterHashes=cache.Hashes;f.ParameterValues=new float[cache.Hashes.Length];try{for(var i=0;i<cache.Hashes.Length;i++){var hash=cache.Hashes[i];var type=cache.Types[i];var value=type==AnimatorControllerParameterType.Float?animator.GetFloat(hash):type==AnimatorControllerParameterType.Int?animator.GetInteger(hash):animator.GetBool(hash)?1f:0f;if(!float.IsFinite(value)||Math.Abs(value)>1000000)throw new InvalidOperationException("Native animator parameter is outside packet limits");f.ParameterValues[i]=value;}}catch(Exception error){DisableEnemyParameters(cache,error);f.ParameterHashes=Array.Empty<int>();f.ParameterValues=Array.Empty<float>();}return f;
  }catch(Exception error){EnemyAnimationWarning(enemy.Prefab+" capture unavailable",error);return null;}
 }
 [HideFromIl2Cpp]
 static void ApplyEnemyAnimation(SyncedEnemy enemy,EnemyFrame frame){
  try{
  var animator=enemy.Animator;if(frame.Dead||!animator||!animator.runtimeAnimatorController)return;
  animator.enabled=true;animator.applyRootMotion=false;animator.cullingMode=AnimatorCullingMode.AlwaysAnimate;
  if(frame.Visual is {} f){
   var cache=CachedEnemyAnimation(enemy);var parameters=cache.ByHash;
   if(!unsupportedEnemyParameters.Contains(cache.Name))try{for(var i=0;i<f.ParameterHashes.Length;i++){var hash=f.ParameterHashes[i];if(!parameters.TryGetValue(hash,out var type))continue;var value=f.ParameterValues[i];if(type==AnimatorControllerParameterType.Float)animator.SetFloat(hash,value);else if(type==AnimatorControllerParameterType.Int)animator.SetInteger(hash,(int)value);else if(type==AnimatorControllerParameterType.Bool)animator.SetBool(hash,value>.5f);}}catch(Exception error){DisableEnemyParameters(cache,error);}
   for(var i=0;i<Math.Min(f.States.Length,animator.layerCount);i++){animator.SetLayerWeight(i,f.Weights[i]);var state=animator.GetCurrentAnimatorStateInfo(i);if(f.States[i]!=0&&(state.fullPathHash!=f.States[i]||Math.Abs((state.normalizedTime%1)-(f.Times[i]%1))>.2f))animator.Play(f.States[i],i,f.Times[i]%1);}
  }else if(frame.Animation!=0){var state=animator.GetCurrentAnimatorStateInfo(0);if(state.fullPathHash!=frame.Animation||Math.Abs((state.normalizedTime%1)-(frame.AnimationTime%1))>.2f)animator.Play(frame.Animation,0,frame.AnimationTime%1);}
  }catch(Exception error){EnemyAnimationWarning(enemy.Prefab+" apply unavailable",error);}
 }
}
