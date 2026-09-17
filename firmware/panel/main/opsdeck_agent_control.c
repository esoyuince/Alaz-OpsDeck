#include "opsdeck_agent_control.h"
#include <string.h>
#include <math.h>
#include <limits.h>
#include <inttypes.h>
#include <stdio.h>
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"

static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_agent_control_t latest;
static int next_request=1;

static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out)
{
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
 if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;
 *out=(int)v->valuedouble;return true;
}
static bool ascii(const cJSON *o,const char *name,char *out,size_t size,bool allow_empty)
{
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsString(v))return false;
 size_t n=strlen(v->valuestring);if((!allow_empty&&!n)||n>=size)return false;
 for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32||c>126)return false;}
 memcpy(out,v->valuestring,n+1);return true;
}
bool opsdeck_agent_control_accept(const cJSON *o)
{
 opsdeck_agent_control_t s={0};const cJSON *locked=cJSON_GetObjectItemCaseSensitive(o,"locked"),*running=cJSON_GetObjectItemCaseSensitive(o,"managed_running"),*reconcile=cJSON_GetObjectItemCaseSensitive(o,"managed_reconcile");
 if(!cJSON_IsBool(locked)||!cJSON_IsBool(running)||!cJSON_IsBool(reconcile)||
    !integer(o,"phase",0,6,&s.phase)||!integer(o,"request_id",0,INT_MAX,&s.request_id)||!integer(o,"action",0,5,&s.action)||
    !ascii(o,"result",s.result,sizeof(s.result),false))return false;
 s.locked=cJSON_IsTrue(locked);s.managed_running=cJSON_IsTrue(running);s.managed_reconcile=cJSON_IsTrue(reconcile);
 const cJSON *workspaces=cJSON_GetObjectItemCaseSensitive(o,"workspaces"),*history=cJSON_GetObjectItemCaseSensitive(o,"history");
 if(!cJSON_IsArray(workspaces)||!cJSON_IsArray(history))return false;
 s.workspace_count=cJSON_GetArraySize(workspaces);s.history_count=cJSON_GetArraySize(history);
 if(s.workspace_count>OPSDECK_AGENT_WORKSPACES||s.history_count>OPSDECK_AGENT_HISTORY)return false;
 for(int i=0;i<s.workspace_count;i++){
  const cJSON *w=cJSON_GetArrayItem(workspaces,i);if(!cJSON_IsObject(w)||!integer(w,"id",0,OPSDECK_AGENT_WORKSPACES-1,&s.workspaces[i].id)||!ascii(w,"name",s.workspaces[i].name,sizeof(s.workspaces[i].name),false))return false;
  for(int j=0;j<i;j++)if(s.workspaces[j].id==s.workspaces[i].id)return false;
 }
 for(int i=0;i<s.history_count;i++){
  const cJSON *h=cJSON_GetArrayItem(history,i);if(!cJSON_IsObject(h)||!integer(h,"action",0,5,&s.history[i].action)||!integer(h,"state",0,7,&s.history[i].state)||!integer(h,"age_s",0,604800,&s.history[i].age_s)||!ascii(h,"result",s.history[i].result,sizeof(s.history[i].result),false))return false;
 }
 s.present=true;s.received_us=esp_timer_get_time();portENTER_CRITICAL(&mux);s.sequence=latest.sequence+1;latest=s;portEXIT_CRITICAL(&mux);
 if(s.sequence==1||s.sequence%5==0)ESP_LOGI("opsdeck","AGENT_CONTROL_RX sequence=%"PRIu32" phase=%d request=%d action=%d workspaces=%d history=%d running=%d locked=%d",s.sequence,s.phase,s.request_id,s.action,s.workspace_count,s.history_count,s.managed_running,s.locked);
 return true;
}
void opsdeck_agent_control_copy(opsdeck_agent_control_t *s){portENTER_CRITICAL(&mux);*s=latest;portEXIT_CRITICAL(&mux);}

static int allocate_request(void)
{
 portENTER_CRITICAL(&mux);int id=next_request;next_request=next_request==INT_MAX?1:next_request+1;portEXIT_CRITICAL(&mux);return id;
}
int opsdeck_agent_send_prompt(const char *prompt,int workspace_id,int sandbox)
{
 if(!prompt||workspace_id<0||workspace_id>=OPSDECK_AGENT_WORKSPACES||(sandbox!=0&&sandbox!=1))return 0;
 size_t bytes=strlen(prompt);if(!bytes||bytes>OPSDECK_AGENT_PROMPT_MAX)return 0;
 int request=allocate_request(),chunks=(int)((bytes+47)/48);
 ESP_LOGI("opsdeck.ui","AGENT_PROMPT_BEGIN request=%d bytes=%u chunks=%d repo=%d sandbox=%d",request,(unsigned)bytes,chunks,workspace_id,sandbox);
 static const char hex[]="0123456789ABCDEF";
 for(int i=0;i<chunks;i++){
  size_t start=(size_t)i*48,count=bytes-start;if(count>48)count=48;char encoded[97];
  for(size_t j=0;j<count;j++){unsigned char c=(unsigned char)prompt[start+j];encoded[j*2]=hex[c>>4];encoded[j*2+1]=hex[c&15];}encoded[count*2]=0;
  ESP_LOGI("opsdeck.ui","AGENT_PROMPT_CHUNK request=%d index=%d data=%s",request,i,encoded);
 }
 ESP_LOGI("opsdeck.ui","AGENT_PROMPT_COMMIT request=%d",request);return request;
}
int opsdeck_agent_send_action(int action)
{
 if(action<2||action>5)return 0;
 int request=allocate_request();const char *name=action==2?"stop":action==3?"resume":action==4?"retry":"handoff";
 ESP_LOGI("opsdeck.ui","AGENT_ACTION request=%d action=%s",request,name);return request;
}
void opsdeck_agent_confirm(int request_id){if(request_id>0)ESP_LOGI("opsdeck.ui","AGENT_CONFIRM request=%d",request_id);}
void opsdeck_agent_cancel(int request_id){if(request_id>0)ESP_LOGI("opsdeck.ui","AGENT_CANCEL request=%d",request_id);}

