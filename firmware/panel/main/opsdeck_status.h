#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"
typedef struct {int state,age_s,covered,expected; double value,secondary; bool has_value,has_secondary; char unit[17],note[41],period_start[11],period_end[11],source_end[11];} opsdeck_metric_t;
typedef struct {int codex_count,codex_state,bridge_state;int rdc_state,rdc_process_count,rdc_total_calls,rdc_sessions,rdc_success_permille,rdc_last_action_age_s;int managed_codex_state,managed_codex_owned;bool locked,task_present,link_present,link_recovering,codex_usage_present,rdc_present,managed_codex_present,managed_codex_running,managed_codex_runtime,managed_codex_reconcile;int codex_usage_state,codex_usage_age_s,codex_quota_used,codex_quota_remaining,codex_quota_window_m,codex_spark_used,codex_spark_remaining,codex_spark_window_m;char codex_quota_reset[13],codex_spark_reset[13];int task_state,task_open,task_claimed,task_in_progress,task_needs_approval,task_blocked,task_expired_claims,task_latest_age_s;char task_latest_event[17];int link_state,link_reopen_count,link_rom_probe_count,link_recovered_count,link_last_action,link_last_reason,link_host_uptime_s,link_forward_age_s,link_recovery_age_s;opsdeck_metric_t metrics[5];uint32_t sequence;int64_t received_us;} opsdeck_status_t;
bool opsdeck_status_accept(const cJSON *o);
void opsdeck_status_copy(opsdeck_status_t *s);

bool opsdeck_metric_parse(const cJSON *o,opsdeck_metric_t *m);

bool opsdeck_cost_valid(const opsdeck_metric_t *m);
