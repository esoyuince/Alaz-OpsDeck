#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>
#include "opsdeck_pc.h"

#define OPSDECK_SD_BUFFER_WRITE_ENABLED 0
#define OPSDECK_SD_BUFFER_INTERVAL_SECONDS 60u
#define OPSDECK_SD_BUFFER_MAX_RECORDS (7u*24u*60u)
#define OPSDECK_SD_RECORD_V1_BYTES 80u
#define OPSDECK_SD_BUFFER_RAW_BYTES (OPSDECK_SD_BUFFER_MAX_RECORDS*OPSDECK_SD_RECORD_V1_BYTES)

uint32_t opsdeck_sd_buffer_crc32(const uint8_t *data,size_t len);
size_t opsdeck_sd_buffer_encode_v1(const opsdeck_pc_t *pc,uint64_t uptime_s,
    uint8_t out[OPSDECK_SD_RECORD_V1_BYTES]);
bool opsdeck_sd_buffer_should_sample(const opsdeck_pc_t *pc,uint32_t last_sequence,
    int64_t now_us,int64_t last_sample_us);
