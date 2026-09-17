#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "cJSON.h"

#define OPSDECK_CODEX_SESSIONS 16
#define OPSDECK_CODEX_CHAT_MESSAGES 3
#define OPSDECK_CODEX_CHAT_TEXT 641

typedef struct {char key[17],repo[25],activity[17];bool running,write;int age_s;} opsdeck_codex_session_t;
typedef struct {int id,age_s;char key[17],kind[9],summary[97];} opsdeck_codex_approval_t;
typedef struct {bool present,locked,approval_present;int session_count;opsdeck_codex_session_t sessions[OPSDECK_CODEX_SESSIONS];opsdeck_codex_approval_t approval;uint32_t sequence;int64_t received_us;} opsdeck_codex_sessions_t;
typedef struct {int role;char text[OPSDECK_CODEX_CHAT_TEXT];} opsdeck_codex_chat_message_t;
typedef struct {bool present,running;int request_id,page,total_pages,message_count;char key[17],repo[25],activity[17],result[33];opsdeck_codex_chat_message_t messages[OPSDECK_CODEX_CHAT_MESSAGES];uint32_t sequence;int64_t received_us;} opsdeck_codex_chat_t;

bool opsdeck_codex_sessions_accept(const cJSON *o);
bool opsdeck_codex_chat_accept(const cJSON *o);
void opsdeck_codex_sessions_copy(opsdeck_codex_sessions_t *out);
void opsdeck_codex_chat_copy(opsdeck_codex_chat_t *out);
int opsdeck_codex_chat_request(const char *key,int page);
int opsdeck_codex_send_continue(const char *key,const char *prompt);
int opsdeck_codex_send_action(const char *key,int action);
int opsdeck_codex_send_approval(int approval_id,bool allow);
