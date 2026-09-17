#include "opsdeck_codex_sessions.h"
#include <string.h>
#include <math.h>
#include <limits.h>
#include <inttypes.h>
#include <stdio.h>
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"

static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_codex_sessions_t sessions_latest;
static opsdeck_codex_chat_t chat_latest;
static int next_request=1,query_request,query_page;
static char query_key[17];
static int allocate_request(void){portENTER_CRITICAL(&mux);int id=next_request;next_request=next_request==INT_MAX?1:next_request+1;portEXIT_CRITICAL(&mux);return id;}
static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out)
{
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;*out=(int)v->valuedouble;return true;
}
static bool ascii(const cJSON *o,const char *name,char *out,size_t size,bool empty)
{
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsString(v))return false;size_t n=strlen(v->valuestring);if((!empty&&!n)||n>=size)return false;
 for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32||c>126)return false;}memcpy(out,v->valuestring,n+1);return true;
}
static bool key_valid(const char *s)
{
 if(strlen(s)!=16)return false;
 for(int i=0;i<16;i++){if(!((s[i]>='0'&&s[i]<='9')||(s[i]>='a'&&s[i]<='f')))return false;}
 return true;
}
static bool utf8_text(const cJSON *o,const char *name,char *out,size_t size)
{
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsString(v))return false;size_t n=strlen(v->valuestring);if(!n||n>=size)return false;
 for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32&&c!='\n'&&c!='\t')return false;}memcpy(out,v->valuestring,n+1);return true;
}
void opsdeck_codex_sessions_copy(opsdeck_codex_sessions_t *out){portENTER_CRITICAL(&mux);*out=sessions_latest;portEXIT_CRITICAL(&mux);}
void opsdeck_codex_chat_copy(opsdeck_codex_chat_t *out){portENTER_CRITICAL(&mux);*out=chat_latest;portEXIT_CRITICAL(&mux);}

bool opsdeck_codex_sessions_accept(const cJSON *o)
{
 opsdeck_codex_sessions_t s={0};const cJSON *locked=cJSON_GetObjectItemCaseSensitive(o,"locked"),*rows=cJSON_GetObjectItemCaseSensitive(o,"sessions");
 if(!cJSON_IsBool(locked)||!cJSON_IsArray(rows))return false;
 s.locked=cJSON_IsTrue(locked);s.session_count=cJSON_GetArraySize(rows);if(s.session_count>OPSDECK_CODEX_SESSIONS)return false;
 for(int i=0;i<s.session_count;i++){
  const cJSON *r=cJSON_GetArrayItem(rows,i),*running,*write;if(!cJSON_IsObject(r)||!ascii(r,"key",s.sessions[i].key,sizeof(s.sessions[i].key),false)||!key_valid(s.sessions[i].key)||!ascii(r,"repo",s.sessions[i].repo,sizeof(s.sessions[i].repo),false)||!ascii(r,"activity",s.sessions[i].activity,sizeof(s.sessions[i].activity),false)||!integer(r,"age_s",0,604800,&s.sessions[i].age_s))return false;
  running=cJSON_GetObjectItemCaseSensitive(r,"running");write=cJSON_GetObjectItemCaseSensitive(r,"write");if(!cJSON_IsBool(running)||!cJSON_IsBool(write))return false;s.sessions[i].running=cJSON_IsTrue(running);s.sessions[i].write=cJSON_IsTrue(write);
  for(int j=0;j<i;j++)if(!strcmp(s.sessions[j].key,s.sessions[i].key))return false;
 }
 const cJSON *a=cJSON_GetObjectItemCaseSensitive(o,"approval");
 if(a&&!cJSON_IsNull(a)){
  if(!cJSON_IsObject(a)||!integer(a,"id",1,INT_MAX,&s.approval.id)||!integer(a,"age_s",0,3600,&s.approval.age_s)||!ascii(a,"key",s.approval.key,sizeof(s.approval.key),false)||!key_valid(s.approval.key)||!ascii(a,"kind",s.approval.kind,sizeof(s.approval.kind),false)||!ascii(a,"summary",s.approval.summary,sizeof(s.approval.summary),false))return false;
  s.approval_present=true;
 }
 if(s.locked&&(s.session_count||s.approval_present))return false;
 s.present=true;s.received_us=esp_timer_get_time();
 portENTER_CRITICAL(&mux);s.sequence=sessions_latest.sequence+1;sessions_latest=s;portEXIT_CRITICAL(&mux);
 if(s.sequence==1||s.sequence%5==0)ESP_LOGI("opsdeck","CODEX_SESSIONS_RX sequence=%"PRIu32" count=%d approval=%d locked=%d",s.sequence,s.session_count,s.approval_present,s.locked);
 return true;
}

