using System.Runtime.CompilerServices;
using Cewka.Audio.Devices;

namespace Cewka.Audio.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Żaden test w tym zestawie nie gra przez prawdziwe urządzenie: zaplecze puste wybiera
    /// się, zanim cokolwiek dotknie dźwięku.
    /// </summary>
    [ModuleInitializer]
    internal static void UseNullOutput() => AudioDevice.UseNullOutput = true;
}
