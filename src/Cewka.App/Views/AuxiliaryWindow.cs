using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Cewka.App.Controls;
using Cewka.App.Models;

namespace Cewka.App.Views;

/// <summary>
/// Okno pomocnicze bez ramki, jak okno główne, i niemodalne: ustawienia oraz korektor z efektami.
/// Wspólne są pasek tytułu z przyciskami okna po wybranej stronie, przeciąganie i zmiana rozmiaru
/// bez ramki systemowej, pierwsze otwarcie na środku okna głównego i zapamiętane położenie, Esc
/// zamykający okno i skróty odtwarzania przekazywane do okna głównego.
/// </summary>
public class AuxiliaryWindow : Window
{
    /// <summary>Pas przy krawędzi okna do zmiany rozmiaru, jak w oknie głównym.</summary>
    private const double ResizeMargin = 6;

    /// <summary>Zapas na niewidoczną ramkę systemową przy sprawdzaniu zapamiętanego położenia.</summary>
    private const double FrameTolerance = 8;

    private Control? _titleBar;
    private WindowButtons? _controlsLeft;
    private WindowButtons? _controlsRight;
    private Func<WindowGeometry?> _loadGeometry = () => null;
    private Action<WindowGeometry> _saveGeometry = _ => { };

    /// <summary>Zapamiętany rozmiar wraca razem z położeniem (okno korektora); ustawienia mają stały rozmiar.</summary>
    private bool _restoreSize;

    /// <summary>Ramka systemowa w Windows, jak w oknie głównym.</summary>
    protected bool SystemFrame { get; private set; }

    /// <summary>Obsługa skrótów odtwarzania z okna głównego; zwraca prawdę, gdy klawisz był skrótem.</summary>
    public Func<KeyEventArgs, bool>? PlaybackShortcuts { get; init; }

    /// <summary>
    /// Podłącza części okna z XAML okna pochodnego; woła się raz, po <c>InitializeComponent</c>.
    /// Zapamiętane położenie czyta i zapisuje okno pochodne we własnym polu ustawień.
    /// </summary>
    protected void UseChrome(Control titleBar, Border shell, WindowButtons controlsLeft, WindowButtons controlsRight,
        Func<WindowGeometry?> loadGeometry, Action<WindowGeometry> saveGeometry, bool restoreSize = false)
    {
        _restoreSize = restoreSize;
        _titleBar = titleBar;
        _controlsLeft = controlsLeft;
        _controlsRight = controlsRight;
        _loadGeometry = loadGeometry;
        _saveGeometry = saveGeometry;

        Icon = AppIcon.Window;
        ApplyControlsPosition();

        SystemFrame = OperatingSystem.IsWindows();
        if (SystemFrame)
        {
            WindowDecorations = WindowDecorations.BorderOnly;
            WindowDecorationProperties.SetElementRole(titleBar, WindowDecorationsElementRole.TitleBar);
            TitleBarRoles.Apply(titleBar);
            shell.Classes.Add("square");
        }
    }

    /// <summary>Przyciski okna po stronie wybranej w ustawieniach; woła się też po zmianie ustawienia.</summary>
    public void ApplyControlsPosition()
    {
        if (_controlsLeft is null || _controlsRight is null) return;

        var position = App.Settings.Current.WindowControls;
        var onRight = position == WindowControlsPosition.Right;

        _controlsLeft.Position = position;
        _controlsRight.Position = position;

        _controlsLeft.IsVisible = !onRight;
        _controlsRight.IsVisible = onRight;
    }

    // ================= Położenie =================

    /// <summary>Okno główne, nad którym okno stoi na środku; puste, gdy wróciło zapamiętane położenie.</summary>
    private Window? _centreOver;

    /// <summary>Położenie nadane przy środkowaniu. Inne położenie znaczy, że okno przesunięto.</summary>
    private PixelPoint _centredAt;

    /// <summary>Okno już pokazane: jego własna ramka systemowa jest znana.</summary>
    private bool _opened;

    /// <summary>
    /// Ustawia okno przed pokazaniem. Zapamiętane położenie wraca, jeśli okno mieści się na
    /// którymś z ekranów. Inaczej — przy pierwszym otwarciu i gdy zapamiętane miejsce leży poza
    /// ekranami — okno staje na środku okna głównego, jak okna ustawień w macOS i GNOME, przycięte
    /// tak, żeby całe leżało w obszarze roboczym ekranu okna głównego. Środek liczy się jeszcze raz
    /// po pokazaniu i po zmianie wysokości przez okno pochodne, dopóki nikt okna nie przesunie.
    /// </summary>
    public void PlaceOver(Window owner)
    {
        _centreOver = null;

        if ((Screens.ScreenFromWindow(owner) ?? Screens.Primary) is not { } screen)
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            return;
        }

