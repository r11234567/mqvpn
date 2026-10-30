using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed class RoutingRuleViewModel
{
    public RoutingRule Model { get; }

    public RoutingRuleViewModel(RoutingRule model)
    {
        Model = model;
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set => Model.Enabled = value;
    }

    public string Name => Model.Name;
    public string TypeDisplay => RoutingRuleForm.GetTypeDisplay(Model.Type);
    public string Value => Model.Value;
    public string Network => Model.Network;
    public string Outbound => Model.Outbound;
}

internal sealed class RoutingSettingsForm : FluentWindow, IDisposable
{
    private readonly RoutingDocument _routing;
    private readonly ComboBox _policies = new() { Width = 240 };
    private readonly ComboBox _finalOutbound = new() { Width = 150 };
    private readonly Wpf.Ui.Controls.DataGrid _rules = new();
    private readonly ObservableCollection<RoutingRuleViewModel> _ruleItems = [];
    private bool _updatingPolicy;

    public RoutingSettingsForm(RoutingDocument routing)
    {
        FluentUi.ConfigureWindow(this, "路由分流设置", 960, 650, 840, 520);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _routing = JsonSerializer.Deserialize<RoutingDocument>(JsonSerializer.Serialize(routing))
            ?? new RoutingDocument();

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar("路由分流策略与规则", showMaximize: true, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var topGrid = new Grid();
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var policyBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        policyBar.Children.Add(FluentUi.Icon(SymbolRegular.Router20, 18, foreground: FluentUi.BrandSoftBrush));
        var policyLabel = FluentUi.PrimaryText("当前策略", 13, FontWeights.SemiBold);
        policyLabel.Margin = new Thickness(8, 0, 10, 0);
        policyBar.Children.Add(policyLabel);

        _policies.Margin = new Thickness(0, 0, 10, 0);
        _policies.SelectionChanged += (_, _) =>
        {
            if (!_updatingPolicy)
                RefreshRules();
        };
        policyBar.Children.Add(_policies);

        var btnNew = FluentUi.ActionButton("新建", SymbolRegular.Add20, ControlAppearance.Secondary, (_, _) => AddPolicy());
        btnNew.Margin = new Thickness(0, 0, 6, 0);
        var btnClone = FluentUi.ActionButton("克隆", SymbolRegular.Copy20, ControlAppearance.Secondary, (_, _) => ClonePolicy());
        btnClone.Margin = new Thickness(0, 0, 6, 0);
        var btnDelete = FluentUi.ActionButton("删除", SymbolRegular.Delete20, ControlAppearance.Secondary, (_, _) => DeletePolicy());
        policyBar.Children.Add(btnNew);
        policyBar.Children.Add(btnClone);
        policyBar.Children.Add(btnDelete);

        var finalBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var finalLabel = FluentUi.SecondaryText("未匹配流量出站", 12.5);
        finalLabel.Margin = new Thickness(0, 0, 8, 0);
        finalBar.Children.Add(finalLabel);
        foreach (var item in new[] { "DIRECT", "MQVPN-TUN", "REJECT" })
            _finalOutbound.Items.Add(item);
        _finalOutbound.SelectionChanged += (_, _) =>
        {
            if (!_updatingPolicy && CurrentPolicy is { } policy && _finalOutbound.SelectedItem is string val)
                policy.FinalOutbound = val;
        };
        finalBar.Children.Add(_finalOutbound);

        Grid.SetColumn(policyBar, 0);
        Grid.SetColumn(finalBar, 1);
        topGrid.Children.Add(policyBar);
        topGrid.Children.Add(finalBar);

        var topCard = FluentUi.CreateCardBorder(topGrid, new Thickness(16, 12, 16, 12), new Thickness(18, 8, 18, 10));
        Grid.SetRow(topCard, 1);
        root.Children.Add(topCard);

        var centerGrid = new Grid { Margin = new Thickness(18, 0, 18, 12) };
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        centerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });

