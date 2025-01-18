// All other files in the project are file-scoped, which means that the namespace in the files cannot be
// changed. This file is meant for injecting dummy classes and changed between net472 and current version.

// When compiling against targets that are before net5.0, this type needs to be manually defined.
// https://developercommunity.visualstudio.com/t/error-cs0518-predefined-type-systemruntimecompiler/1244809
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit {}
}
#endif // !NET5_0_OR_GREATER