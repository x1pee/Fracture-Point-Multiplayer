// IL2CPP-exported game assemblies expose stub Nullable attributes whose generated
// constructors cannot be used by Roslyn. Keep the standard metadata attributes local.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple=false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) { NullableFlags = new[] { flag }; }
        public NullableAttribute(byte[] flags) { NullableFlags = flags; }
    }
    [AttributeUsage(AttributeTargets.All, AllowMultiple=false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) { Flag=flag; }
    }
}
