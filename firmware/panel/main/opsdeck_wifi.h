#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

#define OPSDECK_WIFI_SCAN_MAX 5

typedef enum {
    OPSDECK_WIFI_DISABLED=0,
    OPSDECK_WIFI_NO_CONFIG=1,
    OPSDECK_WIFI_CONNECTING=2,
    OPSDECK_WIFI_CONNECTED=3,
    OPSDECK_WIFI_ERROR=4,
} opsdeck_wifi_state_t;

typedef struct {
    char ssid[33];
    int rssi;
    int authmode;
    bool secure;
} opsdeck_wifi_ap_t;

typedef bool (*opsdeck_wifi_frame_handler_t)(const char *line);

typedef struct {
    opsdeck_wifi_state_t state;
    bool configured;
    bool radio_active;
    bool connected;
    bool scanning;
    char ssid[33];
    char ip[16];
    int rssi;
    int retry_count;
    int last_disconnect_reason;
    int scan_count;
    opsdeck_wifi_ap_t scan[OPSDECK_WIFI_SCAN_MAX];
    char host_name[64];
    char host_ip[16];
    uint16_t host_port;
    int64_t host_seen_us;
    bool telemetry_authenticated;
    bool telemetry_active;
    uint32_t telemetry_frames;
    int64_t telemetry_seen_us;
    uint32_t sequence;
} opsdeck_wifi_status_t;

esp_err_t opsdeck_wifi_init(void);
void opsdeck_wifi_copy(opsdeck_wifi_status_t *out);
uint32_t opsdeck_wifi_stack_min(void);
void opsdeck_wifi_set_frame_handler(opsdeck_wifi_frame_handler_t handler);
esp_err_t opsdeck_wifi_scan(void);
esp_err_t opsdeck_wifi_set_credentials(const char *ssid,const char *password);
esp_err_t opsdeck_wifi_connect(void);
esp_err_t opsdeck_wifi_forget(void);
