using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace UnVault.App.Views;

/// <summary>
/// A drop-down closed from the keyboard (Enter picks, Esc cancels) shows its focus ring again. Avalonia hands focus back
/// to it as if it had been clicked, which hides the ring: keys still go there, but the keyboard user can't see where.
/// </summary>
internal static class DropDownFocus
{
    /// <summary>The drop-down a key is being handled in, while it's open: if it closes meanwhile, a key closed it.</summary>
    private static ComboBox? s_keyIn;

    public static void Register()
    {
        // Tab closes it too, but moves on to the next control.
        InputElement.KeyDownEvent.AddClassHandler<ComboBox>((box, e) => s_keyIn = box.IsDropDownOpen && e.Key != Key.Tab ? box : null,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<ComboBox>((_, _) => s_keyIn = null, RoutingStrategies.Bubble, handledEventsToo: true);

        ComboBox.IsDropDownOpenProperty.Changed.AddClassHandler<ComboBox>((box, _) =>
        {
            if (box.IsDropDownOpen || s_keyIn != box)
                return;
            // After the drop-down's own focusing, which would hide the ring again.
            Dispatcher.UIThread.Post(() =>
            {
                if (box.IsFocused)
                    TopLevel.GetTopLevel(box)?.FocusManager?.Focus(null, NavigationMethod.Unspecified);
                box.Focus(NavigationMethod.Tab);
            }, DispatcherPriority.Background);
        });
    }
}
