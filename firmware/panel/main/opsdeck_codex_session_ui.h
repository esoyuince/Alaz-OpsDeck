#pragma once
#include <stdint.h>
#include "lvgl.h"

void opsdeck_codex_session_ui_open(void);
void opsdeck_codex_session_ui_close(void);
void opsdeck_codex_session_ui_refresh(int64_t now);
bool opsdeck_codex_session_ui_is_open(void);
