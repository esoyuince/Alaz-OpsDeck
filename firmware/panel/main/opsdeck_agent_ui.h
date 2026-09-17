#pragma once
#include <stdint.h>
#include "lvgl.h"

void opsdeck_agent_ui_create(lv_obj_t *parent,int x,int y,int w,int h);
void opsdeck_agent_ui_refresh(int64_t now);
void opsdeck_agent_ui_clear(void);
void opsdeck_agent_ui_open_new_task(void);
