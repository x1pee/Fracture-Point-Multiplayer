using UnityEngine;
namespace FractureTogether;

static class PlayerTagUi
{
 static GUIStyle? nameStyle,farNameStyle,metaStyle,healthStyle;
 static Texture2D? healthTexture;
 static readonly Color Healthy=new(.43f,.82f,.53f),Caution=new(.97f,.79f,.32f),Critical=new(.96f,.29f,.27f);
 // All teammate labels share the current rendered frame's camera snapshot.
 // Refresh on every frame, including a null-camera frame; never reuse an old view.
 static int viewFrame=-1,viewWidth,viewHeight;static Camera? viewCamera;
 static Vector3 viewPosition,viewForward;static float viewScale;
 internal readonly struct Projection {
  internal readonly Camera Camera;internal readonly Vector3 Origin,Position,Point;
  internal readonly float Height,Distance,BaseScale;internal readonly int Width,ScreenHeight;
  internal Projection(Camera camera,Vector3 origin,Vector3 position,Vector3 point,float height,float distance,float scale,int width,int screenHeight){Camera=camera;Origin=origin;Position=position;Point=point;Height=height;Distance=distance;BaseScale=scale;Width=width;ScreenHeight=screenHeight;}
 }
 internal static bool TryProject(Packet state,Vector3 position,out Projection projection){
  projection=default;
  if(Event.current?.type!=EventType.Repaint)return false;
  var frame=Time.frameCount;if(viewFrame!=frame){
   viewFrame=frame;viewCamera=Camera.main;
   if(viewCamera){var transform=viewCamera.transform;viewPosition=transform.position;viewForward=transform.forward;viewWidth=Screen.width;viewHeight=Screen.height;viewScale=Mathf.Clamp(viewHeight/900f,.85f,1.25f);}
  }
  if(!viewCamera)return false;
  var toward=position-viewPosition;var square=toward.sqrMagnitude;
  if(square>=65*65||Vector3.Dot(viewForward,toward)<=0)return false;
  var height=state.Pose.Length>5?Mathf.Clamp(state.Pose[5],.65f,2.2f):1.8f;
  var point=viewCamera.WorldToScreenPoint(position+Vector3.up*(height+.28f));
  if(point.z<=0||point.x<20||point.x>viewWidth-20||point.y<20||point.y>viewHeight-20)return false;
  projection=new(viewCamera,viewPosition,position,point,height,Mathf.Sqrt(square),viewScale,viewWidth,viewHeight);return true;
 }
 internal sealed class TextCache
 {
  internal GUIContent? Name,Distance,Health;
  internal float HealthFraction,Opacity,DistanceScale=1,Reveal=1;float lastDrawAt=-1;int lastDrawFrame=-1;
  string? priorName;int priorDistance=int.MinValue,priorHealth=int.MinValue;
  internal void Update(string name,int distance,int health){
   Name??=new GUIContent();Distance??=new GUIContent();Health??=new GUIContent();
   if(priorName!=name){priorName=name;Name.text=name;}
   if(priorDistance!=distance){priorDistance=distance;Distance.text=distance+" m";}
   if(priorHealth!=health){priorHealth=health;Health.text=health+" HP";}
  }
  internal void Animate(float fraction,float alpha,float distanceScale){
   // IMGUI may repaint more than once. Advance these cached values only once
   // per rendered frame; no animation objects, strings or textures are created.
   var frame=Time.frameCount;if(lastDrawFrame==frame)return;lastDrawFrame=frame;
   var now=Time.unscaledTime;var gap=lastDrawAt<0||now-lastDrawAt>.5f;
   if(gap){HealthFraction=fraction;DistanceScale=distanceScale;Opacity=0;Reveal=0;}
   var delta=Mathf.Clamp(Time.unscaledDeltaTime,0,.1f);
   var settle=1-Mathf.Exp(-10*delta);var appear=1-Mathf.Exp(-15*delta);
   HealthFraction=Mathf.Lerp(HealthFraction,fraction,settle);DistanceScale=Mathf.Lerp(DistanceScale,distanceScale,settle);
   Opacity=Mathf.Lerp(Opacity,alpha,appear);Reveal=Mathf.Lerp(Reveal,1,appear);lastDrawAt=now;
  }
 }
 static void Label(Rect rect,GUIContent text,GUIStyle style,float alpha,bool critical=false){
  GUI.color=new Color(0,0,0,.48f*alpha);GUI.Label(new Rect(rect.x+.75f,rect.y+.75f,rect.width,rect.height),text,style);
  GUI.color=critical?new Color(Critical.r,Critical.g,Critical.b,alpha):new Color(1,1,1,alpha);GUI.Label(rect,text,style);
 }
 static void HealthBar(float fraction,float alpha){
  healthTexture??=Texture2D.whiteTexture;
  GUI.color=new Color(.66f,.71f,.75f,.22f*alpha);GUI.DrawTexture(new Rect(0,23,196,2),healthTexture);
  if(fraction<=0)return;
  var color=fraction<.5f?Color.Lerp(Critical,Caution,fraction*2):Color.Lerp(Caution,Healthy,(fraction-.5f)*2);
  color.a=.94f*alpha;GUI.color=color;GUI.DrawTexture(new Rect(0,23,196*fraction,2),healthTexture);
 }
 static float DistanceScale(float distance){
  // Start shrinking beside the player, not at 15 m. Far labels become a
  // compact name/bar, keeping larger source glyphs legible at their small scale.
  if(distance<=5)return Mathf.Lerp(.95f,.70f,Mathf.InverseLerp(1,5,distance));
  if(distance<=15)return Mathf.Lerp(.70f,.50f,Mathf.InverseLerp(5,15,distance));
  return Mathf.Lerp(.50f,.35f,Mathf.InverseLerp(15,40,distance));
 }
 internal static void Draw(Packet state,in Projection projected,TextCache text){
  if(Event.current?.type!=EventType.Repaint)return;
  var point=projected.Point;var distance=projected.Distance;
  text.Update(string.IsNullOrWhiteSpace(state.Name)?"Teammate":state.Name,Mathf.RoundToInt(distance),state.Health);
  if(nameStyle==null){nameStyle=new GUIStyle(GUI.skin.label){fontSize=14,fontStyle=FontStyle.Normal,alignment=TextAnchor.MiddleLeft,clipping=TextClipping.Clip,richText=false};nameStyle.normal.textColor=new Color(.94f,.95f,.96f);farNameStyle=new GUIStyle(nameStyle){fontSize=18};metaStyle=new GUIStyle(nameStyle){fontSize=10};metaStyle.normal.textColor=new Color(.72f,.75f,.78f);healthStyle=new GUIStyle(metaStyle){fontSize=11,alignment=TextAnchor.MiddleRight};healthStyle.normal.textColor=new Color(.94f,.95f,.96f);}
  var knownMaximum=state.MaxHealth>0;var fraction=knownMaximum?Mathf.Clamp01((float)state.Health/state.MaxHealth):0;
  text.Animate(fraction,Mathf.Clamp01((65-distance)/20),DistanceScale(distance));
  var compact=Mathf.SmoothStep(0,1,Mathf.InverseLerp(10,18,distance));var cardHeight=Mathf.Lerp(48,29,compact);
  var scale=projected.BaseScale*text.DistanceScale;var width=196*scale;var margin=8+8*scale;var x=Mathf.Clamp(point.x-width*.5f,margin,projected.Width-width-margin);var y=Mathf.Clamp(projected.ScreenHeight-point.y-(cardHeight-2)*scale+(1-text.Reveal)*3*scale,8+3*scale,projected.ScreenHeight-(cardHeight-3)*scale-8);
  var matrix=GUI.matrix;var color=GUI.color;var alpha=text.Opacity;
  GUI.matrix=Matrix4x4.TRS(new Vector3(x,y,0),Quaternion.identity,new Vector3(scale,scale,1));
  try{
   healthTexture??=Texture2D.whiteTexture;
   // A low-opacity rounded surface keeps the scene readable behind the tag.
   // The pinned engine's rounded GUI overload uses its existing UI material.
   GUI.color=Color.white;GUI.DrawTexture(new Rect(-8,-3,212,cardHeight),healthTexture,ScaleMode.StretchToFill,true,0,new Color(.09f,.105f,.125f,.24f*alpha),0,7);
   GUI.color=new Color(.75f,.81f,.88f,.035f*alpha);GUI.DrawTexture(new Rect(-1,-3,198,1),healthTexture);
   if(compact<1)Label(new Rect(0,0,134,20),text.Name!,nameStyle,alpha*(1-compact));
   if(compact>0)Label(new Rect(0,0,196,22),text.Name!,farNameStyle!,alpha*compact);
   if(compact<1)Label(new Rect(134,0,62,20),text.Health!,healthStyle!,alpha*(1-compact),knownMaximum&&fraction<=.15f);
   // A legacy/QA state with unknown maximum shows the absolute HP only. Do
   // not imply 100 HP or draw an empty bar that would falsely mean zero HP.
   if(knownMaximum)HealthBar(text.HealthFraction,alpha);
   if(compact<1)Label(new Rect(0,28,196,14),text.Distance!,metaStyle!,alpha*(1-compact));
  }finally{GUI.matrix=matrix;GUI.color=color;}
 }
}
