#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "esp_err.h"
#include "lvgl.h"
typedef struct {
    lv_display_t *display;
    bool touch_ok;
    bool backlight_ok;
    uint8_t touch_address;
} opsdeck_board_t;
esp_err_t opsdeck_board_init(opsdeck_board_t *board);
