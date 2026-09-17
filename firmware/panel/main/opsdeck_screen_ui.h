#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "lvgl.h"
typedef enum {
    OPS_SCREEN_CPU=0,OPS_SCREEN_INTEL=1,OPS_SCREEN_NVIDIA=2,OPS_SCREEN_RAM=3,
    OPS_SCREEN_SHARED=4,OPS_SCREEN_VRAM=5,OPS_SCREEN_THERMALS=6,OPS_SCREEN_FANS=7,
    OPS_SCREEN_STORAGE=8,OPS_SCREEN_NETWORK=9,OPS_SCREEN_HISTORY=10,
    OPS_SCREEN_ALERTS=11,OPS_SCREEN_TIMELINE=12,OPS_SCREEN_DEVICE=13,
    OPS_SCREEN_SD=14,OPS_SCREEN_LINK=15,OPS_SCREEN_WIFI=16
} opsdeck_screen_mode_t;
void opsdeck_screen_ui_open(opsdeck_screen_mode_t mode);
void opsdeck_screen_ui_close(void);
void opsdeck_screen_ui_refresh(int64_t now);
bool opsdeck_screen_ui_is_open(void);
