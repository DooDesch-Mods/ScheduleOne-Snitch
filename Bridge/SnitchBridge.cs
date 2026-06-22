using System;

namespace Snitch.Bridge
{
    /// <summary>
    /// The ONE stable contract between the Snitch host and the modder shim (Snitch.Api). The shim locates this
    /// type by full name via reflection and binds these standard-BCL delegates - so the two assemblies share no
    /// custom type and stay version-independent. NEVER rename this type, its namespace, or these fields without
    /// bumping <see cref="AbiVersion"/>; only ADD fields (additive ABI). Filled by <see cref="BridgeHost"/>.
    /// </summary>
    public static class SnitchBridge
    {
        public const int AbiVersion = 1;

        public static Func<bool> IsEnabled;
        public static Func<string, int> BeginScope;     // label -> token (0 = dropped / not sampling)
        public static Action<int> EndScope;             // token
        public static Action<string> BeginLabel;        // manual begin
        public static Action<string> EndLabel;          // manual end
        public static Action<string, Func<double>, string> RegisterCounter;   // id, read, unit
        public static Action<string> UnregisterCounter;
        public static Action<string, Func<object[]>> RegisterStateProvider;    // id, poll -> [title, string[] names, int[] counts, int total]
        public static Action<string> UnregisterStateProvider;
        public static Action<string> Mark;
        public static Action<string, Action, Action> RegisterAblationLever;    // name, apply(off), restore(on)
    }
}
