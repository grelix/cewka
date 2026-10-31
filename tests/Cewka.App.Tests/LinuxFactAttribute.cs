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
