using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Localisation;

namespace Cewka.App.Controls;

/// <summary>Jeden element rzędu okładek. Rząd nie wie, czy to utwór, czy stacja.</summary>
/// <param name="Id">Tożsamość elementu; po niej fokus trzyma się elementu przy zmianie listy.</param>
/// <param name="Duration">Czas do podpisu; null, gdy go nie ma.</param>
/// <param name="LoadImage">Odczyt obrazu, wołany poza wątkiem interfejsu; rząd przejmuje wynik i sam go zwalnia. Null: brak obrazu.</param>
public sealed record CoverRowItem(object Id, string Title, string Subtitle, string? Duration, Func<Bitmap?> LoadImage);

/// <summary>
/// Poziomy rząd kwadratowych okładek z fokusem na jednej z nich i jednowierszowym podpisem pod nią.
/// Istnieją tylko okładki widoczne i po <see cref="Overscan"/> z każdej strony, a obrazy czyta się
/// w tle, od najbliższych fokusowi, i zwalnia razem z okładką.
/// <para>
/// Pułapka: zwolniony obraz w <see cref="Image"/> rzuca wyjątek przy następnym rysowaniu i psuje
/// rysowanie do końca procesu. Dlatego każda okładka zwalnia swój obraz tylko przez
/// <see cref="CoverRowTile.Release"/>, które najpierw go odpina, a spóźniony wynik odczytu nie trafia do
/// żadnego <see cref="Image"/>.
/// </para>
/// </summary>
public sealed class CoverRow : Decorator
{
    /// <summary>Tyle okładek ponad widoczne istnieje z każdej strony.</summary>
    public const int Overscan = 4;

    /// <summary>Tyle obrazów czyta się naraz, żeby szybkie przewijanie nie zatykało puli wątków.</summary>
    private const int MaxLoads = 2;

    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<object, CoverRowTile> _tiles = [];
    private readonly List<CoverRowTile> _pending = [];
    private readonly Canvas _strip = new();
    private readonly Panel _viewport = new() { ClipToBounds = true };
    private readonly TextBlock _caption = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TranslateTransform _scroll = new();
    private readonly Transitions _scrollTransitions;

    private IReadOnlyList<CoverRowItem> _items = [];
    private double _offset;
    private int _loading;
    private bool _open;
    private Bitmap? _placeholder;

    public CoverRow()
    {
        _scrollTransitions =
        [
            new DoubleTransition { Property = TranslateTransform.XProperty, Duration = ScrollDuration, Easing = new CubicEaseOut() },
        ];

        _strip.RenderTransform = _scroll;
        _viewport.Children.Add(_strip);
        _caption.Classes.Add("cover-row-caption");
        _caption.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(_caption, AccessibilityView.Raw);

        var column = new StackPanel();
        column.Children.Add(_viewport);
        column.Children.Add(_caption);
        Child = column;
    }

    /// <summary>Bok okładki w jednostkach układu.</summary>
    public double TileSize { get; set; } = 172.8;

    /// <summary>Odstęp między okładkami.</summary>
    public double TileSpacing { get; set; } = 24;

    /// <summary>Margines rzędu przy jego początku i końcu.</summary>
    public double EdgePadding { get; set; }

    /// <summary>Miejsce pod okładką na kropkę-znacznik.</summary>
    public double FooterHeight { get; set; } = 16;

    /// <summary>Przewijanie jest płynne; bez tego skokowe.</summary>
    public bool IsAnimated { get; set; }

    /// <summary>Obraz elementu bez własnego; tworzony raz, gdy trzeba, na wątku interfejsu. Rząd sam go zwalnia.</summary>
    public Func<Bitmap?>? PlaceholderFactory { get; set; }

    /// <summary>Kliknięcie okładki; rząd ustawia najpierw fokus na tym elemencie.</summary>
    public event Action<CoverRowItem>? ItemInvoked;

    public IReadOnlyList<CoverRowItem> Items => _items;

