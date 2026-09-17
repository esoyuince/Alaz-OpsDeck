#pragma once
#include "opsdeck_board.h"
#include "opsdeck_pc.h"
void opsdeck_ui_init(const opsdeck_board_t *board);
void opsdeck_pc_publish(const opsdeck_pc_t *sample);
void opsdeck_pc_copy(opsdeck_pc_t *sample);
