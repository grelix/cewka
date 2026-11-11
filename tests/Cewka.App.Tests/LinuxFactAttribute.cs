using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenie przypadku, który zachodzi tylko pod Linuksem. Na innym systemie przebieg pokazuje
/// je jako pominięte, a nie zaliczone, żeby wynik nie udawał sprawdzenia.
/// </summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Przypadek zachodzi tylko pod Linuksem.";
    }
}

/// <summary>
/// Sprawdzenie na prawdziwej szynie sesji D-Bus. Pomijane poza Linuksem i tam, gdzie szyny nie ma
/// (konsola, kontener bez <c>dbus-daemon</c>).
/// </summary>
public sealed class LinuxSessionBusFactAttribute : FactAttribute
{
    public LinuxSessionBusFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Przypadek zachodzi tylko pod Linuksem.";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            Skip = "Brak szyny sesji D-Bus (DBUS_SESSION_BUS_ADDRESS jest puste).";
    }
}
