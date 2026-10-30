// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

using System.Globalization;

namespace TruckersMP.Proxy;

// Text stays intact until validation. NumberBox uses double, which cannot
// represent the uint64 configuration fields (including 2^53 + 1) exactly.
internal sealed class IntegerInput : Wpf.Ui.Controls.TextBox
{
    public IntegerInput() => SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.TextBox));

    public decimal Minimum { get; set; }
    public decimal Maximum { get; set; }
    public decimal? Value
    {
        get => TryRead(out var value) ? value : null;
        set => Text = value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public bool TryRead(out decimal value)
    {
        var text = Text.Trim();
        value = 0;
        return text.Length > 0 && text.All(c => c is >= '0' and <= '9') &&
            decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= Minimum && value <= Maximum;
    }
}
