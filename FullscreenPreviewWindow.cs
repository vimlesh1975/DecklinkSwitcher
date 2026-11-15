using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfImage = System.Windows.Controls.Image;

namespace DecklinkSwitcher
{
    public sealed class FullscreenPreviewWindow : Window
    {
        public WpfImage FsPreviewOutput { get; }

        public FullscreenPreviewWindow()
        {
            Title = "DeckLink Switcher - Fullscreen Program Preview";
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Background = System.Windows.Media.Brushes.Black;
            Cursor = Cursors.Arrow;

            var viewbox = new Viewbox { Stretch = System.Windows.Media.Stretch.Uniform };
            FsPreviewOutput = new WpfImage { Stretch = System.Windows.Media.Stretch.Uniform, Width = 1920, Height = 1080 };

            viewbox.Child = FsPreviewOutput;
            Content = viewbox;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape || e.Key == Key.F11)
                {
                    e.Handled = true;
                    Close();
                }
            };

            MouseDoubleClick += (s, e) =>
            {
                e.Handled = true;
                Close();
            };
        }
    }
}
