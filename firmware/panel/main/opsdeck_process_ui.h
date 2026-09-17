#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "lvgl.h"

void opsdeck_process_ui_open(void);
void opsdeck_process_ui_close(void);
void opsdeck_process_ui_refresh(int64_t now);
bool opsdeck_process_ui_is_open(void);
