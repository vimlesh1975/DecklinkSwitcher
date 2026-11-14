using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using System;

namespace DecklinkSwitcher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
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
            dict.Add("SourceButtonBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333344")));
            dict.Add("SourceButtonText", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff")));
            dict.Add("TextBoxBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1a1a20")));
            dict.Add("DividerBrush", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2a2a35")));
            dict.Add("ButtonBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#555555")));
            dict.Add("OutputCardYouTubeBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#341f25")));
            dict.Add("OutputCardRecordBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1e302a")));
            dict.Add("OutputCardNdiBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1d2b3e")));
            dict.Add("OutputCardSrtBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2b233a")));
            dict.Add("PgmSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#162231")));
            dict.Add("SettingsSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#262833")));
            dict.Add("OutputSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#15161c")));
            dict.Add("PgmSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2e6da4")));
            dict.Add("SettingsSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#6c757d")));
            dict.Add("OutputSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7c5cdb")));
            dict.Add("SourcesSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#151c1a")));
            dict.Add("SourcesSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2e8b6d")));
            dict.Add("OverlayCardNdiBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#232436")));
            dict.Add("OverlayCardHtmlBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1e2d33")));
            dict.Add("OverlayCardPgmBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1f2633")));
            dict.Add("OverlayCardLogoBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2a2622")));
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
            dict.Add("SourceButtonBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e6e9f2")));
            dict.Add("SourceButtonText", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#151520")));
            dict.Add("TextBoxBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#ffffff")));
            dict.Add("DividerBrush", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#dddddd")));
            dict.Add("ButtonBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#cccccc")));
            dict.Add("OutputCardYouTubeBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#fbe9ea")));
            dict.Add("OutputCardRecordBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e6f4ee")));
            dict.Add("OutputCardNdiBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e5effc")));
            dict.Add("OutputCardSrtBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#efeafc")));
            dict.Add("PgmSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#dce8f5")));
            dict.Add("SettingsSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f4f4f6")));
            dict.Add("OutputSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e2e4ea")));
            dict.Add("PgmSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4a89dc")));
            dict.Add("SettingsSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#8c96a0")));
            dict.Add("OutputSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#7c5cdb")));
            dict.Add("SourcesSectionBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#e9f2ee")));
            dict.Add("SourcesSectionBorder", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2e8b6d")));
            dict.Add("OverlayCardNdiBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#eef0fa")));
            dict.Add("OverlayCardHtmlBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#eaf6f8")));
            dict.Add("OverlayCardPgmBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#f2f6fb")));
            dict.Add("OverlayCardLogoBg", new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#faf4ea")));
        }
        
        // Remove existing and add new
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(dict);
    }
}
