using System.Net;
using System.Runtime.CompilerServices;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Atrapy stoją na adresach lokalnych, więc żądania do nich nie mogą iść przez serwer pośredniczący
/// programisty. Testy pośrednika podają go jawnie.
/// </summary>
public class TestProxyIsolation
{
#pragma warning disable CA2255 // Właśnie tu: przed pierwszym odczytem ustawienia z otoczenia.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void IgnoreTheProxyOfTheEnvironment() => HttpClient.DefaultProxy = new WebProxy();

    [Fact]
    public void ProxyOfTheEnvironmentDoesNotReachTheTests()
    {
        Assert.True(HttpClient.DefaultProxy.IsBypassed(new Uri("http://stacja.test/")));
    }
}
