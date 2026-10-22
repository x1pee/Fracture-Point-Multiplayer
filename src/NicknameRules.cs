using System.Buffers;
using System.Globalization;
using System.Text;

namespace FractureTogether;

// Network names and the local text field share the same Unicode boundaries.
// StringInfo uses extended grapheme clusters, including emoji ZWJ sequences.
public static class NicknameRules
{
 public const int MaxCharacters=20;
 public const int MaxUtf16Length=512;
 public const int MaxUtf8Bytes=1024;
 public const string Fallback="Player";

 public static string Limit(string? value)
 {
  if(string.IsNullOrEmpty(value))return string.Empty;
  var filtered=new StringBuilder(Math.Min(value.Length,MaxUtf16Length));
  for(var index=0;index<value.Length;){
   var status=Rune.DecodeFromUtf16(value.AsSpan(index),out var rune,out var consumed);
   if(status!=OperationStatus.Done){index++;continue;}
   if(Allowed(rune))filtered.Append(value,index,consumed);
   index+=consumed;
  }
  var text=filtered.ToString();var elements=StringInfo.GetTextElementEnumerator(text);var end=0;var bytes=0;var count=0;
  while(count<MaxCharacters&&elements.MoveNext()){
   var element=elements.GetTextElement();var next=elements.ElementIndex+element.Length;var size=Encoding.UTF8.GetByteCount(element);
   if(next>MaxUtf16Length||bytes+size>MaxUtf8Bytes)break;
   end=next;bytes+=size;count++;
  }
  return end==text.Length?text:text[..end];
 }

 public static string Clean(string? value)
 {
  var name=Limit(value).Trim();return name.Length==0?Fallback:name;
 }

 public static bool IsValid(string? value)
 {
  if(string.IsNullOrWhiteSpace(value)||value.Length>MaxUtf16Length)return false;
  for(var index=0;index<value.Length;){
   if(Rune.DecodeFromUtf16(value.AsSpan(index),out var rune,out var consumed)!=OperationStatus.Done||!Allowed(rune))return false;
   index+=consumed;
  }
  if(Encoding.UTF8.GetByteCount(value)>MaxUtf8Bytes)return false;
  var elements=StringInfo.GetTextElementEnumerator(value);var count=0;
  while(elements.MoveNext())if(++count>MaxCharacters)return false;
  return count>0;
 }

 static bool Allowed(Rune rune)=>Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);
}