        FluentUi.StyleDataGrid(_rules);
        _rules.AutoGenerateColumns = false;
        _rules.CanUserAddRows = false;
        _rules.CanUserDeleteRows = false;
        _rules.IsReadOnly = false;
        _rules.SelectionMode = DataGridSelectionMode.Single;
        _rules.HeadersVisibility = DataGridHeadersVisibility.Column;
        _rules.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "启用",
            Binding = new Binding(nameof(RoutingRuleViewModel.Enabled)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 64,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "规则名称",
            Binding = new Binding(nameof(RoutingRuleViewModel.Name)),
            IsReadOnly = true,
            Width = 175,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "匹配类型",
            Binding = new Binding(nameof(RoutingRuleViewModel.TypeDisplay)),
            IsReadOnly = true,
            Width = 120,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "匹配值",
            Binding = new Binding(nameof(RoutingRuleViewModel.Value)),
            IsReadOnly = true,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "网络",
            Binding = new Binding(nameof(RoutingRuleViewModel.Network)),
            IsReadOnly = true,
            Width = 86,
        });
        _rules.Columns.Add(new DataGridTextColumn
        {
            Header = "出站动作",
            Binding = new Binding(nameof(RoutingRuleViewModel.Outbound)),
            IsReadOnly = true,
            Width = 115,
        });
        _rules.MouseDoubleClick += (_, _) =>
        {
            if (_rules.SelectedItem is RoutingRuleViewModel)
                EditRule();
        };
        _rules.ItemsSource = _ruleItems;

        var tableCard = FluentUi.CreateCardBorder(_rules, new Thickness(4), new Thickness(0, 0, 12, 0));
        Grid.SetColumn(tableCard, 0);
        centerGrid.Children.Add(tableCard);

        var sideStack = new StackPanel();
        sideStack.Children.Add(MakeSideButton("添加规则", SymbolRegular.Add20, ControlAppearance.Primary, (_, _) => AddRule()));
        sideStack.Children.Add(MakeSideButton("编辑规则", SymbolRegular.Edit20, ControlAppearance.Secondary, (_, _) => EditRule()));
        sideStack.Children.Add(MakeSideButton("删除规则", SymbolRegular.Delete20, ControlAppearance.Secondary, (_, _) => DeleteRule()));
        sideStack.Children.Add(new Separator { Margin = new Thickness(4, 6, 4, 10) });
        sideStack.Children.Add(MakeSideButton("上移优先级", SymbolRegular.ArrowUp20, ControlAppearance.Secondary, (_, _) => MoveRule(-1)));
        sideStack.Children.Add(MakeSideButton("下移优先级", SymbolRegular.ArrowDown20, ControlAppearance.Secondary, (_, _) => MoveRule(1)));

        var sideCard = FluentUi.CreateCardBorder(sideStack, new Thickness(12, 12, 12, 12));
        Grid.SetColumn(sideCard, 1);
        centerGrid.Children.Add(sideCard);

        Grid.SetRow(centerGrid, 2);
        root.Children.Add(centerGrid);

        var footer = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var note = FluentUi.SecondaryText("提示：系统会自动置顶生成 mqvpn 防回环、服务端绕行及局域网直连规则。双击任意行可编辑。", 12);
        Grid.SetColumn(note, 0);
        footerGrid.Children.Add(note);

        var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) =>
        {
            DialogResult = false;
            Close();
        });
        cancel.MinWidth = 92;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        var save = FluentUi.ActionButton("保存策略", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) => SaveAndClose());
        save.MinWidth = 108;
        footerButtons.Children.Add(cancel);
        footerButtons.Children.Add(save);
        Grid.SetColumn(footerButtons, 1);
        footerGrid.Children.Add(footerButtons);

        footer.Child = footerGrid;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        RefreshPolicies();
    }

    public RoutingDocument Result => _routing;
    private RoutingPolicy? CurrentPolicy => _policies.SelectedItem as RoutingPolicy;

    public bool ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        return ShowDialog() == true;
    }

    public void Dispose()
    {
        if (IsVisible)
            Close();
    }

    private static Wpf.Ui.Controls.Button MakeSideButton(string text, SymbolRegular icon, ControlAppearance appearance, RoutedEventHandler click)
    {
        var btn = FluentUi.ActionButton(text, icon, appearance, click);
        btn.HorizontalAlignment = HorizontalAlignment.Stretch;
        btn.Margin = new Thickness(0, 0, 0, 8);
        return btn;
    }

    private void RefreshPolicies(string? selectId = null)
    {
        _updatingPolicy = true;
        var selected = selectId ?? CurrentPolicy?.Id ?? _routing.Policies.FirstOrDefault()?.Id;
        _policies.ItemsSource = null;
        _policies.DisplayMemberPath = nameof(RoutingPolicy.Name);
        _policies.SelectedValuePath = nameof(RoutingPolicy.Id);
        _policies.ItemsSource = _routing.Policies.ToList();
        if (selected is not null)
            _policies.SelectedValue = selected;
        if (_policies.SelectedIndex < 0 && _policies.Items.Count > 0)
            _policies.SelectedIndex = 0;
        _updatingPolicy = false;
        RefreshRules();
    }

    private void RefreshRules(int? selectIndex = null)
    {
        _updatingPolicy = true;
        _ruleItems.Clear();
        if (CurrentPolicy is { } policy)
        {
            foreach (var rule in policy.Rules)
                _ruleItems.Add(new RoutingRuleViewModel(rule));
            _finalOutbound.SelectedItem = policy.FinalOutbound;
            if (selectIndex is { } idx && idx >= 0 && idx < _ruleItems.Count)
                _rules.SelectedIndex = idx;
        }
        _updatingPolicy = false;
    }

    private void AddPolicy()
    {
        using var prompt = new TextPromptForm("新建策略", "请输入新路由策略的名称：");
        if (!prompt.ShowModal(this))
            return;
        var policy = new RoutingPolicy { Name = prompt.Value };
        _routing.Policies.Add(policy);
        RefreshPolicies(policy.Id);
    }

    private void ClonePolicy()
    {
        if (CurrentPolicy is not { } source)
            return;
        var clone = JsonSerializer.Deserialize<RoutingPolicy>(JsonSerializer.Serialize(source))!;
        clone.Id = Guid.NewGuid().ToString("N");
        clone.Name += " - 副本";
        _routing.Policies.Add(clone);
        RefreshPolicies(clone.Id);
    }

    private void DeletePolicy()
    {
        if (CurrentPolicy is not { } policy || _routing.Policies.Count == 1)
            return;
        if (!FluentUi.Confirm(this, $"确认删除路由策略“{policy.Name}”？", "确认删除"))
            return;
        _routing.Policies.Remove(policy);
        RefreshPolicies();
    }

    private void AddRule()
    {
        if (CurrentPolicy is not { } policy)
            return;
        using var editor = new RoutingRuleForm(new RoutingRule());
        if (!editor.ShowModal(this))
            return;
        policy.Rules.Add(editor.Result);
        RefreshRules(policy.Rules.Count - 1);
    }

    private void EditRule()
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel vm)
            return;
        using var editor = new RoutingRuleForm(vm.Model);
        if (!editor.ShowModal(this))
            return;
        var index = policy.Rules.IndexOf(vm.Model);
        if (index >= 0)
        {
            policy.Rules[index] = editor.Result;
            RefreshRules(index);
        }
    }

    private void DeleteRule()
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel vm)
            return;
        var index = policy.Rules.IndexOf(vm.Model);
        policy.Rules.Remove(vm.Model);
        RefreshRules(Math.Min(index, policy.Rules.Count - 1));
    }

    private void MoveRule(int direction)
    {
        if (CurrentPolicy is not { } policy || _rules.SelectedItem is not RoutingRuleViewModel vm)
            return;
        var oldIndex = policy.Rules.IndexOf(vm.Model);
        var newIndex = oldIndex + direction;
        if (newIndex < 0 || newIndex >= policy.Rules.Count)
            return;
        policy.Rules.RemoveAt(oldIndex);
        policy.Rules.Insert(newIndex, vm.Model);
        RefreshRules(newIndex);
    }

    private void SaveAndClose()
    {
        if (_routing.Policies.Any(policy => string.IsNullOrWhiteSpace(policy.Name)))
            return;
        DialogResult = true;
        Close();
    }
}

