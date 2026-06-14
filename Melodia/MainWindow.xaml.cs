using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Melodia;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        RootFrame.Navigate(typeof(MainPage));
        AppWindow.Resize(new SizeInt32(1100, 900));
    }
}
