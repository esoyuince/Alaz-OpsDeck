#include "opsdeck_cloud.h"
#include <string.h>
#include <math.h>
#include <inttypes.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE cloud_mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_cloud_t slots[2];static char generation[9];static uint32_t sequence;
static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;
    *out=(int)v->valuedouble;return true;
}
static bool summary_number(const cJSON *o,const char *name,double *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble< -1||v->valuedouble>1e16)return false;
    if(v->valuedouble<0&&v->valuedouble!=-1)return false;
    *out=v->valuedouble;return true;
}
bool opsdeck_cloud_accept(const cJSON *o)
{
    opsdeck_cloud_t s={0};int slot;const cJSON *g=cJSON_GetObjectItemCaseSensitive(o,"generation"),*n=cJSON_GetObjectItemCaseSensitive(o,"name"),*e=cJSON_GetObjectItemCaseSensitive(o,"enabled");
    if(!integer(o,"slot",0,1,&slot)||!integer(o,"total",1,2,&s.total)||slot>=s.total||!cJSON_IsBool(e))return false;
    if(!cJSON_IsString(g)||strlen(g->valuestring)!=8||!cJSON_IsString(n)||strlen(n->valuestring)==0||strlen(n->valuestring)>22)return false;
    for(const char *p=g->valuestring;*p;p++)if(!((*p>='0'&&*p<='9')||(*p>='a'&&*p<='f')))return false;
    for(const char *p=n->valuestring;*p;p++)if((unsigned char)*p<32||(unsigned char)*p>126)return false;
    const char *names[]={"workers","d1","r2","hosting","cost"};
    for(int i=0;i<5;i++)if(!opsdeck_metric_parse(cJSON_GetObjectItemCaseSensitive(o,names[i]),&s.metrics[i]))return false;
    if(!opsdeck_cost_valid(&s.metrics[4]))return false;
    const cJSON *summary=cJSON_GetObjectItemCaseSensitive(o,"summary");
    if(summary){
        if(!cJSON_IsObject(summary)||
           !integer(summary,"inventory_state",0,6,&s.inventory_state)||!integer(summary,"inventory_age_s",-1,864000,&s.inventory_age_s)||
           !integer(summary,"resource_count",-1,4000,&s.resource_count)||!integer(summary,"complete_sources",0,4,&s.complete_sources)||
           !integer(summary,"workers",-1,4000,&s.worker_count)||!integer(summary,"d1",-1,4000,&s.d1_count)||
           !integer(summary,"r2",-1,4000,&s.r2_count)||!integer(summary,"pages",-1,4000,&s.pages_count)||
           !integer(summary,"queue_state",0,6,&s.queue_state)||!integer(summary,"queue_age_s",-1,864000,&s.queue_age_s)||
           !integer(summary,"queue_count",-1,1000,&s.queue_count)||!integer(summary,"queue_observed",-1,1000,&s.queue_observed)||
           !integer(summary,"ai_state",0,6,&s.ai_state)||!integer(summary,"ai_age_s",-1,864000,&s.ai_age_s)||
           !integer(summary,"gateway_state",0,6,&s.gateway_state)||!integer(summary,"gateway_age_s",-1,864000,&s.gateway_age_s)||
           !summary_number(summary,"ai_requests",&s.ai_requests)||!summary_number(summary,"ai_input_tokens",&s.ai_input_tokens)||
           !summary_number(summary,"ai_output_tokens",&s.ai_output_tokens)||!summary_number(summary,"gateway_requests",&s.gateway_requests)||
           !summary_number(summary,"gateway_errors",&s.gateway_errors)||!summary_number(summary,"gateway_cached",&s.gateway_cached))return false;
        if(s.resource_count>=0&&s.worker_count>=0&&s.d1_count>=0&&s.r2_count>=0&&s.pages_count>=0&&
           s.worker_count+s.d1_count+s.r2_count+s.pages_count!=s.resource_count)return false;
        if(s.queue_count>=0&&s.queue_observed>s.queue_count)return false;
        s.summary_present=true;
    }
    strcpy(s.name,n->valuestring);s.enabled=cJSON_IsTrue(e);s.received_us=esp_timer_get_time();
    portENTER_CRITICAL(&cloud_mux);
    if(strcmp(generation,g->valuestring)){memset(slots,0,sizeof(slots));strcpy(generation,g->valuestring);}
    s.sequence=++sequence;slots[slot]=s;portEXIT_CRITICAL(&cloud_mux);
    if(s.sequence<=2||s.sequence%10==0)ESP_LOGI("opsdeck","CLOUD_RX sequence=%"PRIu32" slot=%d enabled=%d",s.sequence,slot,s.enabled);
    return true;
}
void opsdeck_cloud_copy(int slot,opsdeck_cloud_t *s)
{
    memset(s,0,sizeof(*s));if(slot<0||slot>1)return;
    portENTER_CRITICAL(&cloud_mux);*s=slots[slot];portEXIT_CRITICAL(&cloud_mux);
}