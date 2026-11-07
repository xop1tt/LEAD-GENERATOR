using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Customers.Ui;

/// <summary>Узкая полоса для изменения ширины панели мышью: курсор «↔», перетаскивание, двойной щелчок возвращает ширину по умолчанию.</summary>
internal sealed partial class ResizeThumb : Grid
{
    private bool _dragging;
    private double _startX;

    public ResizeThumb()
    {
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += OnLost;
        DoubleTapped += (_, _) => ResetRequested?.Invoke();
    }

    /// <summary>Смещение указателя (в логических пикселях) от начала перетаскивания.</summary>
    public event Action<double>? Dragged;

    public event Action? DragStarted;

    public event Action? DragCompleted;

    public event Action? ResetRequested;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = CapturePointer(e.Pointer);
        _startX = e.GetCurrentPoint(null).Position.X;
        if (_dragging)
        {
            DragStarted?.Invoke();
        }

        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Dragged?.Invoke(e.GetCurrentPoint(null).Position.X - _startX);
            e.Handled = true;
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            DragCompleted?.Invoke();
            e.Handled = true;
        }
    }

    private void OnLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            DragCompleted?.Invoke();
        }
    }
}