    /// <summary>Element bieżący; −1, gdy żaden.</summary>
    public int CurrentIndex { get; private set; } = -1;

    public int FocusIndex { get; private set; }

    public CoverRowItem? FocusedItem => FocusIndex < _items.Count ? _items[FocusIndex] : null;

    /// <summary>Okładki, które teraz istnieją.</summary>
    internal IReadOnlyCollection<CoverRowTile> Tiles => _tiles.Values;

    internal TextBlock Caption => _caption;

    internal TranslateTransform ScrollTransform => _scroll;

    /// <summary>Docelowe przesunięcie rzędu; bieżące jest w trakcie przewijania.</summary>
    internal double ScrollOffset => _offset;

    /// <summary>Jakiś obraz czeka na odczyt albo właśnie się czyta.</summary>
    internal bool IsLoading => _loading > 0 || _pending.Count > 0;

    /// <summary>Fokus klawiatury jest na jednej z okładek.</summary>
    public bool HasFocusedTile =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused);

    private double Pitch => TileSize + TileSpacing;

    private double ViewportWidth => Bounds.Width > 0 ? Bounds.Width : double.IsNaN(Width) ? 0 : Width;

    /// <summary>Pokazuje rząd z fokusem na elemencie bieżącym, bez przewijania.</summary>
    public void Open(IReadOnlyList<CoverRowItem> items, int currentIndex)
    {
        _open = true;
        _items = items;
        CurrentIndex = currentIndex;
        FocusIndex = currentIndex >= 0 && currentIndex < items.Count ? currentIndex : 0;
        _viewport.Height = TileSize + FooterHeight;
        _strip.Height = TileSize + FooterHeight;
        IsVisible = true;

        _scroll.Transitions = null;
        _offset = TargetOffset();
        _scroll.X = -_offset;

        Update(takeFocus: true);
    }

    /// <summary>
    /// Nowa lista przy otwartym rzędzie. Fokus zostaje na tym samym elemencie, a gdy go już nie ma, na
    /// tym samym miejscu. Fokus klawiatury przechodzi na nową okładkę tylko wtedy, gdy był w rzędzie.
    /// </summary>
    public void SetItems(IReadOnlyList<CoverRowItem> items, int currentIndex)
    {
        if (!_open) return;

        var takeFocus = HasFocusedTile;
        var focused = FocusedItem?.Id;
        var index = -1;

        if (focused is not null)
        {
            for (var i = 0; i < items.Count && index < 0; i++)
                if (Equals(items[i].Id, focused)) index = i;
        }

        _items = items;
        CurrentIndex = currentIndex;
        FocusIndex = index >= 0 ? index : Math.Clamp(FocusIndex, 0, Math.Max(0, items.Count - 1));

        ScrollToFocus();
        Update(takeFocus);
    }

    public void MoveFocusBy(int delta) => MoveFocusTo(FocusIndex + delta);

    public void MoveFocusTo(int index)
    {
        if (!_open || _items.Count == 0) return;

        FocusIndex = Math.Clamp(index, 0, _items.Count - 1);
        ScrollToFocus();
        Update(takeFocus: true);
    }

    /// <summary>Nazwy dostępności po zmianie języka.</summary>
    public void RefreshTexts()
    {
        if (_open) Update(takeFocus: false);
    }

    /// <summary>Chowa rząd: wszystkie okładki znikają, a ich obrazy są odpinane i zwalniane.</summary>
    public void Close()
    {
        ReleaseAll();
        IsVisible = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Zamknięcie okna przy otwartym rzędzie: nikt już nie zamknie go inaczej.
        ReleaseAll();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_open) return;

        ScrollToFocus();
        Update(takeFocus: false);
    }

    private void ReleaseAll()
    {
        _open = false;
        _pending.Clear();

        foreach (var tile in _tiles.Values)
        {
            tile.Release();
            _strip.Children.Remove(tile);
        }

        _tiles.Clear();
        _items = [];
        CurrentIndex = -1;
        FocusIndex = 0;
        _caption.Text = null;

        // Wspólny obraz na końcu: żadna okładka go już nie pokazuje.
        var placeholder = _placeholder;
        _placeholder = null;
        placeholder?.Dispose();
    }

    private double TargetOffset()
    {
        var content = 2 * EdgePadding + _items.Count * Pitch - TileSpacing;
        var centred = EdgePadding + FocusIndex * Pitch + TileSize / 2 - ViewportWidth / 2;
        return Math.Clamp(centred, 0, Math.Max(0, content - ViewportWidth));
    }

    /// <summary>Daleki skok nie przelatuje płynnie przez okładki, których nie ma.</summary>
    private void ScrollToFocus()
    {
        var target = TargetOffset();
        if (target == _offset) return;

        _scroll.Transitions = IsAnimated && Math.Abs(target - _offset) <= ViewportWidth ? _scrollTransitions : null;
        _offset = target;
        _scroll.X = -target;
    }

    /// <summary>Tworzy brakujące okładki, ustawia ich stan i dopiero na końcu usuwa zbędne, żeby fokus miał dokąd przejść.</summary>
    private void Update(bool takeFocus)
    {
        var kept = new HashSet<CoverRowTile>();

        if (_items.Count > 0)
        {
            var first = Math.Max(0, (int)Math.Floor((_offset - EdgePadding) / Pitch) - Overscan);
            var last = Math.Min(_items.Count - 1, (int)Math.Ceiling((_offset + ViewportWidth - EdgePadding) / Pitch) + Overscan);

            for (var index = first; index <= last; index++)
                kept.Add(Place(index));

            // Fokus bywa poza zakresem tylko przed pierwszym układem.
            if (FocusIndex < first || FocusIndex > last) kept.Add(Place(FocusIndex));

            if (takeFocus && _tiles.TryGetValue(_items[FocusIndex].Id, out var focus)
                && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() != focus)
                focus.Focus(NavigationMethod.Directional);
        }

        foreach (var tile in _tiles.Values.Where(tile => !kept.Contains(tile)).ToList())
        {
            _tiles.Remove(tile.Item.Id);
            _pending.Remove(tile);
            tile.Release();
            _strip.Children.Remove(tile);
        }

        UpdateCaption();
        PumpLoads();
    }

    private CoverRowTile Place(int index)
    {
        var item = _items[index];

        if (!_tiles.TryGetValue(item.Id, out var tile))
        {
            tile = new CoverRowTile(TileSize, TileSize + FooterHeight);
            tile.Click += (sender, _) => OnTileClick((CoverRowTile)sender!);
            _tiles[item.Id] = tile;
            _strip.Children.Add(tile);
            _pending.Add(tile);
        }

        var isCurrent = index == CurrentIndex;
        tile.Item = item;
        tile.Index = index;
        tile.SetState(isCurrent, CurrentIndex >= 0 && index < CurrentIndex, index == FocusIndex);
        Canvas.SetLeft(tile, EdgePadding + index * Pitch);

        var name = Describe(item, ", ");
        AutomationProperties.SetName(tile, isCurrent ? string.Format(Strings.Current["QueueNowPlaying"], name) : name);
        return tile;
    }

    private void OnTileClick(CoverRowTile tile)
    {
        if (!tile.IsRealized) return;

        FocusIndex = tile.Index;
        ScrollToFocus();
        Update(takeFocus: false);
        ItemInvoked?.Invoke(tile.Item);
    }

    /// <summary>Podpis stoi przy okładce z fokusem i rośnie w stronę środka rzędu, więc mieści się na obu krańcach.</summary>
    private void UpdateCaption()
    {
        var item = FocusedItem;
        _caption.Text = item is null ? null : Describe(item, " · ");

        var left = EdgePadding + FocusIndex * Pitch - _offset;
        var width = ViewportWidth;

        if (left + TileSize / 2 <= width / 2)
        {
            _caption.HorizontalAlignment = HorizontalAlignment.Left;
            _caption.Margin = new Thickness(left, 0, 0, 0);
        }
        else
        {
            _caption.HorizontalAlignment = HorizontalAlignment.Right;
            _caption.Margin = new Thickness(0, 0, Math.Max(0, width - left - TileSize), 0);
        }
    }

    private static string Describe(CoverRowItem item, string separator) =>
        string.Join(separator, new[] { item.Title, item.Subtitle, item.Duration }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private void PumpLoads()
    {
        while (_loading < MaxLoads && _pending.Count > 0)
        {
            var tile = _pending.MinBy(pending => Math.Abs(pending.Index - FocusIndex))!;
            _pending.Remove(tile);
            _loading++;

            var load = tile.Item.LoadImage;
            _ = Task.Run(() =>
            {
                Bitmap? bitmap = null;
                try
                {
                    bitmap = load();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[cewka] obraz rzędu okładek: {ex.Message}");
                }

                Dispatcher.UIThread.Post(() => FinishLoad(tile, bitmap));
            });
        }
    }

    /// <summary>Wynik dla okładki, której już nie ma, nie dotyka żadnego <see cref="Image"/> i od razu jest zwalniany.</summary>
    private void FinishLoad(CoverRowTile tile, Bitmap? bitmap)
    {
        _loading--;

        if (tile.IsRealized)
        {
            if (bitmap is not null)
            {
                tile.ShowOwned(bitmap);
            }
            else
            {
                _placeholder ??= PlaceholderFactory?.Invoke();
                tile.ShowShared(_placeholder);
            }
        }
        else
        {
            bitmap?.Dispose();
        }

        PumpLoads();
    }
}

