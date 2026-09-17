#pragma once
#include "opsdeck_status.h"
typedef struct { bool enabled; int total; char name[23]; opsdeck_metric_t metrics[5]; uint32_t sequence; int64_t received_us; } opsdeck_cloud_t;
bool opsdeck_cloud_accept(const cJSON *o);
void opsdeck_cloud_copy(int slot,opsdeck_cloud_t *s);