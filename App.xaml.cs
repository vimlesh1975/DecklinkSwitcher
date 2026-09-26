using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System;

namespace DecklinkSwitcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        this.DispatcherUnhandledException += (s, e) => {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "decklinkswitcher_log.txt"), "CRASH: " + e.Exception.ToString() + "\n");
        };
    }
}
