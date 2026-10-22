namespace FractureTogether;

public sealed partial class Together
{
 // Floor zero is an authored street, not a failed/unbuilt procedural floor.
 // Generated floors continue to require the completed native room model.
 internal bool SharedActorWorldReady=>SharedRun&&!DungeonSeedPatch.Building&&
  (floorLayoutReady||(floor==0&&!DungeonSeedPatch.Latest));
}
