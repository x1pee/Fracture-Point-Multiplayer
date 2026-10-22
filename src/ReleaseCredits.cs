using System.Text;
namespace FractureTogether;

// Compiled F11 attribution, decoded once. No external config or gameplay hook.
// This only discourages casual string editing; a local DLL cannot be tamper-proof.
internal static class ReleaseCredits
{
 internal static readonly string Author=Read(new byte[]{32,12,9,8,77,15,20,77,25,67,0,8,66,21,92,29,8,8});
 internal static readonly string Site=Read(new byte[]{29,0,21,29,67,11,24,3});
 internal static readonly string AuthorUrl=Read(new byte[]{5,25,25,29,30,87,66,66,25,67,0,8,66,21,92,29,8,8});
 internal static readonly string SiteUrl=Read(new byte[]{5,25,25,29,30,87,66,66,29,0,21,29,67,11,24,3});
 static string Read(byte[] value){for(var i=0;i<value.Length;i++)value[i]^=109;return Encoding.UTF8.GetString(value);}
}
