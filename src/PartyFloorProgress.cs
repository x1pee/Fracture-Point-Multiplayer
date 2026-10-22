namespace FractureTogether;

public static class PartyFloorProgress
{
 // The authored street and the first generated floor both have native ID 0.
 public static bool Advanced(int before,bool hadLayout,int after,bool hasLayout,bool differentWorld)=>
  before>=0&&after>=before&&hasLayout&&differentWorld&&(after>before||before==0&&!hadLayout);
}
