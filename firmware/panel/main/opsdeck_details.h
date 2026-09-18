#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"
#define OPSDECK_DETAILS_ROWS 4
#define OPSDECK_DETAILS_KINDS 6
#define OPSDECK_DETAILS_TTL 180
#define OPSDECK_DETAILS_TRANSPORT_TTL 16
typedef struct {int slot,kind,page,request_id;char group[17],project[49];} opsdeck_details_query_t;
typedef struct {char label[29],unit[13];bool valid;double value;} opsdeck_detail_metric_t;
typedef struct {
    bool present,test;int slot,kind,requested_page,page,total_pages,request_id,state,age_s,row_count;
    char generation[9],key[17],group[17],account_name[23],title[49],scope[81],note[81],reason[81];
    opsdeck_detail_metric_t rows[OPSDECK_DETAILS_ROWS];int64_t received_us;uint32_t sequence;
} opsdeck_details_t;
bool opsdeck_details_accept(const cJSON *o);
void opsdeck_details_copy(opsdeck_details_t *s,opsdeck_details_query_t *q);
void opsdeck_details_select(int slot,int kind,int page);
void opsdeck_details_select_project(int slot,int kind,int page,const char *group,const char *project);
void opsdeck_details_request_current(void);
void opsdeck_details_deactivate(void);
