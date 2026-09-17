#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "esp_err.h"

typedef enum {
    OPSDECK_SD_SETUP=0,
    OPSDECK_SD_READY=1,
    OPSDECK_SD_UNAVAILABLE=2,
    OPSDECK_SD_ERROR=3,
} opsdeck_sd_state_t;

typedef struct {
    opsdeck_sd_state_t state;
    bool mounted;
    uint64_t total_bytes;
    uint64_t free_bytes;
    esp_err_t last_error;
} opsdeck_sd_info_t;

esp_err_t opsdeck_sd_init(void);
void opsdeck_sd_copy(opsdeck_sd_info_t *out);
