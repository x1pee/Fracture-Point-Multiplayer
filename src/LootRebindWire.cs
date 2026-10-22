namespace FractureTogether;

// Native-free bounds for the host's one-time world-item identity correction.
internal static class LootRebindWire
{
 internal static bool Key(string? value)=>value is {Length:64}&&value.All(c=>c is >= '0' and <= '9' or >= 'A' and <= 'F');
 internal static bool Position(float[]? value)=>value is {Length:3}&&value.All(v=>float.IsFinite(v)&&Math.Abs(v)<=1000000);
 internal static bool Valid(Packet packet)=>packet.Category==0&&packet.Count==0&&packet.ItemId is >=0 and <=100000&&packet.TargetId is >=1 and <=1000000&&Key(packet.ObjectId)&&Key(packet.MachineId)&&packet.ObjectId!=packet.MachineId&&Position(packet.Pose);
}
