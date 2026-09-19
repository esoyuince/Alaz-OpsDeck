#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include "esp_err.h"

typedef bool (*opsdeck_tailscale_frame_handler_t)(const char *line);

typedef enum {
    OPSDECK_TS_DISABLED=0,
    OPSDECK_TS_IDLE=1,
    OPSDECK_TS_CONNECTING=2,
    OPSDECK_TS_CONNECTED=3,
    OPSDECK_TS_ERROR=4,
} opsdeck_tailscale_state_t;

typedef struct {
    bool compiled;
    bool configured;
    bool connected;
    bool direct_path;
    bool telemetry_authenticated;
    bool telemetry_active;
    bool enrollment_key_in_ram;
    opsdeck_tailscale_state_t state;
    int ml_state;
    int last_error;
    uint16_t host_port;
    uint32_t telemetry_frames;
    uint32_t sequence;
    char vpn_ip[16];
    char host_ip[16];
} opsdeck_tailscale_status_t;

void opsdeck_tailscale_set_frame_handler(opsdeck_tailscale_frame_handler_t handler);
esp_err_t opsdeck_tailscale_init(void);
void opsdeck_tailscale_copy(opsdeck_tailscale_status_t *out);
bool opsdeck_tailscale_accept_provision(const char *line,char *ack,size_t ack_n);
