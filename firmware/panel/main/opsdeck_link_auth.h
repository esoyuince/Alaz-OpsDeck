#pragma once
#include <stdbool.h>
#include <stddef.h>
#include "esp_err.h"

esp_err_t opsdeck_link_auth_init(void);
bool opsdeck_link_auth_paired(void);
bool opsdeck_link_auth_tls_ready(void);
const char *opsdeck_link_auth_pair_id(void);
const char *opsdeck_link_auth_device_id(void);
const char *opsdeck_link_auth_tls_fingerprint(void);
const unsigned char *opsdeck_link_auth_tls_cert(size_t *out_n);
bool opsdeck_link_auth_accept_pairing(const char *line,char *ack,size_t ack_n);
bool opsdeck_link_auth_build_response(const char *challenge,char *out,size_t out_n);
bool opsdeck_link_auth_verify_server(const char *challenge,const char *reply,char *nonce_out,size_t nonce_n);
bool opsdeck_link_auth_verify_frame(const char *nonce,uint32_t sequence,const char *frame,const char *mac_hex);
bool opsdeck_link_auth_verify_standby(const char *nonce,uint32_t sequence,const char *mac_hex);
