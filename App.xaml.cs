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
    
    public void SetTheme(bool isDark)
    {
        var dict = new ResourceDictionary();
        if (isDark)
        {
            dict.Add("WindowBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0f0f13")));
            dict.Add("PanelBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252530")));
            dict.Add("PanelBgDark", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#181820")));
            dict.Add("PanelBgColor", (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252530"));
            dict.Add("PanelBgDarkColor", (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#181820"));
            dict.Add("PanelBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3a3a45")));
            dict.Add("TextPrimary", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e0e0e0")));
            dict.Add("TextInverse", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black));
            dict.Add("TextDim", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa")));
            dict.Add("TextMuted", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#888888")));
            dict.Add("ImageBg", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Black));
            dict.Add("ControlBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1e1e24")));
            dict.Add("ControlBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333333")));
            dict.Add("ButtonBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333344")));
            dict.Add("ButtonAltBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#444455")));
            dict.Add("TextBoxBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a1a20")));
            dict.Add("DividerBrush", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2a2a35")));
            dict.Add("ButtonBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#555555")));
        }
        else
        {
            dict.Add("WindowBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f5f5f5")));
            dict.Add("PanelBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff")));
            dict.Add("PanelBgDark", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e0e0e0")));
            dict.Add("PanelBgColor", (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff"));
            dict.Add("PanelBgDarkColor", (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e0e0e0"));
            dict.Add("PanelBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#cccccc")));
            dict.Add("TextPrimary", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#202020")));
            dict.Add("TextInverse", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White));
            dict.Add("TextDim", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#555555")));
            dict.Add("TextMuted", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#777777")));
            dict.Add("ImageBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#eeeeee")));
            dict.Add("ControlBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff")));
            dict.Add("ControlBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#aaaaaa")));
            dict.Add("ButtonBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e0e0e0")));
            dict.Add("ButtonAltBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#d0d0d0")));
            dict.Add("TextBoxBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff")));
            dict.Add("DividerBrush", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#dddddd")));
            dict.Add("ButtonBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#cccccc")));
        }
        
        // Remove existing and add new
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
    }
}
