#pragma once
#include <stdint.h>
#include "lvgl.h"
void opsdeck_inventory_ui_create(lv_obj_t *parent);
void opsdeck_inventory_ui_refresh(int64_t now);
void opsdeck_inventory_ui_clear(void);
