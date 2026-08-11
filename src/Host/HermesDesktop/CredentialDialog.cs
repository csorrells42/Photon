using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Security;

namespace HermesDesktop;

internal sealed class CredentialDialog : Window
{
    private static readonly Brush WindowBrush = BrushFromHex("#090B12");
    private static readonly Brush PanelBrush = BrushFromHex("#111521");
    private static readonly Brush FieldBorderBrush = BrushFromHex("#2B3140");
    private static readonly Brush PrimaryTextBrush = BrushFromHex("#F3F4F8");
    private static readonly Brush MutedTextBrush = BrushFromHex("#979DB0");
    private static readonly Brush AccentBrush = BrushFromHex("#7257E8");

    private readonly PasswordBox _secretBox;
    private readonly Button _saveButton;

    internal CredentialDialog(
        string provider,
        string credentialId,
        IReadOnlyList<string>? purposes = null,
        string storageLabel = "WINDOWS CREDENTIAL MANAGER")
    {
        Title = $"Connect {provider}";
        Width = 520;
        Height = 360;
        MinWidth = 460;
        MinHeight = 330;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = WindowBrush;
        Foreground = PrimaryTextBrush;
        FontFamily = new FontFamily("Segoe UI");

        var content = new Grid { Margin = new Thickness(28) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var eyebrow = new TextBlock
        {
            Text = storageLabel,
            Foreground = AccentBrush,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetRow(eyebrow, 0);
        content.Children.Add(eyebrow);

        var title = new TextBlock
        {
            Text = $"Connect {provider}",
            Foreground = PrimaryTextBrush,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 4),
        };
        Grid.SetRow(title, 1);
        content.Children.Add(title);

        var description = new TextBlock
        {
            Text = $"Credential profile: {credentialId}. Authorized use: {FormatPurposes(purposes)}. The secret goes directly into your Windows vault and is never returned to the Workbench renderer.",
            Foreground = MutedTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 19,
            Margin = new Thickness(0, 0, 0, 20),
        };
        Grid.SetRow(description, 2);
        content.Children.Add(description);

        var field = new StackPanel();
        field.Children.Add(new TextBlock
        {
            Text = "API key or provider secret",
            Foreground = PrimaryTextBrush,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7),
        });
        _secretBox = new PasswordBox
        {
            Height = 42,
            Padding = new Thickness(12, 8, 12, 8),
            Background = PanelBrush,
            Foreground = PrimaryTextBrush,
            BorderBrush = FieldBorderBrush,
            BorderThickness = new Thickness(1),
            CaretBrush = PrimaryTextBrush,
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = 13,
        };
        field.Children.Add(_secretBox);
        Grid.SetRow(field, 3);
        content.Children.Add(field);

        var note = new TextBlock
        {
            Text = "Hermes Workbench stores only an opaque credential reference in its UI. Provider collectors will read the secret inside the trusted desktop host.",
            Foreground = MutedTextBrush,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(note, 4);
        content.Children.Add(note);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancelButton = CreateButton("Cancel", PanelBrush, FieldBorderBrush);
        cancelButton.IsCancel = true;
        cancelButton.Click += (_, _) =>
        {
            _secretBox.Clear();
            DialogResult = false;
        };
        actions.Children.Add(cancelButton);

        _saveButton = CreateButton("Save securely", AccentBrush, AccentBrush);
        _saveButton.IsDefault = true;
        _saveButton.IsEnabled = false;
        _saveButton.Margin = new Thickness(10, 0, 0, 0);
        _saveButton.Click += (_, _) => DialogResult = true;
        actions.Children.Add(_saveButton);
        Grid.SetRow(actions, 5);
        content.Children.Add(actions);

        _secretBox.PasswordChanged += (_, _) => _saveButton.IsEnabled = _secretBox.Password.Length > 0;
        Content = content;
        Loaded += (_, _) => _secretBox.Focus();
        Closed += (_, _) =>
        {
            if (DialogResult != true) _secretBox.Clear();
        };
    }

    internal SecureString TakeSecret()
    {
        var secret = _secretBox.SecurePassword.Copy();
        _secretBox.Clear();
        secret.MakeReadOnly();
        return secret;
    }

    internal void ClearSecret() => _secretBox.Clear();

    private static string FormatPurposes(IReadOnlyList<string>? purposes) => purposes is { Count: > 0 }
        ? string.Join(", ", purposes)
        : "the selected provider connection";

    private static Button CreateButton(string label, Brush background, Brush border) => new()
    {
        Content = label,
        MinWidth = 108,
        Height = 38,
        Padding = new Thickness(15, 7, 15, 7),
        Background = background,
        Foreground = PrimaryTextBrush,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Cursor = System.Windows.Input.Cursors.Hand,
        OverridesDefaultStyle = true,
        Template = CreateButtonTemplate(),
    };

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "chrome");
        border.SetBinding(Border.BackgroundProperty, new Binding(nameof(Control.Background)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Control.BorderBrush)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding(nameof(Control.BorderThickness)) { RelativeSource = RelativeSource.TemplatedParent });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.ContentProperty, new Binding(nameof(ContentControl.Content)) { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding(nameof(ContentControl.ContentTemplate)) { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(ContentPresenter.MarginProperty, new Binding(nameof(Control.Padding)) { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45, "chrome"));
        template.Triggers.Add(disabled);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "chrome"));
        template.Triggers.Add(pressed);
        return template;
    }

    private static SolidColorBrush BrushFromHex(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
