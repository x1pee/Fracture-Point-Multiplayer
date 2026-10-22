namespace FractureTogether;

// Catalogue identities only: peers cannot name files, scripts or arbitrary prefabs.
public sealed class ItemDropFrame
{
 public int Type {get;set;}
 public int ItemId {get;set;}
 public int Ammo {get;set;}
 public int MagazineAmmo {get;set;}
 public int ValueFactor {get;set;}=1;
 public bool Valid()=>Type is >=0 and <=8&&ItemId is >=0 and <=100000&&Ammo is >=0 and <=10000&&MagazineAmmo is >=0 and <=1000&&ValueFactor is >=1 and <=6;
}