internal sealed class RoutingRuleForm : FluentWindow, IDisposable
{
    private static readonly RuleTypeOption[] RuleTypes =
    [
        new("PROCESS-NAME", "进程名称"),
        new("DOMAIN", "完整域名"),
        new("DOMAIN-SUFFIX", "域名后缀"),
        new("IP-CIDR", "IP/CIDR 网段"),
        new("DST-PORT", "目标端口"),
        new("GEOIP", "GeoIP 分类"),
        new("GEOSITE", "GeoSite 分类"),
        new("PROTOCOL", "协议类型"),
    ];

    private readonly CheckBox _enabled = new() { Content = "启用此规则" };
    private readonly Wpf.Ui.Controls.TextBox _name = new() { PlaceholderText = "例如：TruckersMP Launcher" };
    private readonly ComboBox _type = new();
    private readonly Wpf.Ui.Controls.TextBox _value = new() { PlaceholderText = "多个值请用英文逗号分隔" };
    private readonly ComboBox _network = new();
    private readonly ComboBox _outbound = new();

    public RoutingRuleForm(RoutingRule rule)
    {
        FluentUi.ConfigureWindow(this, "编辑路由规则", 580, 490, 520, 440);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        _enabled.IsChecked = rule.Enabled;
        _name.Text = rule.Name;
        _type.DisplayMemberPath = nameof(RuleTypeOption.Display);
        _type.SelectedValuePath = nameof(RuleTypeOption.Value);
        _type.ItemsSource = RuleTypes.ToList();
        _type.SelectedValue = rule.Type;
        if (_type.SelectedIndex < 0)
            _type.SelectedIndex = 0;

        _value.Text = rule.Value;
        foreach (var net in new[] { "TCP/UDP", "TCP", "UDP" })
            _network.Items.Add(net);
        _network.SelectedItem = rule.Network;
        if (_network.SelectedIndex < 0)
            _network.SelectedIndex = 0;

        foreach (var ob in new[] { "MQVPN-TUN", "DIRECT", "REJECT" })
            _outbound.Items.Add(ob);
        _outbound.SelectedItem = rule.Outbound;
        if (_outbound.SelectedIndex < 0)
            _outbound.SelectedIndex = 0;

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar("路由分流规则", showMaximize: false, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var stack = new StackPanel { Margin = new Thickness(18, 10, 18, 10) };
        stack.Children.Add(FluentUi.SettingCard("规则状态", _enabled));
        stack.Children.Add(FluentUi.SettingCard("规则名称", _name));
        stack.Children.Add(FluentUi.SettingCard("匹配类型", _type));
        stack.Children.Add(FluentUi.SettingCard("匹配内容", _value, "多个值请用逗号分隔（例如 eurotrucks2.exe,amtrucks.exe）。"));
        stack.Children.Add(FluentUi.SettingCard("传输协议", _network));
        stack.Children.Add(FluentUi.SettingCard("出站动作", _outbound));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stack,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new Border
        {
            Padding = new Thickness(18, 12, 18, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) =>
        {
            DialogResult = false;
            Close();
        });
        cancel.MinWidth = 88;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var save = FluentUi.ActionButton("保存规则", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) => SaveAndClose());
        save.MinWidth = 96;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    public RoutingRule Result { get; private set; } = new();

    public bool ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        return ShowDialog() == true;
    }

