using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal static class FluentUi
{
    private static bool _initialized;
    private static ApplicationTheme _currentTheme = ApplicationTheme.Dark;
    private static string _themeMode = "system";

    public static readonly Color BrandViolet = Color.FromRgb(0x7C, 0x3A, 0xED);
    public static readonly Color BrandVioletBright = Color.FromRgb(0x8B, 0x5C, 0xF6);
    public static readonly Color BrandVioletDark = Color.FromRgb(0x5B, 0x21, 0xB6);
    public static readonly Color BrandVioletSoft = Color.FromRgb(0xA7, 0x8B, 0xFA);
    public static readonly Color EmeraldGreen = Color.FromRgb(0x10, 0xB9, 0x81);
    public static readonly Color AmberGold = Color.FromRgb(0xF5, 0x9E, 0x0B);
    public static readonly Color CrimsonRed = Color.FromRgb(0xEF, 0x44, 0x44);
    public static readonly Color SlateMuted = Color.FromRgb(0x94, 0xA3, 0xB8);

    private static readonly SolidColorBrush _darkBrandSoftBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)));
    private static readonly SolidColorBrush _lightBrandSoftBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6D, 0x28, 0xD9)));
    private static readonly SolidColorBrush _darkMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
    private static readonly SolidColorBrush _lightMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)));

    public static readonly SolidColorBrush BrandBrush = Freeze(new SolidColorBrush(BrandVioletBright));
    public static SolidColorBrush BrandSoftBrush => _currentTheme == ApplicationTheme.Dark ? _darkBrandSoftBrush : _lightBrandSoftBrush;
    public static readonly SolidColorBrush GreenBrush = Freeze(new SolidColorBrush(EmeraldGreen));
    public static readonly SolidColorBrush AmberBrush = Freeze(new SolidColorBrush(AmberGold));
    public static readonly SolidColorBrush RedBrush = Freeze(new SolidColorBrush(CrimsonRed));
    public static SolidColorBrush MutedBrush => _currentTheme == ApplicationTheme.Dark ? _darkMutedBrush : _lightMutedBrush;

    public static Color GetMutedColor() => _currentTheme == ApplicationTheme.Dark ? SlateMuted : Color.FromRgb(0x47, 0x55, 0x69);
    public static Color GetBrandSoftColor() => _currentTheme == ApplicationTheme.Dark ? Color.FromRgb(0xA7, 0x8B, 0xFA) : Color.FromRgb(0x6D, 0x28, 0xD9);

    public static ApplicationTheme CurrentTheme => _currentTheme;
    public static string ThemeMode => _themeMode;
    public static event Action<ApplicationTheme>? ThemeChanged;

    public static ApplicationTheme DetectSystemTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int intVal && intVal == 1)
                return ApplicationTheme.Light;
            return ApplicationTheme.Dark;
        }
        catch
        {
            return ApplicationTheme.Dark;
        }
    }

    public static Application EnsureApplication()
    {
        var app = Application.Current ?? new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };

        if (!_initialized)
        {
            _initialized = true;
            _currentTheme = DetectSystemTheme();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_themeMode.Equals("system", StringComparison.OrdinalIgnoreCase))
                        {
                            SetTheme(DetectSystemTheme());
                        }
                    });
                }
            };
            ApplyResources(app, _currentTheme);
        }

        return app;
    }

    public static void SetThemeMode(string mode)
    {
        _themeMode = string.IsNullOrWhiteSpace(mode) ? "system" : mode.ToLowerInvariant();
        var targetTheme = _themeMode switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => DetectSystemTheme(),
        };
        SetTheme(targetTheme);
    }

    public static void ToggleTheme()
    {
        var nextTheme = _currentTheme == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark;
        _themeMode = nextTheme == ApplicationTheme.Dark ? "dark" : "light";
        SetTheme(nextTheme);
    }

    public static void SetTheme(ApplicationTheme theme)
    {
        _currentTheme = theme;
        var app = EnsureApplication();
        ApplyResources(app, theme);
        try
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
            ApplicationAccentColorManager.Apply(BrandViolet, theme, false, false);
        }
        catch
        {
        }
        ThemeChanged?.Invoke(theme);
    }

    private static void ApplyResources(Application app, ApplicationTheme theme)
    {
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = theme });
        app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
        try
        {
            ApplicationAccentColorManager.Apply(BrandViolet, theme, false, false);
        }
        catch
        {
        }

        var isDark = theme == ApplicationTheme.Dark;
        var overrides = new ResourceDictionary
        {
            ["AccentFillColorDefaultBrush"] = Freeze(new SolidColorBrush(BrandViolet)),
            ["AccentFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(BrandVioletBright)),
            ["AccentFillColorTertiaryBrush"] = Freeze(new SolidColorBrush(BrandVioletDark)),
            ["SystemAccentColorPrimaryBrush"] = Freeze(new SolidColorBrush(BrandViolet)),
            ["SystemAccentColorSecondaryBrush"] = Freeze(new SolidColorBrush(BrandViolet)),
            ["SystemAccentColorTertiaryBrush"] = Freeze(new SolidColorBrush(BrandVioletBright)),
            ["TextOnAccentFillColorPrimaryBrush"] = Brushes.White,
            ["TextOnAccentFillColorSecondaryBrush"] = Brushes.White,
        };

        if (isDark)
        {
            overrides["WindowBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1C)));
            overrides["ApplicationBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x13, 0x1C)));
            overrides["CardBackgroundFillColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x1E, 0x2C)));
            overrides["CardBackgroundFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x13, 0x18, 0x24)));
            overrides["CardStrokeColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x31, 0x45)));
            overrides["ControlFillColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x28, 0x3A)));
            overrides["ControlFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x32, 0x48)));
            overrides["ControlElevationBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x3F, 0x58)));
            overrides["TextFillColorPrimaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)));
            overrides["TextFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
            overrides["TextFillColorTertiaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)));
            overrides["BrandSoftBrush"] = _darkBrandSoftBrush;
            overrides["MutedTextBrush"] = _darkMutedBrush;
            overrides["DataGridSelectedRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)));
            overrides["DataGridHoverRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0x8B, 0x5C, 0xF6)));

            overrides["StatusActiveBadgeBgBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x10, 0xB9, 0x81)));
            overrides["StatusActiveBadgeBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x45, 0x10, 0xB9, 0x81)));
            overrides["StatusActiveBadgeFgBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));

            overrides["StatusStandbyBadgeBgBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0x64, 0x74, 0x8B)));
            overrides["StatusStandbyBadgeBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x35, 0x64, 0x74, 0x8B)));
            overrides["StatusStandbyBadgeFgBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
        }
        else
        {
            overrides["WindowBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
            overrides["ApplicationBackgroundBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
            overrides["CardBackgroundFillColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)));
            overrides["CardBackgroundFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)));
            overrides["CardStrokeColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)));
            overrides["ControlFillColorDefaultBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
            overrides["ControlFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)));
            overrides["ControlElevationBorderBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)));
            overrides["TextFillColorPrimaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)));
            overrides["TextFillColorSecondaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)));
            overrides["TextFillColorTertiaryBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)));
            overrides["BrandSoftBrush"] = _lightBrandSoftBrush;
            overrides["MutedTextBrush"] = _lightMutedBrush;
            overrides["DataGridSelectedRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0x7C, 0x3A, 0xED)));
            overrides["DataGridHoverRowBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x0E, 0x7C, 0x3A, 0xED)));

            overrides["StatusActiveBadgeBgBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0x10, 0xB9, 0x81)));
            overrides["StatusActiveBadgeBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x10, 0xB9, 0x81)));
            overrides["StatusActiveBadgeFgBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)));

            overrides["StatusStandbyBadgeBgBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x18, 0x64, 0x74, 0x8B)));
            overrides["StatusStandbyBadgeBorderBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x28, 0x64, 0x74, 0x8B)));
            overrides["StatusStandbyBadgeFgBrush"] = Freeze(new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)));
        }

        app.Resources.MergedDictionaries.Add(overrides);
    }

    public static void ConfigureWindow(FluentWindow window, string title, double width, double height, double minWidth = 680, double minHeight = 480)
    {
        EnsureApplication();
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.MinWidth = minWidth;
        window.MinHeight = minHeight;
        window.ExtendsContentIntoTitleBar = true;
        window.WindowBackdropType = WindowBackdropType.Mica;
        window.WindowCornerPreference = WindowCornerPreference.Round;
        window.FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI, Segoe UI");
        window.FontSize = 13;
        window.SetResourceReference(Control.BackgroundProperty, "WindowBackgroundBrush");
        window.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        var icon = LoadAppIconSource();
        if (icon is not null)
            window.Icon = icon;
    }

    public static Grid CreateWindowRootGrid()
    {
        var grid = new Grid();
        grid.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
        return grid;
    }

    public static ImageSource? LoadAppIconSource()
    {
        try
        {
            var pngPath = System.IO.Path.Combine(AppContext.BaseDirectory, "mqvpn-icon.png");
            if (System.IO.File.Exists(pngPath))
                return new BitmapImage(new Uri(pngPath, UriKind.Absolute));
            return new BitmapImage(new Uri("pack://application:,,,/mqvpn-icon.png", UriKind.Absolute));
        }
        catch
        {
            return null;
        }
    }

    public static TitleBar CreateTitleBar(string title, UIElement? trailingContent = null, bool showMaximize = true, bool showMinimize = true)
    {
        var bar = new TitleBar
        {
            Title = title,
            ShowMaximize = showMaximize,
            ShowMinimize = showMinimize,
            Icon = new ImageIcon
            {
                Source = LoadAppIconSource(),
                Width = 18,
                Height = 18,
            },
        };
        bar.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (trailingContent is not null)
            bar.TrailingContent = trailingContent;
        return bar;
    }

    public static void StyleDataGrid(Wpf.Ui.Controls.DataGrid grid)
    {
        grid.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        grid.SetResourceReference(Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        grid.SetResourceReference(Control.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        grid.BorderThickness = new Thickness(0);
        grid.GridLinesVisibility = DataGridGridLinesVisibility.None;
        grid.HorizontalGridLinesBrush = Brushes.Transparent;
        grid.VerticalGridLinesBrush = Brushes.Transparent;
        grid.FocusVisualStyle = null;

        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        cellStyle.Setters.Add(new Setter(UIElement.FocusableProperty, false));
        cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")));
        cellStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));

        var cellTemplate = new ControlTemplate(typeof(DataGridCell));
        var cellPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        cellPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cellTemplate.VisualTree = cellPresenter;
        cellStyle.Setters.Add(new Setter(Control.TemplateProperty, cellTemplate));

        var cellFocusedTrigger = new Trigger { Property = UIElement.IsFocusedProperty, Value = true };
        cellFocusedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellFocusedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        cellStyle.Triggers.Add(cellFocusedTrigger);

        var cellSelectedTrigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        cellSelectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        cellSelectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        cellSelectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        cellSelectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")));
        cellStyle.Triggers.Add(cellSelectedTrigger);
        grid.CellStyle = cellStyle;

        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ControlFillColorDefaultBrush")));
        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10)));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension("CardStrokeColorDefaultBrush")));
        grid.ColumnHeaderStyle = headerStyle;

        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(Control.FocusVisualStyleProperty, null));
        rowStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        rowStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush")));

        var rowSelectedTrigger = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        rowSelectedTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("DataGridSelectedRowBrush")));
        rowSelectedTrigger.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        rowSelectedTrigger.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        rowStyle.Triggers.Add(rowSelectedTrigger);

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("DataGridHoverRowBrush")));
        rowStyle.Triggers.Add(hoverTrigger);
        grid.RowStyle = rowStyle;
    }

    public static void StyleCheckBox(CheckBox checkBox)
    {
        checkBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        checkBox.VerticalContentAlignment = VerticalAlignment.Center;
    }

    public static void StyleTabControl(TabControl tabs)
    {
        tabs.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        tabs.Background = Brushes.Transparent;
        tabs.BorderThickness = new Thickness(0);

        var itemStyle = new Style(typeof(TabItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush")));
        itemStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 8, 16, 8)));
        itemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 0, 6, 4)));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension("ControlFillColorDefaultBrush")));

        var template = new ControlTemplate(typeof(TabItem));
        var borderFactory = new FrameworkElementFactory(typeof(Border), "TabBorder");
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(16, 8, 16, 8));
        borderFactory.SetValue(Border.MarginProperty, new Thickness(0, 0, 6, 4));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorDefaultBrush"));
        borderFactory.SetValue(Border.BorderBrushProperty, new DynamicResourceExtension("ControlElevationBorderBrush"));

        var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        contentFactory.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        contentFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        contentFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        borderFactory.AppendChild(contentFactory);
        template.VisualTree = borderFactory;

        var selectedTrigger = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
        selectedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, BrandBrush, "TabBorder"));
        selectedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, BrandBrush, "TabBorder"));
        selectedTrigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(selectedTrigger);

        itemStyle.Setters.Add(new Setter(Control.TemplateProperty, template));
        tabs.ItemContainerStyle = itemStyle;
    }

    public static FrameworkElement CreateMqvpnBrandBadge(double size = 42)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.24),
            Background = new LinearGradientBrush(
                Color.FromRgb(0x2E, 0x10, 0x65),
                Color.FromRgb(0x4C, 0x1D, 0x95),
                45),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xA7, 0x8B, 0xFA)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                Color = BrandVioletBright,
                BlurRadius = 14,
                ShadowDepth = 0,
                Opacity = 0.35,
            },
        };

        var canvas = new Canvas
        {
            Width = 64,
            Height = 64,
        };

        var topBar = new Rectangle
        {
            Width = 34,
            Height = 14,
            RadiusX = 7,
            RadiusY = 7,
            Fill = Freeze(new SolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD))),
        };
        Canvas.SetLeft(topBar, 8);
        Canvas.SetTop(topBar, 14);

        var bottomBar = new Rectangle
        {
            Width = 34,
            Height = 14,
            RadiusX = 7,
            RadiusY = 7,
            Fill = Freeze(new LinearGradientBrush(BrandVioletBright, BrandVioletDark, new Point(0, 0), new Point(1, 1))),
        };
        Canvas.SetLeft(bottomBar, 22);
        Canvas.SetTop(bottomBar, 36);

        var centerDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.White,
        };
        Canvas.SetLeft(centerDot, 28);
        Canvas.SetTop(centerDot, 28);

        canvas.Children.Add(topBar);
        canvas.Children.Add(bottomBar);
        canvas.Children.Add(centerDot);

        border.Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(size * 0.12),
            Child = canvas,
        };
        return border;
    }

    public static SymbolIcon Icon(SymbolRegular symbol, double fontSize = 16, bool filled = false, Brush? foreground = null)
    {
        var icon = new SymbolIcon
        {
            Symbol = symbol,
            Filled = filled,
            FontSize = fontSize,
        };
        if (foreground is not null)
            icon.Foreground = foreground;
        return icon;
    }

    public static System.Windows.Controls.MenuItem CreateMenuItem(
        string header,
        SymbolRegular? symbol = null,
        RoutedEventHandler? onClick = null,
        bool isCheckable = false,
        bool isChecked = false)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = header,
            IsCheckable = isCheckable,
            IsChecked = isChecked,
            Cursor = Cursors.Hand,
            FontSize = 12.5,
        };
        item.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        if (symbol is { } s)
        {
            item.Icon = Icon(s, 15, foreground: BrandSoftBrush);
        }
        if (onClick is not null)
            item.Click += onClick;
        return item;
    }

    public static Wpf.Ui.Controls.Button ActionButton(
        string text,
        SymbolRegular? symbol = null,
        ControlAppearance appearance = ControlAppearance.Secondary,
        RoutedEventHandler? onClick = null,
        Thickness? padding = null)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = text,
            Appearance = appearance,
            Padding = padding ?? new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (appearance == ControlAppearance.Secondary)
        {
            button.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        }
        else if (appearance == ControlAppearance.Primary)
        {
            button.Background = Freeze(new SolidColorBrush(BrandViolet));
            button.BorderBrush = Freeze(new SolidColorBrush(BrandVioletBright));
            button.Foreground = Brushes.White;
            button.MouseOverBackground = BrandBrush;
            button.MouseOverBorderBrush = BrandSoftBrush;
            button.PressedBackground = Freeze(new SolidColorBrush(BrandVioletDark));
            button.PressedForeground = Brushes.White;
        }

        if (symbol is { } s)
            button.Icon = Icon(s, 15, foreground: appearance == ControlAppearance.Primary ? Brushes.White : BrandSoftBrush);
        if (onClick is not null)
            button.Click += onClick;
        return button;
    }

    public static Border CreateCardBorder(UIElement child, Thickness? padding = null, Thickness? margin = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(16, 14, 16, 14),
            Margin = margin ?? new Thickness(0),
            Child = child,
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    public static System.Windows.Controls.TextBlock PrimaryText(string text, double size = 13, FontWeight? weight = null)
    {
        var block = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        return block;
    }

    public static System.Windows.Controls.TextBlock SecondaryText(string text, double size = 12)
    {
        var block = new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return block;
    }

    public static Border SectionHeader(string text, SymbolRegular symbol)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 14, 0, 8),
        };
        row.Children.Add(new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)),
            Margin = new Thickness(0, 0, 8, 0),
            Child = Icon(symbol, 14, foreground: BrandSoftBrush),
        });
        row.Children.Add(PrimaryText(text, 13.5, FontWeights.SemiBold));
        return new Border { Child = row };
    }

    public static Border SettingCard(string label, UIElement editor, string? help = null, bool stackEditorBelow = false)
    {
        if (editor is CheckBox cb)
            StyleCheckBox(cb);
        else if (editor is Control ctrl)
            ctrl.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

        if (stackEditorBelow)
        {
            var stack = new StackPanel();
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            header.Children.Add(PrimaryText(label, 13, FontWeights.SemiBold));
            if (!string.IsNullOrWhiteSpace(help))
            {
                var hint = SecondaryText(help.Trim(), 11.5);
                hint.Margin = new Thickness(0, 3, 0, 0);
                header.Children.Add(hint);
            }
            stack.Children.Add(header);
            stack.Children.Add(editor);
            return CreateCardBorder(stack, new Thickness(14, 11, 14, 12), new Thickness(0, 0, 0, 8));
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310) });

        var left = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        left.Children.Add(PrimaryText(label, 13, FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(help))
        {
            var hint = SecondaryText(help.Trim(), 11.5);
            hint.Margin = new Thickness(0, 3, 0, 0);
            left.Children.Add(hint);
        }

        var rightHost = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = editor is CheckBox ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Child = editor,
        };

        Grid.SetColumn(left, 0);
        Grid.SetColumn(rightHost, 1);
        grid.Children.Add(left);
        grid.Children.Add(rightHost);

        return CreateCardBorder(grid, new Thickness(14, 10, 14, 10), new Thickness(0, 0, 0, 8));
    }

    public static void ShowAlert(Window? owner, string message, string title, bool isError = false)
    {
        ShowModalDialog(owner, title, message, isConfirm: false, isError: isError);
    }

    public static bool Confirm(Window? owner, string message, string title)
    {
        return ShowModalDialog(owner, title, message, isConfirm: true, isError: false);
    }

    private static bool ShowModalDialog(Window? owner, string title, string message, bool isConfirm, bool isError)
    {
        var dialog = new FluentWindow
        {
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height,
        };
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        ConfigureWindow(dialog, title, 440, 220, 400, 180);

        var root = CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = CreateTitleBar(title, showMaximize: false, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var bodyGrid = new Grid { Margin = new Thickness(22, 16, 22, 18) };
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badgeColor = isError ? CrimsonRed : isConfirm ? AmberGold : BrandVioletBright;
        var badgeIcon = isError ? SymbolRegular.DismissCircle24 : isConfirm ? SymbolRegular.Warning24 : SymbolRegular.Info24;
        var iconBox = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x26, badgeColor.R, badgeColor.G, badgeColor.B)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 14, 0),
            Child = Icon(badgeIcon, 22, foreground: new SolidColorBrush(badgeColor)),
        };
        var textBlock = PrimaryText(message, 13);
        textBlock.TextWrapping = TextWrapping.Wrap;
        textBlock.LineHeight = 20;
        Grid.SetColumn(iconBox, 0);
        Grid.SetColumn(textBlock, 1);
        bodyGrid.Children.Add(iconBox);
        bodyGrid.Children.Add(textBlock);
        Grid.SetRow(bodyGrid, 1);
        root.Children.Add(bodyGrid);

        var footer = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var accepted = false;
        if (isConfirm)
        {
            var cancelBtn = ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) =>
            {
                accepted = false;
                dialog.Close();
            });
            cancelBtn.MinWidth = 88;
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancelBtn);
        }

        var okBtn = ActionButton(
            isConfirm ? "确认" : "知道了",
            SymbolRegular.Checkmark20,
            isError ? ControlAppearance.Danger : ControlAppearance.Primary,
            (_, _) =>
            {
                accepted = true;
                dialog.Close();
            });
        okBtn.MinWidth = 92;
        buttons.Children.Add(okBtn);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        dialog.Content = root;
        dialog.ShowDialog();
        return accepted;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze)
            freezable.Freeze();
        return freezable;
    }
}

