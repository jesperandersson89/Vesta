using System.Runtime.CompilerServices;

namespace VestaClient.Tests;

internal static class TestEnvironment
{
    // Tests that exhaust every relay must never open a real browser window.
    [ModuleInitializer]
    internal static void DisableRelayPickerLaunch()
    {
        VestaConnection.RelayPickerEnabled = false;
    }
}
