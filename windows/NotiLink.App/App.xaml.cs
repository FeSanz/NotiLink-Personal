using System.Configuration;
using System.Data;
using System.Windows;

namespace NotiLink.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
// Fully qualified because UseWindowsForms (needed for the tray icon in MainWindow) implicitly
// brings in System.Windows.Forms.Application too, which collides with WPF's own Application.
public partial class App : System.Windows.Application
{
}

