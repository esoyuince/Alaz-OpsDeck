#pragma once
#include "lvgl.h"
#include <stdint.h>
void opsdeck_details_ui_create(lv_obj_t *parent);
void opsdeck_details_ui_clear(void);
void opsdeck_details_ui_refresh(int64_t now);