    public void Dispose()
    {
        if (IsVisible)
            Close();
    }

    internal static string GetTypeDisplay(string type) =>
        RuleTypes.FirstOrDefault(option => option.Value.Equals(type, StringComparison.OrdinalIgnoreCase))?.Display
        ?? type;

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_value.Text) ||
            _value.Text.Contains('\n'))
        {
            FluentUi.ShowAlert(this, "规则名称和匹配值不能为空。多个匹配值请使用逗号分隔。", "规则无效", isError: true);
            return;
        }
        Result = new RoutingRule
        {
            Enabled = _enabled.IsChecked == true,
            Name = _name.Text.Trim(),
            Type = _type.SelectedValue?.ToString() ?? "PROCESS-NAME",
            Value = _value.Text.Trim(),
            Network = _network.SelectedItem?.ToString() ?? "TCP/UDP",
            Outbound = _outbound.SelectedItem?.ToString() ?? "MQVPN-TUN",
        };
        DialogResult = true;
        Close();
    }

    private sealed record RuleTypeOption(string Value, string Display);
}

internal sealed class TextPromptForm : FluentWindow, IDisposable
{
    private readonly Wpf.Ui.Controls.TextBox _text = new() { PlaceholderText = "请输入名称..." };

    public TextPromptForm(string title, string label)
    {
        FluentUi.ConfigureWindow(this, title, 440, 220, 400, 200);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var root = FluentUi.CreateWindowRootGrid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = FluentUi.CreateTitleBar(title, showMaximize: false, showMinimize: false);
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new StackPanel { Margin = new Thickness(20, 14, 20, 14) };
        var caption = FluentUi.PrimaryText(label, 13, FontWeights.Medium);
        caption.Margin = new Thickness(0, 0, 0, 10);
        body.Children.Add(caption);
        body.Children.Add(_text);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var footer = new Border
        {
            Padding = new Thickness(18, 12, 18, 14),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = FluentUi.ActionButton("取消", SymbolRegular.Dismiss20, ControlAppearance.Secondary, (_, _) =>
        {
            DialogResult = false;
            Close();
        });
        cancel.MinWidth = 88;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        var ok = FluentUi.ActionButton("确定", SymbolRegular.Checkmark20, ControlAppearance.Primary, (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_text.Text))
                return;
            DialogResult = true;
            Close();
        });
        ok.MinWidth = 92;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        footer.Child = buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        Loaded += (_, _) => _text.Focus();
    }

    public string Value => _text.Text.Trim();

    public bool ShowModal(Window? owner)
    {
        if (owner is { IsVisible: true })
            Owner = owner;
        return ShowDialog() == true;
    }

    public void Dispose()
    {
        if (IsVisible)
            Close();
    }
}
