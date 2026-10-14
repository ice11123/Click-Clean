namespace ClickClean;

public partial class LanguageWindow : Window
{
    public string SelectedLanguage => Languages.SelectedIndex == 1 ? "en-US" : "zh-CN";
    public LanguageWindow(string language)
    {
        InitializeComponent();
        Languages.SelectedIndex = language == "en-US" ? 1 : 0;
        Loaded += (_, _) => Languages.Focus();
    }
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
