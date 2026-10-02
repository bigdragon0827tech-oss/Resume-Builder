using System.Windows;

namespace ResumeBuilder;

public partial class ProfileEditorWindow : Window {
    public string EnteredName { get; private set; } = "";
    public string? AvatarSource { get; private set; }

    public ProfileEditorWindow(string heading, string name, bool chooseAvatar) {
        InitializeComponent();
        Title = heading;
        Heading.Text = heading;
        NameBox.Text = name;
        ConfirmButton.Content = chooseAvatar ? "Create" : "Save";
        AvatarButton.Visibility = chooseAvatar ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => {
            FitToContent();
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    void FitToContent() {
        if (Content is not FrameworkElement root) return;
        SizeToContent = SizeToContent.Manual;
        root.Measure(new System.Windows.Size(420, double.PositiveInfinity));
        var chrome = SystemParameters.WindowNonClientFrameThickness;
        Height = Math.Ceiling(root.DesiredSize.Height + chrome.Top + chrome.Bottom);
    }

    void Avatar_Click(object sender, RoutedEventArgs e) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "Choose a picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp"
        };
        if (dialog.ShowDialog(this) != true) return;
        AvatarSource = dialog.FileName;
        AvatarLabel.Text = System.IO.Path.GetFileName(dialog.FileName);
        AvatarLabel.Visibility = Visibility.Visible;
        ShowPreview(dialog.FileName);
        FitToContent();
    }

    void ShowPreview(string path) {
        try {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            AvatarPreview.Source = image;
            AvatarPreviewHost.Visibility = Visibility.Visible;
        } catch (Exception ex) {
            AvatarPreviewHost.Visibility = Visibility.Collapsed;
            PerfLog.Line("PROFILE avatar preview skipped " + ex.GetType().Name);
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Save_Click(object sender, RoutedEventArgs e) {
        var name = ProfileIds.NormalizeName(NameBox.Text);
        if (name is null) {
            System.Windows.MessageBox.Show("Enter a name up to 40 characters, without file symbols.",
                "Resume Builder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        EnteredName = name;
        DialogResult = true;
    }
}
