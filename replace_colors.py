import re

with open('MainWindow.xaml', 'r', encoding='utf-8') as f:
    text = f.read()

replacements = {
    '"#0f0f13"': '"{DynamicResource WindowBg}"',
    '"#252530"': '"{DynamicResource PanelBg}"',
    '"#181820"': '"{DynamicResource PanelBgDark}"',
    '"#3a3a45"': '"{DynamicResource PanelBorder}"',
    '"#e0e0e0"': '"{DynamicResource TextPrimary}"',
    '"#aaa"': '"{DynamicResource TextDim}"',
    '"#888"': '"{DynamicResource TextMuted}"',
    '"#1e1e24"': '"{DynamicResource ControlBg}"',
    '"#333"': '"{DynamicResource ControlBorder}"',
    '"#222"': '"{DynamicResource ControlBorder}"',
    '"#333344"': '"{DynamicResource ButtonBg}"',
    '"#444455"': '"{DynamicResource ButtonAltBg}"',
    '"#1a1a20"': '"{DynamicResource TextBoxBg}"',
    '"#2a2a35"': '"{DynamicResource DividerBrush}"',
    '"#555"': '"{DynamicResource ButtonBorder}"'
}

for k, v in replacements.items():
    text = text.replace(k, v)

text = text.replace('Background="Black"', 'Background="{DynamicResource ImageBg}"')
text = text.replace('Foreground="Black"', 'Foreground="{DynamicResource TextInverse}"')
text = text.replace('Foreground="White"', 'Foreground="{DynamicResource TextPrimary}"')

header = """
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        
        <StackPanel Grid.Row="0" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,10,25,0">
            <TextBlock Text="CPU:" Foreground="{DynamicResource TextPrimary}" FontSize="12" FontWeight="Bold" Margin="0,0,5,0" VerticalAlignment="Center"/>
            <TextBlock Name="TxtCpuUsage" Text="0%" Foreground="#00e676" FontSize="12" FontWeight="Bold" Margin="0,0,15,0" VerticalAlignment="Center" Width="40"/>
            
            <TextBlock Text="Dark Mode" Foreground="{DynamicResource TextPrimary}" FontSize="12" FontWeight="Bold" Margin="0,0,5,0" VerticalAlignment="Center"/>
            <CheckBox Name="ChkDarkMode" IsChecked="True" VerticalAlignment="Center" Checked="ChkDarkMode_Checked" Unchecked="ChkDarkMode_Unchecked" Foreground="{DynamicResource TextPrimary}"/>
        </StackPanel>

        <Grid Grid.Row="1" Margin="25">
"""

text = text.replace('<Grid Margin="25">', header)
text = text.replace('</Window>', '    </Grid>\n</Window>')

with open('MainWindow.xaml', 'w', encoding='utf-8') as f:
    f.write(text)
