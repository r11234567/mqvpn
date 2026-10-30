using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace TruckersMP.Proxy;

internal sealed class ReorderRuleRow
{
    public string Port { get; set; } = string.Empty;
    public string Profile { get; set; } = "cellular_bond";
    public string MaxWaitMs { get; set; } = string.Empty;
    public string CapPackets { get; set; } = string.Empty;
    public JsonObject? OriginalTag { get; set; }
}

internal sealed partial class ProfileEditorForm
{
    private static readonly string[] ReorderProfiles =
        MqvpnProfileConfiguration.ReorderProfiles;

    private readonly ObservableCollection<ReorderRuleRow> _reorderRuleRows = [];

    public ObservableCollection<ReorderRuleRow> ReorderRuleRows => _reorderRuleRows;

    private void InitializeReorderRules(JsonObject reorder)
    {
        _reorderEnabled.Content = "启用 UDP Reorder";
        _reorderEnabled.IsChecked = GetBoolean(reorder, "enabled", false);

        FluentUi.StyleDataGrid(_reorderRules);
        _reorderRules.AutoGenerateColumns = false;
        _reorderRules.CanUserAddRows = false;
        _reorderRules.CanUserDeleteRows = true;
        _reorderRules.HeadersVisibility = DataGridHeadersVisibility.Column;
        _reorderRules.SelectionMode = DataGridSelectionMode.Single;
        _reorderRules.Height = 190;
        _reorderRules.Columns.Clear();

        _reorderRules.Columns.Add(new DataGridTextColumn
        {
            Header = "UDP 端口",
            Binding = new Binding(nameof(ReorderRuleRow.Port)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        _reorderRules.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "预设",
            ItemsSource = ReorderProfiles,
            SelectedItemBinding = new Binding(nameof(ReorderRuleRow.Profile)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
        });
        _reorderRules.Columns.Add(new DataGridTextColumn
        {
            Header = "等待覆盖 ms",
            Binding = new Binding(nameof(ReorderRuleRow.MaxWaitMs)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
        });
        _reorderRules.Columns.Add(new DataGridTextColumn
        {
            Header = "包数覆盖",
            Binding = new Binding(nameof(ReorderRuleRow.CapPackets)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
        });

        _reorderRuleRows.Clear();
        foreach (var rule in (_config["reorder_rules"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var port = GetInteger(rule, "port", 0);
            _reorderRuleRows.Add(new ReorderRuleRow
            {
                Port = port.ToString(),
                Profile = GetString(rule, "profile", "quic_bulk"),
                MaxWaitMs = GetOptionalUnsignedText(rule, "max_wait_ms"),
                CapPackets = GetOptionalUnsignedText(rule, "cap_packets"),
                OriginalTag = rule.DeepClone().AsObject(),
            });
        }
        _reorderRules.ItemsSource = _reorderRuleRows;

        _reorderCustomWait.Content = "覆盖预设的全局最大等待";
        _reorderCustomWait.IsChecked = reorder.ContainsKey("max_wait_ms");
        _reorderCustomCap.Content = "覆盖预设的全局缓存包数";
        _reorderCustomCap.IsChecked = reorder.ContainsKey("cap_packets");

        _reorderEnabled.Checked += (_, _) => UpdateDependentControls();
        _reorderEnabled.Unchecked += (_, _) => UpdateDependentControls();
        _reorderCustomWait.Checked += (_, _) => UpdateDependentControls();
        _reorderCustomWait.Unchecked += (_, _) => UpdateDependentControls();
        _reorderCustomCap.Checked += (_, _) => UpdateDependentControls();
        _reorderCustomCap.Unchecked += (_, _) => UpdateDependentControls();
    }

    private TabItem BuildReorderPage()
    {
        var page = CreatePage("UDP 重排", out var stack);
        stack.Children.Add(FluentUi.SectionHeader("UDP Reorder 策略与规则", SymbolRegular.Server20));
        stack.Children.Add(FluentUi.SettingCard("UDP Reorder 状态", _reorderEnabled,
            "有规则时只处理匹配流量；零规则是核心支持的全流量匹配模式（包括内层 TCP）。会引入等待，默认关闭；建议显式限定 UDP 端口。"));

        var rulesContainer = new StackPanel();
        var ruleToolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var addRule = FluentUi.ActionButton("添加端口规则", SymbolRegular.Add20, ControlAppearance.Primary, (_, _) =>
        {
            var nextPort = 443;
            while (_reorderRuleRows.Any(r => r.Port == nextPort.ToString()))
                nextPort++;
            var added = new ReorderRuleRow
            {
                Port = nextPort.ToString(),
                Profile = "cellular_bond",
            };
            _reorderRuleRows.Add(added);
            _reorderRules.SelectedItem = added;
        });
        addRule.Margin = new Thickness(0, 0, 8, 0);

        var deleteRule = FluentUi.ActionButton("删除选中规则", SymbolRegular.Delete20, ControlAppearance.Secondary, (_, _) =>
        {
            if (_reorderRules.SelectedItem is ReorderRuleRow row)
                _reorderRuleRows.Remove(row);
        });

        ruleToolbar.Children.Add(addRule);
        ruleToolbar.Children.Add(deleteRule);
        rulesContainer.Children.Add(ruleToolbar);
        rulesContainer.Children.Add(_reorderRules);

        stack.Children.Add(FluentUi.SettingCard("端口规则列表", rulesContainer,
            "每行可独立选择预设（双击单元格编辑）。等待与包数覆盖留空时使用全局显式值、预设值或内置默认值。", stackEditorBelow: true));

        stack.Children.Add(FluentUi.SectionHeader("全局预设覆盖", SymbolRegular.Settings20));
        stack.Children.Add(FluentUi.SettingCard("等待覆盖开关", _reorderCustomWait, "关闭时从 JSON 删除 max_wait_ms，让各规则预设生效。"));
        stack.Children.Add(FluentUi.SettingCard("最大等待（ms）", _reorderMaxWait, "0 及以上，默认 30。"));
        stack.Children.Add(FluentUi.SettingCard("包数覆盖开关", _reorderCustomCap, "关闭时从 JSON 删除 cap_packets，让各规则预设生效。"));
        stack.Children.Add(FluentUi.SettingCard("每流缓存包数", _reorderCapPackets, "必须是非零的 2 的幂，默认 1024。"));

        stack.Children.Add(FluentUi.SectionHeader("全局限制与分类", SymbolRegular.TopSpeed20));
        stack.Children.Add(FluentUi.SettingCard("每流缓存字节", _reorderMaxBytesPerFlow));
        stack.Children.Add(FluentUi.SettingCard("分类窗口包数", _reorderClassifyWindow, "0 关闭 ACK 方向降级。"));
        stack.Children.Add(FluentUi.SettingCard("ACK 大包阈值", _reorderAckDemoteMaxLarge));
        stack.Children.Add(FluentUi.SettingCard("小包字节阈值", _reorderSmallPacketThreshold));
        stack.Children.Add(FluentUi.SettingCard("重置标记包数", _reorderResetMarkPackets));
        stack.Children.Add(FluentUi.SettingCard("重置空闲宽限（ms）", _reorderResetIdleGrace));
        stack.Children.Add(FluentUi.SettingCard("最大流数", _reorderMaxFlows));
        stack.Children.Add(FluentUi.SettingCard("全局缓存字节", _reorderGlobalMaxBytes));
        stack.Children.Add(FluentUi.SettingCard("接收流空闲回收（秒）", _reorderIngressIdle));
        stack.Children.Add(FluentUi.SettingCard("发送流空闲回收（秒）", _reorderEgressIdle, "必须大于接收流空闲回收时间。"));
        return page;
    }

    private bool TryReadReorderRules(out JsonArray rules, out string error)
    {
        rules = new JsonArray();
        error = string.Empty;
        if (!_reorderRules.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !_reorderRules.CommitEdit(DataGridEditingUnit.Row, true))
        {
            error = "请先完成或取消当前规则单元格的编辑。";
            return false;
        }
        var ports = new HashSet<int>();
        foreach (var row in _reorderRuleRows)
        {
            var portText = row.Port?.Trim();
            var profile = row.Profile?.Trim();
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                error = "UDP 重排规则的端口必须是 1-65535 的整数。";
                return false;
            }
            if (!ports.Add(port))
            {
                error = $"UDP 重排端口 {port} 重复。";
                return false;
            }
            if (profile is null || !ReorderProfiles.Contains(profile, StringComparer.Ordinal))
            {
                error = $"UDP 重排端口 {port} 未选择有效预设。";
                return false;
            }
            if (!TryParseOptionalUInt(row.MaxWaitMs, out var maxWait))
            {
                error = $"UDP 重排端口 {port} 的等待覆盖必须留空或填写大于 0 的整数。";
                return false;
            }
            if (!TryParseOptionalUInt(row.CapPackets, out var capPackets) ||
                (capPackets.HasValue && !IsPowerOfTwo(capPackets.Value)))
            {
                error = $"UDP 重排端口 {port} 的包数覆盖必须留空或填写非零的 2 的幂。";
                return false;
            }
            var rule = row.OriginalTag?.DeepClone().AsObject() ?? new JsonObject();
            if (rule.ContainsKey("proto") && GetString(rule, "proto") != "udp")
            {
                error = "规则 proto 必须是 udp；不支持的导入规则请删除后重新添加。";
                return false;
            }
            if (row.OriginalTag is null) rule["proto"] = "udp";
            if (row.OriginalTag is null || GetInteger(rule, "port", 0) != port) rule["port"] = port;
            if (row.OriginalTag is null || GetString(rule, "profile", "quic_bulk") != profile) rule["profile"] = profile;
            if ((row.MaxWaitMs?.Trim() ?? "") != GetOptionalUnsignedText(rule, "max_wait_ms"))
            {
                if (maxWait.HasValue) rule["max_wait_ms"] = maxWait.Value;
                else rule.Remove("max_wait_ms");
            }
            if ((row.CapPackets?.Trim() ?? "") != GetOptionalUnsignedText(rule, "cap_packets"))
            {
                if (capPackets.HasValue) rule["cap_packets"] = capPackets.Value;
                else rule.Remove("cap_packets");
            }
            rules.Add(rule);
        }
        if (rules.Count > 16)
        {
            error = "UDP Reorder 最多支持 16 条端口规则。";
            return false;
        }
        return true;
    }

    private static string GetOptionalUnsignedText(JsonObject source, string name) =>
        source[name]?.ToJsonString() ?? string.Empty;

    private static bool TryParseOptionalUInt(object? value, out uint? result)
    {
        var text = value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            result = null;
            return true;
        }
        if (uint.TryParse(text, out var parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }
        result = null;
        return false;
    }
}
