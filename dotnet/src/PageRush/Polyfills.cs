#if !NET
// Lets the compiler emit init-only setters when targeting netstandard2.0.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
#endif
