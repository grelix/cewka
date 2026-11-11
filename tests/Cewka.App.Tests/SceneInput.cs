using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

namespace Cewka.App.Tests.SceneInput;

/// <summary>
/// Zdarzenia wejścia z jednym wymuszeniem klatki przed i po, tak jak pomocniki okna bez ekranu przy
/// nieruchomej treści. Pomocniki wymuszają klatki, dopóki dyspozytor ma zadania, najwyżej po 10
/// z każdej strony, a przy kręcącej się płycie zadania są zawsze, więc jedno zdarzenie kosztowało
/// kilkadziesiąt klatek sceny. Zegar okna tyka też sam w czasie zadań, ale tylko raz na sekundę
/// (<see cref="TestApp.BuildAvaloniaApp"/>), więc zwykle są dokładnie te dwie klatki.
/// </summary>
internal static class SceneInputExtensions
{
    private static readonly Type Input =
        typeof(HeadlessWindowExtensions).Assembly.GetType("Avalonia.Headless.IHeadlessWindow", throwOnError: true)!;

    public static void KeyPress(this TopLevel window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? keySymbol) =>
        Send(window, nameof(KeyPress), key, modifiers, physicalKey, keySymbol);

    public static void KeyRelease(this TopLevel window, Key key, RawInputModifiers modifiers, PhysicalKey physicalKey, string? keySymbol) =>
        Send(window, nameof(KeyRelease), key, modifiers, physicalKey, keySymbol);

    public static void MouseDown(this TopLevel window, Point point, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Send(window, nameof(MouseDown), point, button, modifiers);

    public static void MouseMove(this TopLevel window, Point point, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Send(window, nameof(MouseMove), point, modifiers);

    public static void MouseUp(this TopLevel window, Point point, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Send(window, nameof(MouseUp), point, button, modifiers);

    public static void MouseWheel(this TopLevel window, Point point, Vector delta, RawInputModifiers modifiers = RawInputModifiers.None) =>
        Send(window, nameof(MouseWheel), point, delta, modifiers);

    private static void Send(TopLevel window, string method, params object?[] arguments)
    {
        Frame();
        Input.GetMethod(method)!.Invoke(window.PlatformImpl, BindingFlags.DoNotWrapExceptions, null, arguments, null);
        Frame();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Frame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
