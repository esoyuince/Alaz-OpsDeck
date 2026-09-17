#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"
#define OPSDECK_OPSVIEW_POINTS 48
#define OPSDECK_OPSVIEW_ALERTS 6
#define OPSDECK_OPSVIEW_EVENTS 7
typedef struct {bool a_valid,b_valid,c_valid;float a,b,c;} opsdeck_ops_point_t;
typedef struct {int level;char code[33],source[29],text[105];} opsdeck_ops_alert_t;
typedef struct {int age_s,severity,domain;char code[33],summary[113];} opsdeck_ops_event_t;
typedef struct {int kind,window,metric,variant,request_id;} opsdeck_opsview_query_t;
typedef struct {
    bool present;int kind,request_id,window,metric,variant,volume_count,state,level,count,minute_rows,sample_rows,expected_minutes,point_count,coverage_state,coverage_pct,covered_buckets,bucket_count,last_age_s;
    char label[25],unit[13],label2[25],unit2[13],label3[25],unit3[13],first_local[17],last_local[17];
    bool min_valid,avg_valid,max_valid,min2_valid,avg2_valid,max2_valid,min3_valid,avg3_valid,max3_valid;
    double min,avg,max,min2,avg2,max2,min3,avg3,max3;
    opsdeck_ops_point_t points[OPSDECK_OPSVIEW_POINTS];
    opsdeck_ops_alert_t alerts[OPSDECK_OPSVIEW_ALERTS];
    opsdeck_ops_event_t events[OPSDECK_OPSVIEW_EVENTS];
    int64_t received_us;uint32_t sequence;
} opsdeck_opsview_t;
bool opsdeck_opsview_accept(const cJSON *o);
void opsdeck_opsview_copy(opsdeck_opsview_t *s,opsdeck_opsview_query_t *q);
void opsdeck_opsview_select(int kind,int window,int metric,int variant);
void opsdeck_opsview_request_current(void);
void opsdeck_opsview_deactivate(void);
