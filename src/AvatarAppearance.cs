using UnityEngine;
namespace FractureTogether;

static class AvatarAppearance
{
 static float nextCapture;static OutfitEntry[] cached=Array.Empty<OutfitEntry>();static IntPtr sourcePointer;
 static readonly HashSet<string> captureNames=new(128,StringComparer.Ordinal);
 static readonly List<(string Name,bool Enabled)> captureEntries=new(128);
 internal static PlayerClothesManager? Source(){
  var player=Together.Current?.LocalController;
  if(player){var own=player.GetComponent<PlayerClothesManager>()??player.GetComponentInChildren<PlayerClothesManager>(true);if(own)return own;}
  // The native FPSGenericPlayer already carries a complete body in the hub.
  // Include inactive objects/prefabs; an enemy does not have to spawn first.
  return Resources.FindObjectsOfTypeAll<PlayerClothesManager>()
   .Where(x=>x&&x.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length>0&&!x.transform.root.name.StartsWith("FractureTogether.",StringComparison.Ordinal))
   .OrderByDescending(x=>x.gameObject.scene.IsValid()&&x.gameObject.scene.isLoaded)
   .ThenByDescending(x=>x.gameObject.activeInHierarchy).FirstOrDefault();
 }
 internal static OutfitEntry[] Capture(){
  if(Time.unscaledTime<nextCapture)return cached;nextCapture=Time.unscaledTime+.5f;
  var source=Source();if(!source)return cached;
  try{
   // Native GetOutfit reports enabledState rather than activeSelf: the local
   // player's chosen headgear is deliberately hidden only in first person.
   var items=source!.GetOutfit();var same=true;
   for(var i=0;i<items.Length;i++){
    var item=items[i];var name=item.Name;
    // Preserve the native order and the first duplicate's enabled state. Read
    // each native Name only once; the getter marshals it into managed text.
    if(name==null||!captureNames.Add(name))continue;
    if(name.Length is <1 or >96||captureEntries.Count>=128)return cached;
    foreach(var c in name)if(char.IsControl(c))return cached;
    var enabled=item.Enabled;var index=captureEntries.Count;captureEntries.Add((name,enabled));
    if(index>=cached.Length||cached[index].Name!=name||cached[index].Enabled!=enabled)same=false;
   }
   if(captureEntries.Count==0)return cached;
   if(same&&captureEntries.Count==cached.Length){sourcePointer=source.Pointer;return cached;}
   var values=new OutfitEntry[captureEntries.Count];
   for(var i=0;i<values.Length;i++){var item=captureEntries[i];values[i]=new OutfitEntry{Name=item.Name,Enabled=item.Enabled};}
   // Changed snapshots still pass the wire validator. Unchanged snapshots have
   // already passed its same name/count/uniqueness checks without new buffers.
   if(OutfitEntry.Validate(values)){cached=values;sourcePointer=source.Pointer;}
  }catch(Exception e){if(sourcePointer!=source!.Pointer){sourcePointer=source.Pointer;Plugin.Instance.Log.LogWarning("Outfit is not ready yet: "+e.Message);}}
  finally{captureNames.Clear();captureEntries.Clear();}
  return cached;
 }
}
