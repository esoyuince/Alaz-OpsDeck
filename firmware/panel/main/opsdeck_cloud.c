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