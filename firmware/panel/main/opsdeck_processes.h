#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"

#define OPSDECK_PROCESS_ROWS 12

typedef struct {
    int pid;
    float cpu_pct;
    float ram_mib;
    char name[32];
} opsdeck_process_row_t;

typedef struct {
    bool present;
    int total_count;
    int row_count;
    uint32_t sequence;
    int64_t received_us;
    opsdeck_process_row_t rows[OPSDECK_PROCESS_ROWS];
} opsdeck_processes_t;

bool opsdeck_processes_accept(const cJSON *o);
void opsdeck_processes_copy(opsdeck_processes_t *out);
