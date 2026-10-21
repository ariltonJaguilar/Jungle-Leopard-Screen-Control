using System.Windows;

namespace JungleLeopardPoc;

internal enum DialogResult { None, OK, Yes, No, Cancel }
internal enum MessageBoxButtons { OK, YesNo }
internal enum MessageBoxIcon { None, Information, Question, Warning, Error }
internal enum MessageBoxDefaultButton { Button1, Button2, Button3 }

internal static class BackendMessageBox
{
    internal static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        => Show(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

    internal static DialogResult Show(Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        MessageBoxButton wpfButtons = buttons == MessageBoxButtons.YesNo ? MessageBoxButton.YesNo : MessageBoxButton.OK;
        MessageBoxImage wpfIcon = icon switch
        {
            MessageBoxIcon.Information => MessageBoxImage.Information,
            MessageBoxIcon.Question => MessageBoxImage.Question,
            MessageBoxIcon.Warning => MessageBoxImage.Warning,
            MessageBoxIcon.Error => MessageBoxImage.Error,
            _ => MessageBoxImage.None
        };
        MessageBoxResult wpfDefault = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => MessageBoxResult.No,
            MessageBoxDefaultButton.Button3 => MessageBoxResult.Cancel,
            _ => buttons == MessageBoxButtons.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK
        };
        MessageBoxResult result = owner is null
            ? System.Windows.MessageBox.Show(text, caption, wpfButtons, wpfIcon, wpfDefault)
            : System.Windows.MessageBox.Show(owner, text, caption, wpfButtons, wpfIcon, wpfDefault);
        return result switch
        {
            MessageBoxResult.OK => DialogResult.OK,
            MessageBoxResult.Yes => DialogResult.Yes,
            MessageBoxResult.No => DialogResult.No,
            MessageBoxResult.Cancel => DialogResult.Cancel,
            _ => DialogResult.None
        };
    }
}
