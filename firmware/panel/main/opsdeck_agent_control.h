#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "cJSON.h"

#define OPSDECK_AGENT_WORKSPACES 32
#define OPSDECK_AGENT_HISTORY 4
#define OPSDECK_AGENT_PROMPT_MAX 2048

typedef struct {int id;char name[25];} opsdeck_agent_workspace_t;
typedef struct {int action,state,age_s;char result[33];} opsdeck_agent_history_t;
typedef struct {
 bool present,locked,managed_running,managed_reconcile;
 int phase,request_id,action,workspace_count,history_count;
 char result[33];opsdeck_agent_workspace_t workspaces[OPSDECK_AGENT_WORKSPACES];
 opsdeck_agent_history_t history[OPSDECK_AGENT_HISTORY];
 uint32_t sequence;int64_t received_us;
} opsdeck_agent_control_t;

bool opsdeck_agent_control_accept(const cJSON *o);
void opsdeck_agent_control_copy(opsdeck_agent_control_t *s);
int opsdeck_agent_send_prompt(const char *prompt,int workspace_id,int sandbox);
int opsdeck_agent_send_action(int action);
void opsdeck_agent_confirm(int request_id);
void opsdeck_agent_cancel(int request_id);
