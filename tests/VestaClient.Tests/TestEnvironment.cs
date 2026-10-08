using System.Runtime.CompilerServices;
using Vesta;

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
