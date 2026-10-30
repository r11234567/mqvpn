// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

namespace TruckersMP.Proxy;

internal static class RoutingSemantics
{
    // Display names are user-editable and cannot select a network mode.
    public static bool IsGlobalMqvpn(RoutingPolicy policy) =>
        policy.FinalOutbound == "MQVPN-TUN" && !policy.Rules.Any(rule => rule.Enabled);
}
