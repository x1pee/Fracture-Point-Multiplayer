namespace FractureTogether;

// Cosmetic CPU admission only: one native clone can exceed2ms by itself. This
// limits subsequent work; it cannot preempt an in-progress Unity native call.
internal sealed class RemoteShotFxBudget
{
 internal const int QueueCapacity=32,EffectsPerFrame=4,ColdBuildsPerFrame=1,WarmPerWeapon=2;
 internal const double Milliseconds=2;
 internal const float Lifetime=.25f;
 int frame=-1,effects,cold,assets;double start;
 internal void Begin(int value,double now){if(frame==value)return;frame=value;start=now;effects=cold=assets=0;}
 internal bool Available(double now)=>double.IsFinite(now)&&now>=start&&now-start<Milliseconds;
 internal bool Effect(double now){if(!Available(now)||effects>=EffectsPerFrame)return false;effects++;return true;}
 internal bool Cold(double now){if(!Available(now)||cold>=ColdBuildsPerFrame)return false;cold++;return true;}
 internal bool Asset(double now){if(!Available(now)||assets>=1)return false;assets++;return true;}
 internal void Reset(){frame=-1;effects=cold=assets=0;start=0;}
}
