#pragma once
#include "opsdeck_status.h"
typedef struct {
    bool enabled,summary_present;int total;char name[23];opsdeck_metric_t metrics[5];
    int inventory_state,inventory_age_s,resource_count,complete_sources,worker_count,d1_count,r2_count,pages_count;
    int queue_state,queue_age_s,queue_count,queue_observed;
    int ai_state,ai_age_s,gateway_state,gateway_age_s;
    double ai_requests,ai_input_tokens,ai_output_tokens,gateway_requests,gateway_errors,gateway_cached;
    uint32_t sequence;int64_t received_us;
} opsdeck_cloud_t;
bool opsdeck_cloud_accept(const cJSON *o);
void opsdeck_cloud_copy(int slot,opsdeck_cloud_t *s);