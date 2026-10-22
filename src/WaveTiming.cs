namespace FractureTogether;

// Native-free math: every duration comes from a fresh native countdown/yield.
internal static class WaveTiming
{
 internal static float Rate(int players)=>players switch {2=>1.3f,3=>1.6f,>=4=>2f,_=>1f};
 internal static float Interval(float nativeSeconds,int players)=>float.IsFinite(nativeSeconds)&&nativeSeconds>0?nativeSeconds/Rate(players):nativeSeconds;
}
