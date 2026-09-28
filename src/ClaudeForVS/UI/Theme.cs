using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeForVS.UI
{
    /// <summary>Applies Visual Studio theme resources to controls built in code.</summary>
    internal static class Theme
    {
        public static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas, Courier New");

        public static T Surface<T>(T element) where T : FrameworkElement
        {
            element.SetResourceReference(Control.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            element.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            if (element is Panel panel) panel.SetResourceReference(Panel.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            return element;
        }

        public static TextBox TextBox(bool multiline, bool readOnly = false, bool mono = true)
        {
            var box = new TextBox
            {
                AcceptsReturn = multiline,
                AcceptsTab = false,
                IsReadOnly = readOnly,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                Padding = new Thickness(4),
            };
            if (mono) box.FontFamily = Mono;
            box.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.TextBoxStyleKey);
            return box;
        }

        public static Button Button(string text, RoutedEventHandler onClick, bool isDefault = false)
        {
            var button = new Button { Content = text, MinWidth = 86, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3), IsDefault = isDefault };
            button.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.ButtonStyleKey);
            button.Click += onClick;
            return button;
        }

        public static TextBlock Label(string text, bool dim = false)
        {
            var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            label.SetResourceReference(TextBlock.ForegroundProperty,
                dim ? EnvironmentColors.ToolWindowTabTextBrushKey : EnvironmentColors.ToolWindowTextBrushKey);
            return label;
        }

        public static CheckBox CheckBox(string text)
        {
            var box = new CheckBox { Content = text, VerticalAlignment = VerticalAlignment.Center };
            box.SetResourceReference(FrameworkElement.StyleProperty, VsResourceKeys.CheckBoxStyleKey);
            return box;
        }
    }
}
