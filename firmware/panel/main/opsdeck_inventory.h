#pragma once
#include <stdint.h>
#include <stdbool.h>
#include "cJSON.h"
#define OPSDECK_INVENTORY_ROWS 4
#define OPSDECK_INVENTORY_TTL 900
/* Bounded display navigation; it cannot change Cloudflare resources. */
typedef struct {int slot,view,page,request_id;char group[17];} opsdeck_inventory_query_t;
typedef struct {char key[17],label[49],detail[97];int health,kind;bool health_present,kind_present;} opsdeck_inventory_row_t;
typedef struct {
    bool present,test,map_ok,project_health_present,project_summary_present;
    int slot,view,requested_page,page,total_pages,total_rows,known_resources;
    int complete_sources,source_count,state,age_s,request_id,row_count,project_health;
    int project_workers,project_d1,project_r2,project_pages,project_ok,project_attention,project_degraded,project_unknown,project_health_age_s;
    char generation[9],group[17],scope[49],account_name[23],project_health_label[21];
    opsdeck_inventory_row_t rows[OPSDECK_INVENTORY_ROWS];
    int64_t received_us;uint32_t sequence;
} opsdeck_inventory_t;
bool opsdeck_inventory_accept(const cJSON *o);
void opsdeck_inventory_copy(opsdeck_inventory_t *s,opsdeck_inventory_query_t *q);
void opsdeck_inventory_request_current(void);
void opsdeck_inventory_select(int slot,int view,int page,const char *group);
void opsdeck_inventory_deactivate(void);