bool opsdeck_codex_chat_accept(const cJSON *o)
{
 opsdeck_codex_chat_t s={0};const cJSON *running=cJSON_GetObjectItemCaseSensitive(o,"running"),*rows=cJSON_GetObjectItemCaseSensitive(o,"messages");
 if(!cJSON_IsBool(running)||!cJSON_IsArray(rows)||!integer(o,"request_id",1,INT_MAX,&s.request_id)||!integer(o,"page",0,999,&s.page)||!integer(o,"total_pages",1,1000,&s.total_pages)||!ascii(o,"key",s.key,sizeof(s.key),false)||!key_valid(s.key)||!ascii(o,"repo",s.repo,sizeof(s.repo),false)||!ascii(o,"activity",s.activity,sizeof(s.activity),false)||!ascii(o,"result",s.result,sizeof(s.result),false))return false;
 s.running=cJSON_IsTrue(running);s.message_count=cJSON_GetArraySize(rows);if(s.message_count>OPSDECK_CODEX_CHAT_MESSAGES||s.page>=s.total_pages)return false;
 for(int i=0;i<s.message_count;i++){const cJSON *m=cJSON_GetArrayItem(rows,i);if(!cJSON_IsObject(m)||!integer(m,"role",1,2,&s.messages[i].role)||!utf8_text(m,"text",s.messages[i].text,sizeof(s.messages[i].text)))return false;}
 portENTER_CRITICAL(&mux);bool match=s.request_id==query_request&&s.page==query_page&&!strcmp(s.key,query_key);if(match){s.present=true;s.received_us=esp_timer_get_time();s.sequence=chat_latest.sequence+1;chat_latest=s;}portEXIT_CRITICAL(&mux);
 if(match)ESP_LOGI("opsdeck","CODEX_CHAT_RX sequence=%"PRIu32" request=%d page=%d/%d messages=%d running=%d",s.sequence,s.request_id,s.page+1,s.total_pages,s.message_count,s.running);
 return match;
}
int opsdeck_codex_chat_request(const char *key,int page)
{
 if(!key||!key_valid(key)||page<0||page>999)return 0;
 int request=allocate_request();portENTER_CRITICAL(&mux);query_request=request;query_page=page;memcpy(query_key,key,17);portEXIT_CRITICAL(&mux);
 ESP_LOGI("opsdeck.ui","CODEX_CHAT_REQUEST request=%d key=%s page=%d",request,key,page);return request;
}
int opsdeck_codex_send_continue(const char *key,const char *prompt)
{
 if(!key||!key_valid(key)||!prompt)return 0;
 size_t bytes=strlen(prompt);if(!bytes||bytes>2048)return 0;int request=allocate_request(),chunks=(int)((bytes+47)/48);
 ESP_LOGI("opsdeck.ui","CODEX_CONTINUE_BEGIN request=%d key=%s bytes=%u chunks=%d",request,key,(unsigned)bytes,chunks);static const char hex[]="0123456789ABCDEF";
 for(int i=0;i<chunks;i++){size_t start=(size_t)i*48,count=bytes-start;if(count>48)count=48;char encoded[97];for(size_t j=0;j<count;j++){unsigned char c=(unsigned char)prompt[start+j];encoded[j*2]=hex[c>>4];encoded[j*2+1]=hex[c&15];}encoded[count*2]=0;ESP_LOGI("opsdeck.ui","CODEX_CONTINUE_CHUNK request=%d index=%d data=%s",request,i,encoded);}
 ESP_LOGI("opsdeck.ui","CODEX_CONTINUE_COMMIT request=%d",request);return request;
}
int opsdeck_codex_send_action(const char *key,int action)
{
 if(!key||!key_valid(key)||action<2||action>4)return 0;
 int request=allocate_request();const char *name=action==2?"stop":action==3?"resume":"retry";ESP_LOGI("opsdeck.ui","CODEX_SESSION_ACTION request=%d key=%s action=%s",request,key,name);return request;
}
int opsdeck_codex_send_approval(int approval_id,bool allow)
{
 if(approval_id<=0)return 0;
 int request=allocate_request();ESP_LOGI("opsdeck.ui","CODEX_APPROVAL request=%d approval=%d decision=%s",request,approval_id,allow?"allow":"deny");return request;
}
