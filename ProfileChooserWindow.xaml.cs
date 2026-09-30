using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ResumeBuilder;

public partial class ProfileChooserWindow : Window {
    public ProfileChooserWindow() {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    void Refresh() {
        Cards.Children.Clear();
        foreach (var card in ProfileCatalog.Cards())
            Cards.Children.Add(ProfileCard(card));
        Cards.Children.Add(AddCard());
    }

    UIElement ProfileCard(ProfileCard card) {
        var name = new TextBlock {
            Text = card.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = card.DisplayName,
            Margin = new Thickness(12, 14, 12, 0)
        };
        var used = new TextBlock {
            Text = card.LastUsedText,
            Foreground = ThemeBrush("Text.Muted"),
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(12, 4, 12, 0)
        };
        var more = new System.Windows.Controls.Button {
            Content = "···",
            Style = (Style)FindResource("IconButtonStyle"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 8, 0),
            ToolTip = "Profile menu"
        };
        more.Click += (_, _) => ShowMenu(more, card);

        var body = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        DockPanel.SetDock(more, Dock.Top);
        var dock = new DockPanel();
        dock.Children.Add(more);
        dock.Children.Add(body);
        body.Children.Add(AvatarButton(card));
        body.Children.Add(name);
        body.Children.Add(used);

        var border = CardBorder(false);
        border.Child = dock;
        border.ToolTip = "Open " + card.DisplayName;
        WireCard(border, add: false, () => Open(card.ProfileId));
        return border;
    }

    UIElement AddCard() {
        var plus = new Border {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(32),
            Background = ThemeBrush("Accent.Muted"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Child = new TextBlock {
                Text = "+",
                FontSize = 32,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("Accent.Default"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
        var label = new TextBlock {
            Text = "Add profile",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var hint = new TextBlock {
            Text = "Another person",
            FontSize = 12,
            Foreground = ThemeBrush("Text.Muted"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var stack = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        stack.Children.Add(plus);
        stack.Children.Add(label);
        stack.Children.Add(hint);
        var border = CardBorder(true);
        border.Child = stack;
        border.ToolTip = "Add profile";
        WireCard(border, add: true, AddProfile);
        return border;
    }

    static Border CardBorder(bool add) => new() {
        Width = 200,
        Height = 236,
        Margin = new Thickness(0, 0, 18, 18),
        CornerRadius = new CornerRadius(16),
        Background = ThemeBrush(add ? "Bg.SurfaceRaised" : "Bg.Surface"),
        BorderBrush = ThemeBrush(add ? "Accent.Default" : "Border.Subtle"),
        BorderThickness = new Thickness(1),
        Focusable = true,
        Cursor = System.Windows.Input.Cursors.Hand
    };

    static void WireCard(Border border, bool add, Action open) {
        void Paint(bool hot, bool focused) {
            border.Background = hot ? ThemeBrush("Bg.Hover") : ThemeBrush(add ? "Bg.SurfaceRaised" : "Bg.Surface");
            border.BorderBrush = hot || focused ? ThemeBrush("Accent.Default") : ThemeBrush(add ? "Accent.Default" : "Border.Subtle");
            border.BorderThickness = new Thickness(focused ? 2 : 1);
        }
        border.MouseEnter += (_, _) => Paint(true, border.IsKeyboardFocused);
        border.MouseLeave += (_, _) => Paint(false, border.IsKeyboardFocused);
        border.GotKeyboardFocus += (_, _) => Paint(border.IsMouseOver, true);
        border.LostKeyboardFocus += (_, _) => Paint(border.IsMouseOver, false);
        border.MouseLeftButtonUp += (_, e) => {
            if (e.OriginalSource is DependencyObject source && InsideButton(source)) return;
            open();
        };
        border.KeyDown += (_, e) => {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            open();
            e.Handled = true;
        };
    }

    UIElement AvatarButton(ProfileCard card) {
        var avatar = Avatar(card.DisplayName, card.AvatarFile, 84);
        if (avatar is FrameworkElement element) {
            element.Cursor = System.Windows.Input.Cursors.Hand;
            element.ToolTip = "Change avatar";
            element.MouseLeftButtonUp += (_, e) => {
                e.Handled = true;
                ChangeAvatar(card);
            };
        }
        return avatar;
    }

    internal static UIElement Avatar(string displayName, string? file, double size) {
        if (file is not null) {
            try {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(file);
                image.EndInit();
                return new Border {
                    Width = size, Height = size,
                    CornerRadius = new CornerRadius(size / 2),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            ClipToBounds = true,
                    Child = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.UniformToFill, Width = size, Height = size }
                };
            } catch { /* a bad image falls back to the initial */ }
        }
        var letter = string.IsNullOrWhiteSpace(displayName) ? "?" : displayName.Trim()[..1].ToUpperInvariant();
        return new Border {
            Width = size, Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = ThemeBrush("Accent.Muted"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Child = new TextBlock {
                Text = letter,
                FontSize = size * 0.38,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrush("Accent.Default"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };
    }

    static bool InsideButton(DependencyObject source) {
        for (DependencyObject? item = source; item is not null; item = VisualTreeHelper.GetParent(item))
            if (item is System.Windows.Controls.Button) return true;
        return false;
    }

    static System.Windows.Media.Brush ThemeBrush(string key) =>
        System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Gray;

    void ShowMenu(FrameworkElement anchor, ProfileCard card) {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Change Avatar", () => ChangeAvatar(card)));
        if (card.AvatarFile is not null)
            menu.Items.Add(Item("Use Initial", () => UseInitial(card)));
        menu.Items.Add(Item("Rename", () => Rename(card)));
        menu.Items.Add(Item("Open Profile Folder", () => OpenFolder(card.ProfileId)));
        menu.Items.Add(new Separator { Margin = new Thickness(8, 4, 8, 4) });
        menu.Items.Add(Item("Delete", () => Delete(card), danger: true));
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    static MenuItem Item(string header, Action action, bool danger = false) {
        var item = new MenuItem { Header = header, Tag = danger ? "danger" : null };
        item.Click += (_, _) => action();
        return item;
    }

    void Open(string profileId) {
        if (!ProfileLaunch.Start(profileId)) {
            System.Windows.MessageBox.Show("Resume Builder could not open that profile.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
    }

    void AddProfile() {
        var editor = new ProfileEditorWindow("Add profile", "", chooseAvatar: true) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try {
            var created = ProfileRegistry.Create(editor.EnteredName, editor.AvatarSource);
            Refresh();
            Open(created.ProfileId);
        } catch (Exception ex) {
            PerfLog.Line("PROFILE create failed " + ex.GetType().Name);
            System.Windows.MessageBox.Show("The profile was not created. Try again.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ChangeAvatar(ProfileCard card) {
        var dialog = new Microsoft.Win32.OpenFileDialog {
            Title = "Choose a picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp"
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!ProfileRegistry.TrySetAvatar(card.ProfileId, dialog.FileName, out var error)) {
            PerfLog.Line("PROFILE avatar chooser rejected id=" + card.ProfileId);
            System.Windows.MessageBox.Show(string.IsNullOrWhiteSpace(error) ? "Unable to use this image." : error,
                "Resume Builder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Refresh();
    }

    void UseInitial(ProfileCard card) {
        ProfileRegistry.ClearAvatar(card.ProfileId);
        Refresh();
    }

    void Rename(ProfileCard card) {
        var editor = new ProfileEditorWindow("Rename profile", card.DisplayName, chooseAvatar: false) { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (!ProfileRegistry.Rename(card.ProfileId, editor.EnteredName)) {
            System.Windows.MessageBox.Show("That name could not be saved.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Refresh();
    }

    static void OpenFolder(string profileId) {
        var root = ProfilePaths.ProfileRoot(profileId);
        if (!System.IO.Directory.Exists(root)) {
            System.Windows.MessageBox.Show("That profile folder is not there.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
    }

    void Delete(ProfileCard card) {
        var answer = System.Windows.MessageBox.Show(
            "Delete " + card.DisplayName + "?\n\nThis removes that profile's jobs, applications, and settings. Generated resumes in Documents are not deleted.",
            "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        if (!ProfileRegistry.Delete(card.ProfileId, out var error)) {
            System.Windows.MessageBox.Show(error ?? "That profile was not deleted.", "Resume Builder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Refresh();
    }
}
