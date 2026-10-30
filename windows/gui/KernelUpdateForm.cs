using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed record KernelUpdatePreferences(
    string GuiSource,
    string MqvpnSource,
    string XraySource,
    string GeoDataSource,
    bool GuiIncludePreRelease,
    bool MqvpnIncludePreRelease,
    bool XrayIncludePreRelease)
{
    public KernelUpdatePreferences(
        string mqvpnSource,
        string xraySource,
        string geoDataSource,
        bool mqvpnIncludePreRelease,
        bool xrayIncludePreRelease)
        : this("r11234567/mqvpn", mqvpnSource, xraySource, geoDataSource, false, mqvpnIncludePreRelease, xrayIncludePreRelease)
    {
    }
}

internal sealed class KernelUpdateForm : FluentWindow, IDisposable
{
    private readonly string _runtimeRoot;
    private readonly Func<Task> _stopServices;
    private readonly Action<KernelUpdatePreferences> _savePreferences;
    private readonly KernelRow _gui;
    private readonly KernelRow _mqvpn;
    private readonly KernelRow _xray;
    private readonly KernelRow _geodata;

    public KernelUpdateForm(
        string runtimeRoot,
        KernelUpdatePreferences preferences,
        Func<Task> stopServices,
        Action<KernelUpdatePreferences> savePreferences)
    {
        FluentUi.ConfigureWindow(this, "组件检查与自更新中心", 880, 680, 760, 520);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _runtimeRoot = runtimeRoot;
        _stopServices = stopServices;
        _savePreferences = savePreferences;

        _gui = new KernelRow(
            "gui",
            "MQVPN 客户端 (GUI)",
            "Windows 桌面客户端界面与自更新组件 (MQVPN.exe / MqvpnUpgrade.exe)",
            SymbolRegular.Desktop24,
            ["r11234567/mqvpn", "mp0rta/mqvpn"],
            preferences.GuiSource,
            true,
            preferences.GuiIncludePreRelease,
            CheckAsync,
            UpdateAsync);

        _mqvpn = new KernelRow(
            "mqvpn",
            "MQVPN 核心",
            "Multipath QUIC 隧道客户端引擎 (mqvpn.exe / xquic.dll / wintun.dll)",
            SymbolRegular.ShieldGlobe24,
            ["mp0rta/mqvpn", "r11234567/mqvpn"],
            preferences.MqvpnSource,
            true,
            preferences.MqvpnIncludePreRelease,
            CheckAsync,
            UpdateAsync);

        _xray = new KernelRow(
            "xray",
            "Xray 分流核心",
            "TUN 透明路由与进程/域名分流引擎 (xray.exe)",
            SymbolRegular.Router24,
            ["XTLS/Xray-core"],
            preferences.XraySource,
            true,
            preferences.XrayIncludePreRelease,
            CheckAsync,
            UpdateAsync);

        _geodata = new KernelRow(
            "geodata",
            "GeoIP / GeoSite 规则库",
            "中国大陆与海外域名/IP 分流数据库 (geoip.dat + geosite.dat)",
            SymbolRegular.Globe24,
            ["Loyalsoldier/v2ray-rules-dat"],
            preferences.GeoDataSource,
            false,
            false,
            CheckAsync,
            UpdateAsync);

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar("组件检查与自更新中心", showMaximize: true, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bannerIcon = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x28, 0x8B, 0x5C, 0xF6)),
            Margin = new Thickness(0, 0, 12, 0),
            Child = FluentUi.Icon(SymbolRegular.CloudArrowDown24, 22, foreground: FluentUi.BrandSoftBrush),
        };

        var bannerStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        bannerStack.Children.Add(FluentUi.PrimaryText("成熟多组件检查与安全热更新", 14, FontWeights.SemiBold));
        var sub = FluentUi.SecondaryText("借鉴成熟架构：客户端 GUI 采用独立更新助手 (MqvpnUpgrade) 无锁热替换，内核与分流规则支持一键比对与原子回滚。", 12);
        sub.Margin = new Thickness(0, 3, 0, 0);
        bannerStack.Children.Add(sub);

        var topActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var btnCheckAll = FluentUi.ActionButton("一键检查全部", SymbolRegular.ArrowSync20, ControlAppearance.Secondary, async (_, _) => await CheckAllAsync());
        btnCheckAll.Margin = new Thickness(0, 0, 8, 0);
        var btnUpdateAll = FluentUi.ActionButton("一键更新全部", SymbolRegular.ArrowDownload20, ControlAppearance.Primary, async (_, _) => await UpdateAllAsync());
        topActions.Children.Add(btnCheckAll);
        topActions.Children.Add(btnUpdateAll);

        Grid.SetColumn(bannerIcon, 0);
        Grid.SetColumn(bannerStack, 1);
        Grid.SetColumn(topActions, 2);
        bannerGrid.Children.Add(bannerIcon);
        bannerGrid.Children.Add(bannerStack);
        bannerGrid.Children.Add(topActions);

        var bannerCard = FluentUi.CreateCardBorder(bannerGrid, new Thickness(16, 12, 16, 12), new Thickness(18, 8, 18, 10));
        Grid.SetRow(bannerCard, 1);
        root.Children.Add(bannerCard);

        var rowsStack = new StackPanel
        {
            Margin = new Thickness(18, 0, 18, 12),
        };
        rowsStack.Children.Add(_gui);
        rowsStack.Children.Add(_mqvpn);
        rowsStack.Children.Add(_xray);
        rowsStack.Children.Add(_geodata);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = rowsStack,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var closeBtn = FluentUi.ActionButton("保存并关闭", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) =>
        {
            SavePreferences();
            Close();
        });
        closeBtn.MinWidth = 110;
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Child = closeBtn;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Closing += (_, _) => SavePreferences();
        Content = root;
    }

    public void ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        ShowDialog();
    }

    public void Dispose()
    {
        if (IsVisible)
            Close();
    }

    private async Task CheckAllAsync()
    {
        await Task.WhenAll(
            CheckAsync(_gui),
            CheckAsync(_mqvpn),
            CheckAsync(_xray),
            CheckAsync(_geodata));
    }

    private async Task UpdateAllAsync()
    {
        var rows = new[] { _geodata, _mqvpn, _xray, _gui };
        foreach (var row in rows)
        {
            if (row.Release is null)
                await CheckAsync(row);
        }

        var updatable = rows.Where(r => r.HasNewVersion).ToList();
        if (updatable.Count == 0)
        {
            FluentUi.ShowAlert(this, "所有组件均为最新版本，无需更新。", "检查结果");
            return;
        }

        var names = string.Join("、", updatable.Select(r => r.DisplayName));
        if (!FluentUi.Confirm(this, $"检测到以下组件有可用更新：\n{names}\n\n确认立即开始更新？", "一键批量更新"))
            return;

        // 优先更新规则库和内核
        foreach (var r in updatable.Where(x => x.Kernel != "gui"))
        {
            await UpdateAsync(r);
        }

        // GUI 最后更新（因无锁热替换会重启进程）
        var guiRow = updatable.FirstOrDefault(x => x.Kernel == "gui");
        if (guiRow is not null)
        {
            await UpdateAsync(guiRow);
        }
    }

    private async Task CheckAsync(KernelRow row)
    {
        try
        {
            row.SetBusy(true, "正在查询本地版本与 GitHub 最新发行版...");
            row.InstalledVersion = await KernelInstaller.GetInstalledVersionAsync(row.Kernel, _runtimeRoot);
            row.Release = await GitHubReleaseClient.GetLatestAsync(
                row.Source,
                row.IncludePreRelease,
                CancellationToken.None);

            var assetNames = row.Kernel == "geodata"
                ? string.Join(" + ", KernelInstaller.SelectGeoDataAssets(row.Release).Select(asset => asset.Name))
                : KernelInstaller.SelectAsset(row.Kernel, row.Release).Name;
            var marker = row.Release.IsPreRelease ? "（预发布）" : string.Empty;

            var isNewer = !string.IsNullOrWhiteSpace(row.InstalledVersion) &&
                          !string.Equals(row.InstalledVersion.TrimStart('v'), row.Release.Tag.TrimStart('v'), StringComparison.OrdinalIgnoreCase);
            row.HasNewVersion = isNewer;

            var versionTip = isNewer ? " [发现新版本]" : " [已是最新]";
            row.SetStatus(
                $"已安装：{row.InstalledVersion} | 最新：{row.Release.Tag}{marker}{versionTip}{Environment.NewLine}" +
                $"更新包：{assetNames}");

            if (!string.IsNullOrWhiteSpace(row.Release.Body))
            {
                row.SetChangelog(row.Release.Body);
            }
        }
        catch (Exception ex)
        {
            row.SetStatus("检查失败：" + ex.Message);
        }
        finally
        {
            row.SetBusy(false);
            SavePreferences();
        }
    }

    private async Task UpdateAsync(KernelRow row)
    {
        try
        {
            if (row.Release is null)
                await CheckAsync(row);
            if (row.Release is null)
                return;

            var marker = row.Release.IsPreRelease ? "预发布版本 " : string.Empty;

            if (row.Kernel == "gui")
            {
                var asset = KernelInstaller.SelectAsset("gui", row.Release);
                if (!FluentUi.Confirm(this, $"确认下载并更新客户端到 {marker}{row.Release.Tag}？\n\n更新包将通过独立更新程序 (MqvpnUpgrade) 自动覆盖并重启客户端。", "确认自更新"))
                    return;

                row.SetBusy(true, "正在下载客户端更新包...");
                row.ShowProgress(true);
                var progress = new Progress<(long received, long total)>(p =>
                {
                    if (p.total > 0)
                    {
                        var pct = (int)(p.received * 100 / p.total);
                        row.UpdateProgress(pct, $"正在下载客户端: {pct}% ({p.received / 1024d / 1024d:0.0} MB / {p.total / 1024d / 1024d:0.0} MB)");
                    }
                    else
                    {
                        row.UpdateProgress(0, $"正在下载客户端: {p.received / 1024d / 1024d:0.0} MB");
                    }
                });

                await KernelInstaller.InstallGuiAsync(asset, progress, CancellationToken.None, _stopServices);
                return;
            }

            var assets = row.Kernel == "geodata"
                ? KernelInstaller.SelectGeoDataAssets(row.Release)
                : new[] { KernelInstaller.SelectAsset(row.Kernel, row.Release) };

            if (!FluentUi.Confirm(this, $"确认停止当前连接并安装 {marker}{row.Release.Tag}？", "确认更新"))
                return;

            row.SetBusy(true, "正在停止运行中的内核服务...");
            await _stopServices();

            row.ShowProgress(true);
            row.SetStatus("正在下载并校验 " + string.Join(" + ", assets.Select(asset => asset.Name)));

            if (row.Kernel == "geodata")
                await KernelInstaller.InstallGeoDataAsync(assets, _runtimeRoot, CancellationToken.None);
            else
                await KernelInstaller.InstallAsync(row.Kernel, assets[0], _runtimeRoot, CancellationToken.None);

            row.InstalledVersion = await KernelInstaller.GetInstalledVersionAsync(row.Kernel, _runtimeRoot);
            row.SetStatus("更新完成：" + row.InstalledVersion);
        }
        catch (Exception ex)
        {
            row.SetStatus("更新失败（原版本或恢复信息已保留）：" + ex.Message);
        }
        finally
        {
            row.ShowProgress(false);
            row.SetBusy(false);
            SavePreferences();
        }
    }

    private void SavePreferences() => _savePreferences(new KernelUpdatePreferences(
        _gui.Source,
        _mqvpn.Source,
        _xray.Source,
        _geodata.Source,
        _gui.IncludePreRelease,
        _mqvpn.IncludePreRelease,
        _xray.IncludePreRelease));
}

