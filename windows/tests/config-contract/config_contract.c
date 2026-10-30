// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

/* Parse-only CLI/bridge observer, never starts a VPN or resolves a host.
 * The GUI tests compare the values, not just a tolerant parser's exit code. */
#include "config.h"
#include "json_util.h"
#include "mqvpn_internal.h"
#include "mqvpn_sched_names.h"
#include "redundant_tx.h"
#include "vpn_client.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static void
report_reorder(yyjson_mut_doc *d, yyjson_mut_val *o, const mqvpn_reorder_config_t *r)
{
#define NUM(key, field) yyjson_mut_obj_add_uint(d, o, key, r->field)
    yyjson_mut_obj_add_bool(d, o, "enabled", r->mode == MQVPN_REORDER_ON);
    NUM("max_wait_ms", max_wait_ms);
    NUM("cap_packets", cap_packets_per_flow);
    NUM("max_bytes_per_flow", max_buffer_bytes_per_flow);
    NUM("classify_window", classify_window);
    NUM("ack_demote_max_large", ack_demote_max_large_packets);
    NUM("small_packet_threshold", small_packet_threshold_bytes);
    NUM("reset_mark_packets", reset_mark_packets);
    NUM("reset_idle_grace_ms", reset_idle_grace_ms);
    NUM("max_flows", max_flows);
    NUM("global_max_bytes", global_max_buffer_bytes);
    NUM("ingress_idle_sec", ingress_idle_timeout_sec);
    NUM("egress_idle_sec", egress_idle_timeout_sec);
    yyjson_mut_obj_add_bool(d, o, "explicit_wait", r->has_explicit_wait != 0);
    yyjson_mut_obj_add_bool(d, o, "explicit_cap", r->has_explicit_cap != 0);
#undef NUM
}

static void
report_hybrid(yyjson_mut_doc *d, yyjson_mut_val *o, const mqvpn_hybrid_config_t *h)
{
    const char *modes[] = {"stream", "raw", "auto"};
    yyjson_mut_obj_add_bool(d, o, "enabled", h->enabled != 0);
    yyjson_mut_obj_add_strcpy(d, o, "tcp", modes[h->tcp_mode]);
    yyjson_mut_obj_add_uint(d, o, "tcp_max_flows", h->tcp_max_flows);
    yyjson_mut_obj_add_uint(d, o, "tcp_idle_timeout_sec", h->tcp_idle_timeout_sec);
}

static void
report_rules(yyjson_mut_doc *d, yyjson_mut_val *o, const mqvpn_reorder_config_t *r)
{
    yyjson_mut_val *a = yyjson_mut_arr(d);
    yyjson_mut_obj_add_val(d, o, "reorder_rules", a);
    for (int i = 0; i < r->n_rules; i++) {
        const mqvpn_reorder_rule_t *v = &r->rules[i];
        yyjson_mut_val *row = yyjson_mut_obj(d);
        yyjson_mut_arr_add_val(a, row);
        yyjson_mut_obj_add_uint(d, row, "proto", v->proto);
        yyjson_mut_obj_add_uint(d, row, "port", v->port);
        yyjson_mut_obj_add_uint(d, row, "profile", v->profile);
        yyjson_mut_obj_add_uint(d, row, "max_wait_ms", v->explicit_wait_ms);
        yyjson_mut_obj_add_uint(d, row, "cap_packets", v->explicit_cap);
    }
}

