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
        AvatarButton.Visibility = chooseAvatar ? Visibility.Visible : Visibility.Collapsed;
        AvatarLabel.Visibility = chooseAvatar ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    void Avatar_Click(object sender, RoutedEventArgs e) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "Choose a picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp"
        };
        if (dialog.ShowDialog(this) != true) return;
        AvatarSource = dialog.FileName;
        AvatarLabel.Text = System.IO.Path.GetFileName(dialog.FileName);
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
