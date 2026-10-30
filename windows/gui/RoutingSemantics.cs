// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

namespace TruckersMP.Proxy;

internal static class RoutingSemantics
{
    // Display names are user-editable and cannot select a network mode.
    public static bool IsGlobalMqvpn(RoutingPolicy policy) =>
        policy.FinalOutbound == "MQVPN-TUN" && !policy.Rules.Any(rule => rule.Enabled);

    public static void ValidateCoreRouteManagement(System.Text.Json.Nodes.JsonObject config, RoutingPolicy policy)
    {
        if (!IsGlobalMqvpn(policy) && MqvpnProfileConfiguration.Boolean(config, "manage_routes"))
            throw new InvalidDataException("manage_routes 接管系统路由，不能与 GUI 分流策略同时使用。请明确关闭核心路由管理，或选择全局 MQVPN；GUI 不会自动覆盖此开关。");
    }
}
