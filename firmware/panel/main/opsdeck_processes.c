#include "opsdeck_processes.h"
#include <math.h>
#include <string.h>
#include <inttypes.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"

static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_processes_t latest;

static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||floor(v->valuedouble)!=v->valuedouble||v->valuedouble<lo||v->valuedouble>hi)return false;
    *out=(int)v->valuedouble;return true;
}
static bool number(const cJSON *o,const char *name,double lo,double hi,float *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi)return false;
    *out=(float)v->valuedouble;return true;
}
static bool name_text(const cJSON *o,char *out,size_t cap)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,"name");
    if(!cJSON_IsString(v)||strlen(v->valuestring)>=cap)return false;
    for(const unsigned char *p=(const unsigned char *)v->valuestring;*p;p++)if(*p<32)return false;
    strcpy(out,v->valuestring);return true;
}
bool opsdeck_processes_accept(const cJSON *o)
{
    opsdeck_processes_t s={0};const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"rows");
    if(!cJSON_IsArray(rows)||!integer(o,"total",0,100000,&s.total_count))return false;
    s.row_count=cJSON_GetArraySize(rows);if(s.row_count<0||s.row_count>OPSDECK_PROCESS_ROWS||s.row_count>s.total_count)return false;
    for(int i=0;i<s.row_count;i++){
        const cJSON *r=cJSON_GetArrayItem(rows,i);if(!cJSON_IsObject(r))return false;
        if(!integer(r,"pid",1,2147483647,&s.rows[i].pid)||!name_text(r,s.rows[i].name,sizeof(s.rows[i].name))||
           !number(r,"cpu",0,100,&s.rows[i].cpu_pct)||!number(r,"ram_mib",0,1048576,&s.rows[i].ram_mib))return false;
        for(int j=0;j<i;j++)if(s.rows[j].pid==s.rows[i].pid)return false;
    }
    s.present=true;s.received_us=esp_timer_get_time();
    portENTER_CRITICAL(&mux);s.sequence=latest.sequence+1;latest=s;portEXIT_CRITICAL(&mux);
    if(s.sequence==1||s.sequence%5==0)ESP_LOGI("opsdeck","PROCESS_RX sequence=%"PRIu32" rows=%d total=%d",s.sequence,s.row_count,s.total_count);
    return true;
}

void opsdeck_processes_copy(opsdeck_processes_t *out)
{
    if(!out)return;
    portENTER_CRITICAL(&mux);*out=latest;portEXIT_CRITICAL(&mux);
}