/// <summary>Jedna okładka rzędu: kwadrat z obrazem i pierścieniem fokusu, pod nim kropka bieżącego elementu.</summary>
internal sealed class CoverRowTile : Button
{
    private readonly Image _image;
    private readonly Ellipse _dot;
    private Bitmap? _owned;

    public CoverRowTile(double size, double height)
    {
        Width = size;
        Height = height;
        Classes.Add("cover-tile");

        // Pierścień jest częścią okładki: ozdobnik z warstwy okna goniłby przewijany rząd po klatkach.
        FocusAdorner = null;

        _image = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        var art = new Border { ClipToBounds = true, Child = _image };
        art.Classes.Add("cover-tile-art");

        var ring = new Border { IsHitTestVisible = false };
        ring.Classes.Add("cover-tile-ring");

        var square = new Panel { Width = size, Height = size };
        square.Children.Add(art);
        square.Children.Add(ring);

        _dot = new Ellipse { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
        _dot.Classes.Add("cover-tile-dot");

        var column = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        column.Children.Add(square);
        column.Children.Add(_dot);
        Content = column;
    }

    protected override Type StyleKeyOverride => typeof(Button);

    public CoverRowItem Item { get; set; } = null!;

    public int Index { get; set; }

    /// <summary>Okładka jest w rzędzie; po <see cref="Release"/> już nigdy nie pokaże obrazu.</summary>
    public bool IsRealized { get; private set; } = true;

    internal Image Picture => _image;

    internal Ellipse Dot => _dot;

    /// <summary>Obraz prywatny tej okładki, do zwolnienia razem z nią.</summary>
    public void ShowOwned(Bitmap bitmap)
    {
        _owned = bitmap;
        _image.Source = bitmap;
    }

    /// <summary>Obraz wspólny, którym zarządza rząd.</summary>
    public void ShowShared(IImage? image) => _image.Source = image;

    public void SetState(bool current, bool played, bool focused)
    {
        Classes.Set("current", current);
        Classes.Set("played", played);
        Classes.Set("focused", focused);
        _dot.IsVisible = current;
    }

    /// <summary>Odpina obraz od <see cref="Image"/> i dopiero potem go zwalnia.</summary>
    public void Release()
    {
        IsRealized = false;

        var owned = _owned;
        _owned = null;
        _image.Source = null;
        owned?.Dispose();
    }
}