        var scale = screen.Scaling;
        var saved = _loadGeometry();

        // Zapamiętany rozmiar wraca razem z położeniem, jeśli nie jest mniejszy od najmniejszego.
        if (_restoreSize && saved is not null && saved.Width >= MinWidth && saved.Height >= MinHeight)
        {
            Width = saved.Width;
            Height = saved.Height;
        }

        // Na małym ekranie (np. 1366×768 przy 125%) okno o wysokości z projektu wystawałoby
        // poza obszar roboczy i nigdy nie wróciłoby w zapamiętane miejsce.
        Width = FitLength(Width, MinWidth, screen.WorkingArea.Width / scale);
        Height = FitLength(Height, MinHeight, screen.WorkingArea.Height / scale);

        var size = new PixelSize((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));

        if (saved is not null
            && FitsOn(new PixelRect(new PixelPoint(saved.X, saved.Y), size),
                      Screens.All.Select(s => s.WorkingArea), (int)(FrameTolerance * scale)))
        {
            Position = new PixelPoint(saved.X, saved.Y);
            return;
        }

        _centreOver = owner;
        CentreOverOwner();
    }

    /// <summary>
    /// Stawia okno na środku okna głównego. Liczą się prostokąty treści, bez niewidocznej ramki
    /// systemowej do zmiany rozmiaru, którą w Windows obejmuje <see cref="Window.Position"/>: ramka
    /// zmaksymalizowanego okna głównego wystaje poza ekran także u góry, a ramka okna pomocniczego nie.
    /// Przed pokazaniem ramka okna pomocniczego nie jest jeszcze znana i zastępuje ją ramka okna
    /// głównego, tego samego rodzaju; po pokazaniu <see cref="OnOpened"/> liczy położenie od nowa.
    /// </summary>
    private void CentreOverOwner()
    {
        if (_centreOver is not { } owner) return;
        if ((Screens.ScreenFromWindow(owner) ?? Screens.Primary) is not { } screen) return;

        var ownerInset = FrameInset(owner);
        var ownerContent = new PixelRect(
            new PixelPoint(owner.Position.X + ownerInset.X, owner.Position.Y + ownerInset.Y),
            new PixelSize((int)Math.Round(owner.ClientSize.Width * owner.RenderScaling),
                          (int)Math.Round(owner.ClientSize.Height * owner.RenderScaling)));

        // Przy pokazaniu Avalonia przywraca na chwilę wysokość z projektu, a okno i tak nie będzie
        // niższe od najmniejszej, którą korektor ustawił już według treści.
        var size = new Size(Math.Max(Width, MinWidth), Math.Max(Height, MinHeight));
        var content = CentreOver(ownerContent, size, screen.Scaling, screen.WorkingArea);
        var inset = _opened ? FrameInset(this) : ownerInset;

        Position = new PixelPoint(content.X - inset.X, content.Y - inset.Y);
        _centredAt = Position;
    }

    /// <summary>
    /// Avalonia stawia okno z <c>WindowStartupLocation.Manual</c> w położeniu nadanym przed pokazaniem,
    /// ale okno główne mogło się od tej chwili zmienić (okno korektora otwarte w czasie sceny pokazuje
    /// się dopiero po niej), a własną ramkę okno zna dopiero teraz. Okno na środku liczy się więc
    /// od nowa, a okno w zapamiętanym miejscu zostaje, gdzie było. Tylko przy pierwszym pokazaniu:
    /// okno korektora chowa się na czas sceny i pokazuje po niej znowu (OnOpened przychodzi ponownie),
    /// a przesunięte w tym czasie ręcznie ma zostać tam, gdzie je postawiono.
    /// </summary>
    protected override void OnOpened(EventArgs e)
    {
        var first = !_opened;
        _opened = true;
        base.OnOpened(e);
        if (first && _centreOver is not null) CentreOverOwner();
    }

    /// <summary>
    /// Woła okno pochodne po zmianie własnej wysokości (korektor dopasowuje ją do treści). Okno
    /// postawione na środku, którego nikt nie przesunął, zostaje na środku okna głównego. Inne,
    /// np. w zapamiętanym miejscu, zostaje w miejscu, ale nie wychodzi dołem poza obszar roboczy.
    /// </summary>
    protected void KeepPlacementAfterResize()
    {
        if (_centreOver is not null && Position == _centredAt)
        {
            CentreOverOwner();
            return;
        }

        if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is not { } screen) return;

        var area = screen.WorkingArea;
        var bottom = Position.Y + (int)Math.Ceiling(Height * screen.Scaling);
        if (bottom > area.Bottom)
            Position = new PixelPoint(Position.X, Math.Max(area.Y, Position.Y - (bottom - area.Bottom)));
    }

    /// <summary>Długość okna przycięta do dostępnego miejsca, ale nie poniżej minimum.</summary>
    internal static double FitLength(double length, double minimum, double available) =>
        Math.Max(minimum, Math.Min(length, available));

    /// <summary>
    /// Położenie treści okna o rozmiarze <paramref name="size"/> (jednostki) na środku treści okna
    /// głównego <paramref name="owner"/> (piksele), przycięte do obszaru roboczego <paramref name="area"/>
    /// tak, żeby okno leżało w nim całe. Okno większe od obszaru staje przy jego lewej albo górnej
    /// krawędzi, żeby pasek tytułu dało się chwycić.
    /// </summary>
    internal static PixelPoint CentreOver(PixelRect owner, Size size, double scaling, PixelRect area)
    {
        var width = (int)Math.Ceiling(size.Width * scaling);
        var height = (int)Math.Ceiling(size.Height * scaling);

        var x = owner.X + (owner.Width - width) / 2;
        var y = owner.Y + (owner.Height - height) / 2;

        return new PixelPoint(
            Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width)),
            Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height)));
    }

    /// <summary>Przesunięcie treści okna względem jego położenia, w pikselach.</summary>
    private static PixelPoint FrameInset(Window window) =>
        FrameInset(window.FrameSize, window.ClientSize, window.RenderScaling);

    /// <summary>
    /// Przesunięcie treści względem położenia okna, wyznaczone z rozmiaru ramki i treści (oba
    /// w jednostkach, jak podaje je Avalonia). Ramka do zmiany rozmiaru w Windows jest równa po
    /// bokach i u dołu; u góry jest tylko tyle, ile zostaje z różnicy wysokości. Bez ramki
    /// (Linux, okna bez okien na pulpicie) treść zaczyna się w położeniu okna.
    /// </summary>
    internal static PixelPoint FrameInset(Size? frame, Size client, double scaling)
    {
        if (frame is not { } outer) return new PixelPoint(0, 0);

        var extraWidth = (int)Math.Round((outer.Width - client.Width) * scaling);
        var extraHeight = (int)Math.Round((outer.Height - client.Height) * scaling);
        var side = Math.Max(0, extraWidth / 2);

        return new PixelPoint(side, Math.Max(0, extraHeight - side));
    }

    /// <summary>
    /// Okno leży na którymś z obszarów roboczych. Kilka pikseli zapasu obejmuje niewidoczną ramkę
    /// systemową i okno przyciągnięte do krawędzi, które zaczyna się tuż poza obszarem.
    /// </summary>
    internal static bool FitsOn(PixelRect window, IEnumerable<PixelRect> areas, int tolerance) =>
        areas.Any(area => new PixelRect(area.X - tolerance, area.Y - tolerance,
                                        area.Width + 2 * tolerance, area.Height + 2 * tolerance).Contains(window));

    /// <summary>Położenie zapisane przy zamykaniu; drugie wywołanie Close nie nadpisuje go już wartością z zamkniętego okna.</summary>
    private bool _geometrySaved;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _geometrySaved) return;
        _geometrySaved = true;

        _saveGeometry(new WindowGeometry
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
        });
        App.Settings.Touch();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Klawisze, których nie zużyła kontrolka z fokusem, sterują odtwarzaniem jak w oknie głównym.
        PlaybackShortcuts?.Invoke(e);
    }

    // ================= Sterowanie oknem bez ramki systemowej =================

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || SystemFrame || _titleBar is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);

        if (WindowState == WindowState.Normal && TryGetResizeEdge(position) is { } edge)
        {
            BeginResizeDrag(edge, e);
            return;
        }

        if (position.Y <= _titleBar.Bounds.Height) BeginMoveDrag(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (SystemFrame) return;

        Cursor = TryGetResizeEdge(e.GetPosition(this)) switch
        {
            WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
            WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
            WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
            _ => Cursor.Default,
        };
    }

    private WindowEdge? TryGetResizeEdge(Point position)
    {
        var left = position.X <= ResizeMargin;
        var right = position.X >= Bounds.Width - ResizeMargin;
        var top = position.Y <= ResizeMargin;
        var bottom = position.Y >= Bounds.Height - ResizeMargin;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (_, true, true, _) => WindowEdge.NorthEast,
            (true, _, _, true) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.West,
            (_, true, _, _) => WindowEdge.East,
            (_, _, true, _) => WindowEdge.North,
            (_, _, _, true) => WindowEdge.South,
            _ => null,
        };
    }
}
