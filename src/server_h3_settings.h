// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2026 mp0rta and mqvpn contributors

#ifndef MQVPN_SERVER_H3_SETTINGS_H
#define MQVPN_SERVER_H3_SETTINGS_H

#include <xquic/xqc_http3.h>

void mqvpn_server_init_h3_settings(xqc_h3_conn_settings_t *settings);

#endif /* MQVPN_SERVER_H3_SETTINGS_H */