internal sealed class ToggleSwitch : Border
{
    private readonly Wpf.Ui.Controls.ToggleSwitch _switch;
    private readonly ProgressRing _ring;
    private bool _isOn;
    private bool _busy;
    private bool _suppressEvent;

    public ToggleSwitch()
    {
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;

        _ring = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsIndeterminate = true,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _switch = new Wpf.Ui.Controls.ToggleSwitch
        {
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
        };
        _switch.Click += OnSwitchClicked;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_ring);
        panel.Children.Add(_switch);
        Child = panel;
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            _isOn = value;
            _suppressEvent = true;
            _switch.IsChecked = value;
            _suppressEvent = false;
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            _busy = value;
            _ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _switch.IsEnabled = Enabled && !value;
            Cursor = value ? Cursors.Wait : Cursors.Hand;
        }
    }

    public bool Enabled
    {
        get => IsEnabled;
        set
        {
            IsEnabled = value;
            _switch.IsEnabled = value && !_busy;
        }
    }

    public event Action<bool>? ToggleRequested;

    public void RequestToggle()
    {
        if (Enabled && !Busy)
            ToggleRequested?.Invoke(!IsOn);
    }

    private void OnSwitchClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvent)
            return;

        _suppressEvent = true;
        _switch.IsChecked = _isOn;
        _suppressEvent = false;

        e.Handled = true;
        RequestToggle();
    }
}
