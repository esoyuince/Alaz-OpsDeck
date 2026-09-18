#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"
typedef struct {
    char id[3];int state,age_s;bool has_value;
    double total_gib,free_gib,available_gib,used_pct;
} opsdeck_volume_t;
typedef struct {
    float cpu,gpu,ram_pct,vram_pct,gpu_temp,ram_used_gib,ram_total_gib;
    float vram_used_gib,vram_total_gib,rx_mbps,tx_mbps;
    float intel_gpu,intel_temp,intel_shared_gib,intel_shared_limit_gib,fan1_rpm,fan2_rpm;
    float cpu_temp,chassis_temp;
    int cpu_sensor,chassis_sensor,fan_sensor,fan_control_supported,fan_manual_supported,fan_control_mode,fan_control_busy,volume_count,shown_volumes;
    opsdeck_volume_t volumes[2];
    uint32_t valid,sequence;
    int64_t received_us;
} opsdeck_pc_t;
#define PC_CPU (1u<<0)
#define PC_GPU (1u<<1)
#define PC_RAM (1u<<2)
#define PC_VRAM (1u<<3)
#define PC_GPU_TEMP (1u<<4)
#define PC_NETWORK (1u<<5)
#define PC_INTEL (1u<<6)
#define PC_INTEL_TEMP (1u<<7)
#define PC_INTEL_SHARED (1u<<8)
#define PC_FAN1 (1u<<9)
#define PC_FAN2 (1u<<10)

#define PC_CPU_TEMP (1u<<11)
#define PC_CHASSIS_TEMP (1u<<12)
#define PC_FAN_CONTROL (1u<<13)
bool opsdeck_pc_decode(const cJSON *o,opsdeck_pc_t *s);
