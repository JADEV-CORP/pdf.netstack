#if !NET8_0_OR_GREATER
// The nullable flow attributes .NET Standard 2.0 lacks. Internal, so they never collide with the
// real types in consumers that target newer frameworks; the compiler matches them by name.
namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>The out parameter is not null when the method returns <see cref="ReturnValue"/>.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        public bool ReturnValue { get; }
    }
}
#endif