int
main(int argc, char **argv)
{
    if (argc != 2) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 3;
    char *text = calloc(1, 2 * 1024 * 1024 + 1);
    if (!text) {
        fclose(file);
        return 4;
    }
    size_t len = fread(text, 1, 2 * 1024 * 1024, file);
    int read_error = ferror(file) || !feof(file);
    fclose(file);
    if (read_error || len == 0) {
        free(text);
        return 5;
    }
    mqvpn_file_config_t f;
    mqvpn_config_defaults(&f);
    int rc = mqvpn_config_load_json_filecfg(&f, text);
    free(text);
    if (rc < 0 || f.is_server || mqvpn_reorder_config_validate(&f.reorder) < 0 ||
        mqvpn_hybrid_config_validate(&f.hybrid) < 0)
        return 6;

    char host[256];
    int port;
    if (mqvpn_parse_host_port(f.server_addr, host, sizeof(host), &port) < 0) return 7;
    mqvpn_client_cfg_t c = {0};
    c.server_addr = host;
    c.server_port = port;
    c.tls_server_name = f.tls_server_name[0] ? f.tls_server_name : NULL;
    c.auth_key = f.auth_key;
    c.insecure = f.insecure;
    c.n_paths = f.n_paths;
    c.reconnect = f.reconnect;
    c.reconnect_interval = f.reconnect_interval;
    c.kill_switch = f.kill_switch;
    c.manage_routes = f.manage_routes;
    c.scheduler = mqvpn_sched_from_name(f.scheduler);
    c.request_server_scheduler = f.request_server_scheduler;
    c.cc = mqvpn_cc_from_name(f.cc);
    c.reinjection = mqvpn_reinj_from_name(f.reinjection);
    c.reinj_srtt_factor_pct = f.reinjection_srtt_factor_pct;
    c.reinj_hard_deadline_ms = f.reinjection_hard_deadline_ms;
    c.reinj_deadline_lower_bound_ms = f.reinjection_deadline_lower_bound_ms;
    c.redundant = f.redundant;
    if (f.redundant) {
        c.n_redundant_ports = mqvpn_redundant_parse_ports(
            f.redundant_ports, c.redundant_ports, MQVPN_REDUNDANT_MAX_PORTS);
        if (c.n_redundant_ports < 0) return 8;
    }
    c.init_max_path_id = f.init_max_path_id;
    c.tun_mtu = f.tun_mtu;
    c.path_mtu = f.path_mtu;
    c.pmtud = f.pmtud;
    c.reorder = f.reorder;
    c.hybrid = f.hybrid;
    c.recv_rate_limit = f.recv_rate_limit;
    /* Same log enum conversion as the CLI (not the GUI's global override). */
    c.log_level = strcmp(f.log_level, "debug") == 0   ? MQVPN_LOG_DEBUG
                  : strcmp(f.log_level, "warn") == 0  ? MQVPN_LOG_WARN
                  : strcmp(f.log_level, "error") == 0 ? MQVPN_LOG_ERROR
                                                      : MQVPN_LOG_INFO;
    mqvpn_config_t *lib = mqvpn_config_new();
    if (!lib) return 9;
    mqvpn_platform_apply_client_config(lib, &c);
    yyjson_mut_doc *d = yyjson_mut_doc_new(NULL);
    yyjson_mut_val *root = yyjson_mut_obj(d), *o = yyjson_mut_obj(d);
    yyjson_mut_doc_set_root(d, root);
    yyjson_mut_obj_add_val(d, root, "parser", o);
#define STR(key, field)  yyjson_mut_obj_add_strcpy(d, o, key, f.field)
#define NUM(key, field)  yyjson_mut_obj_add_uint(d, o, key, (uint64_t)f.field)
#define BOOL(key, field) yyjson_mut_obj_add_bool(d, o, key, f.field != 0)
    yyjson_mut_obj_add_str(d, o, "mode", "client");
    STR("server_addr", server_addr);
    STR("tls_server_name", tls_server_name);
    STR("auth_key", auth_key);
    STR("tun_name", tun_name);
    STR("log_level", log_level);
    STR("scheduler", scheduler);
    STR("cc", cc);
    STR("reinjection", reinjection);
    STR("redundant_ports", redundant_ports);
    BOOL("insecure", insecure);
    BOOL("manage_routes", manage_routes);
    BOOL("kill_switch", kill_switch);
    BOOL("reconnect", reconnect);
    BOOL("request_server_scheduler", request_server_scheduler);
    BOOL("redundant", redundant);
    BOOL("pmtud", pmtud);
    NUM("reconnect_interval", reconnect_interval);
    NUM("init_max_path_id", init_max_path_id);
    NUM("mtu", tun_mtu);
    NUM("path_mtu", path_mtu);
    NUM("reinjection_srtt_factor_pct", reinjection_srtt_factor_pct);
    NUM("reinjection_hard_deadline_ms", reinjection_hard_deadline_ms);
    NUM("reinjection_deadline_lower_bound_ms", reinjection_deadline_lower_bound_ms);
    yyjson_mut_val *a = yyjson_mut_arr(d);
    yyjson_mut_obj_add_val(d, o, "paths", a);
    for (int i = 0; i < f.n_paths; i++)
        yyjson_mut_arr_add_strcpy(d, a, f.paths[i]);
    a = yyjson_mut_arr(d);
    yyjson_mut_obj_add_val(d, o, "dns", a);
    for (int i = 0; i < f.n_dns; i++)
        yyjson_mut_arr_add_strcpy(d, a, f.dns_servers[i]);
    yyjson_mut_val *r = yyjson_mut_obj(d), *h = yyjson_mut_obj(d),
                   *adv = yyjson_mut_obj(d);
    yyjson_mut_obj_add_val(d, o, "reorder", r);
    yyjson_mut_obj_add_val(d, o, "hybrid", h);
    yyjson_mut_obj_add_val(d, o, "advanced", adv);
    report_reorder(d, r, &f.reorder);
    report_hybrid(d, h, &f.hybrid);
    yyjson_mut_obj_add_uint(d, adv, "recv_rate_limit", f.recv_rate_limit);
    report_rules(d, o, &f.reorder);
#undef STR
#undef NUM
#undef BOOL
    o = yyjson_mut_obj(d);
    yyjson_mut_obj_add_val(d, root, "bridge", o);
#define STR(key, field)  yyjson_mut_obj_add_strcpy(d, o, key, lib->field)
#define NUM(key, field)  yyjson_mut_obj_add_uint(d, o, key, (uint64_t)lib->field)
#define BOOL(key, field) yyjson_mut_obj_add_bool(d, o, key, lib->field != 0)
    STR("server_host", server_host);
    NUM("server_port", server_port);
    STR("tls_server_name", tls_server_name);
    STR("auth_key", auth_key);
    BOOL("insecure", insecure);
    BOOL("multipath", multipath);
    BOOL("kill_switch", killswitch_hint);
    BOOL("reconnect", reconnect_enable);
    NUM("reconnect_interval", reconnect_interval_sec);
    BOOL("request_server_scheduler", request_server_scheduler);
    BOOL("redundant", redundant);
    BOOL("pmtud", pmtud);
    NUM("init_max_path_id", init_max_path_id);
    NUM("mtu", tun_mtu);
    NUM("path_mtu", path_mtu);
    NUM("reinjection_srtt_factor_pct", reinj_srtt_factor_pct);
    NUM("reinjection_hard_deadline_ms", reinj_hard_deadline_ms);
    NUM("reinjection_deadline_lower_bound_ms", reinj_deadline_lower_bound_ms);
    yyjson_mut_obj_add_str(d, o, "scheduler", mqvpn_sched_to_name(lib->scheduler));
    yyjson_mut_obj_add_str(d, o, "cc", mqvpn_cc_to_name(lib->cc));
    yyjson_mut_obj_add_str(d, o, "reinjection", mqvpn_reinj_to_name(lib->reinjection));
    const char *levels[] = {"debug", "info", "warn", "error"};
    yyjson_mut_obj_add_str(d, o, "log_level", levels[lib->log_level]);
    a = yyjson_mut_arr(d);
    yyjson_mut_obj_add_val(d, o, "redundant_ports", a);
    for (int i = 0; i < lib->n_redundant_ports; i++)
        yyjson_mut_arr_add_uint(d, a, lib->redundant_ports[i]);
    r = yyjson_mut_obj(d);
    h = yyjson_mut_obj(d);
    adv = yyjson_mut_obj(d);
    yyjson_mut_obj_add_val(d, o, "reorder", r);
    yyjson_mut_obj_add_val(d, o, "hybrid", h);
    yyjson_mut_obj_add_val(d, o, "advanced", adv);
    report_reorder(d, r, &lib->reorder);
    report_hybrid(d, h, &lib->hybrid);
    yyjson_mut_obj_add_uint(d, adv, "recv_rate_limit", lib->recv_rate_limit);
    report_rules(d, o, &lib->reorder);
#undef STR
#undef NUM
#undef BOOL
    char *output = yyjson_mut_write(d, YYJSON_WRITE_PRETTY, NULL);
    if (!output)
        rc = 10;
    else {
        puts(output);
        free(output);
        rc = 0;
    }
    yyjson_mut_doc_free(d);
    mqvpn_config_free(lib);
    return rc;
}