internal sealed class KernelRow : Border
{
    private readonly ComboBox _source = new() { IsEditable = true, MinWidth = 200 };
    private readonly CheckBox _preRelease = new() { Content = "预发布" };
    private readonly System.Windows.Controls.TextBlock _status;
    private readonly System.Windows.Controls.ProgressBar _progressBar = new() { Height = 4, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Wpf.Ui.Controls.Button _check;
    private readonly Wpf.Ui.Controls.Button _update;
    private readonly Wpf.Ui.Controls.Button _changelogBtn;
    private readonly System.Windows.Controls.TextBox _changelogBox;
    private readonly ProgressRing _ring = new() { Width = 18, Height = 18, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly bool _supportsPreRelease;

    public KernelRow(
        string kernel,
        string displayName,
        string description,
        SymbolRegular icon,
        string[] sources,
        string selectedSource,
        bool supportsPreRelease,
        bool includePreRelease,
        Func<KernelRow, Task> check,
        Func<KernelRow, Task> update)
    {
        Kernel = kernel;
        DisplayName = displayName;
        _supportsPreRelease = supportsPreRelease;

        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 14, 16, 14);
        Margin = new Thickness(0, 0, 0, 12);
        SetResourceReference(BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        foreach (var item in sources)
            _source.Items.Add(item);
        _source.Text = selectedSource;

        FluentUi.StyleCheckBox(_preRelease);
        _preRelease.IsChecked = includePreRelease;
        _preRelease.Margin = new Thickness(8, 0, 8, 0);
        _preRelease.VerticalAlignment = VerticalAlignment.Center;
        _preRelease.Checked += (_, _) => OnPreReleaseChanged();
        _preRelease.Unchecked += (_, _) => OnPreReleaseChanged();

        _check = FluentUi.ActionButton("检查更新", SymbolRegular.ArrowSync20, ControlAppearance.Secondary, async (_, _) => await check(this));
        _check.Margin = new Thickness(0, 0, 6, 0);
        _update = FluentUi.ActionButton("安装", SymbolRegular.CloudArrowDown20, ControlAppearance.Primary, async (_, _) => await update(this));
        _update.Margin = new Thickness(0, 0, 6, 0);

        _changelogBtn = FluentUi.ActionButton("更新日志", SymbolRegular.DocumentBulletList20, ControlAppearance.Secondary, (_, _) => ToggleChangelog());
        _changelogBtn.Visibility = Visibility.Collapsed;

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBadge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x8B, 0x5C, 0xF6)),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = FluentUi.Icon(icon, 20, foreground: FluentUi.BrandSoftBrush),
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        titleStack.Children.Add(FluentUi.PrimaryText(displayName, 14, FontWeights.SemiBold));
        var descText = FluentUi.SecondaryText(description, 11.5);
        descText.Margin = new Thickness(0, 2, 0, 0);
        titleStack.Children.Add(descText);

        var controlsBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ring.Margin = new Thickness(0, 0, 8, 0);
        controlsBar.Children.Add(_ring);
        controlsBar.Children.Add(_source);
        if (supportsPreRelease)
            controlsBar.Children.Add(_preRelease);
        else
            _source.Margin = new Thickness(0, 0, 8, 0);
        controlsBar.Children.Add(_check);
        controlsBar.Children.Add(_update);
        controlsBar.Children.Add(_changelogBtn);

        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(controlsBar, 2);
        topGrid.Children.Add(iconBadge);
        topGrid.Children.Add(titleStack);
        topGrid.Children.Add(controlsBar);

        _status = new System.Windows.Controls.TextBlock
        {
            Text = "尚未检查（点击“检查更新”获取版本详情）",
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        _status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var statusStack = new StackPanel();
        statusStack.Children.Add(_status);
        statusStack.Children.Add(_progressBar);

        var statusBox = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = statusStack,
        };
        statusBox.SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        statusBox.SetResourceReference(BorderBrushProperty, "ControlElevationBorderBrush");

        _changelogBox = new System.Windows.Controls.TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 160,
            FontSize = 11.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            Visibility = Visibility.Collapsed,
        };
        _changelogBox.SetResourceReference(System.Windows.Controls.TextBox.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        _changelogBox.SetResourceReference(System.Windows.Controls.TextBox.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _changelogBox.SetResourceReference(System.Windows.Controls.TextBox.ForegroundProperty, "TextFillColorPrimaryBrush");

        var rootStack = new StackPanel();
        rootStack.Children.Add(topGrid);
        rootStack.Children.Add(statusBox);
        rootStack.Children.Add(_changelogBox);
        Child = rootStack;
    }

    public string Kernel { get; }
    public string DisplayName { get; }
    public string Source => _source.Text.Trim();
    public bool IncludePreRelease => _supportsPreRelease && _preRelease.IsChecked == true;
    public string InstalledVersion { get; set; } = string.Empty;
    public GitHubRelease? Release { get; set; }
    public bool HasNewVersion { get; set; }

    private void OnPreReleaseChanged()
    {
        Release = null;
        HasNewVersion = false;
        SetStatus("预发布选项已更改，请重新检查更新");
    }

    public void SetStatus(string value)
    {
        _status.Text = value;
    }

    public void ShowProgress(bool show)
    {
        _progressBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            _progressBar.Value = 0;
    }

    public void UpdateProgress(int percentage, string status)
    {
        _progressBar.Value = percentage;
        _status.Text = status;
    }

    public void SetChangelog(string changelog)
    {
        _changelogBox.Text = changelog;
        _changelogBtn.Visibility = !string.IsNullOrWhiteSpace(changelog) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleChangelog()
    {
        _changelogBox.Visibility = _changelogBox.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void SetBusy(bool busy, string? status = null)
    {
        _source.IsEnabled = !busy;
        _preRelease.IsEnabled = !busy;
        _check.IsEnabled = !busy;
        _update.IsEnabled = !busy;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
            SetStatus(status);
    }
}
