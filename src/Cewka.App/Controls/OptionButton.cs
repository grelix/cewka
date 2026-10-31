using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Cewka.App.Controls;

/// <summary>
/// Przycisk jednej z wzajemnie wykluczających się opcji (pasek segmentów, zakładki ustawień,
/// listy języków, barw i urządzeń). Wygląda jak zwykły przycisk opcji, a czytnikowi ekranu
/// podaje, czy opcja jest wybrana.
/// </summary>
public sealed class OptionButton : Button
{
    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<OptionButton, bool>(nameof(IsSelected));

    protected override Type StyleKeyOverride => typeof(Button);

    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsSelectedProperty && ControlAutomationPeer.FromElement(this) is { } peer)
        {
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty,
                change.GetOldValue<bool>(), change.GetNewValue<bool>());
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new OptionButtonAutomationPeer(this);

    private void Choose() => OnClick();

    private sealed class OptionButtonAutomationPeer(OptionButton owner)
        : ContentControlAutomationPeer(owner), ISelectionItemProvider, IInvokeProvider
    {
        private OptionButton Option => (OptionButton)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.RadioButton;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public bool IsSelected => Option.IsSelected;

        public ISelectionProvider? SelectionContainer => null;

        public void AddToSelection() => Select();

        public void RemoveFromSelection()
        {
        }

        public void Select()
        {
            if (!Option.IsSelected) Option.Choose();
        }

        public void Invoke() => Option.Choose();
    }
}
