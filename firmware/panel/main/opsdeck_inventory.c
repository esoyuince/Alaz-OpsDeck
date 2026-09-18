#include "opsdeck_inventory.h"
#include <string.h>
#include <math.h>
#include <limits.h>
#include <inttypes.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE inventory_mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_inventory_t latest;
static opsdeck_inventory_query_t query={.slot=0,.view=0,.page=0,.request_id=1,.group="all"};
static uint32_t sequence;
static bool active;
static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;
    *out=(int)v->valuedouble;return true;
}
static bool ascii(const cJSON *o,const char *name,char *out,size_t size)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsString(v))return false;
    size_t n=strlen(v->valuestring);if(n==0||n>=size)return false;
    for(size_t i=0;i<n;i++)if((unsigned char)v->valuestring[i]<32||(unsigned char)v->valuestring[i]>126)return false;
    memcpy(out,v->valuestring,n+1);return true;
}
static bool hex(const char *s,size_t n)
{
    if(strlen(s)!=n)return false;
    for(size_t i=0;i<n;i++)if(!((s[i]>='0'&&s[i]<='9')||(s[i]>='a'&&s[i]<='f')))return false;
    return true;
}
static bool group_valid(const char *s){return !strcmp(s,"all")||hex(s,16);}
void opsdeck_inventory_copy(opsdeck_inventory_t *s,opsdeck_inventory_query_t *q)
{
    portENTER_CRITICAL(&inventory_mux);
    if(s)*s=latest;
    if(q)*q=query;
    portEXIT_CRITICAL(&inventory_mux);
}
void opsdeck_inventory_select(int slot,int view,int page,const char *group)
{
    if(slot<0||slot>1||view<0||view>1||page<0||page>1999||!group||!group_valid(group)||(view==0&&strcmp(group,"all")))return;
    opsdeck_inventory_query_t next={.slot=slot,.view=view,.page=page};strcpy(next.group,group);
    portENTER_CRITICAL(&inventory_mux);
    next.request_id=query.request_id==INT_MAX?1:query.request_id+1;
    query=next;active=true;memset(&latest,0,sizeof(latest));
    portEXIT_CRITICAL(&inventory_mux);
    ESP_LOGI("opsdeck.ui","INVENTORY_REQUEST slot=%d view=%d page=%d group=%s request=%d",slot,view,page,group,next.request_id);
}
void opsdeck_inventory_deactivate(void)
{
    portENTER_CRITICAL(&inventory_mux);active=false;portEXIT_CRITICAL(&inventory_mux);
}
void opsdeck_inventory_request_current(void)
{
    opsdeck_inventory_query_t q;bool enabled;
    portENTER_CRITICAL(&inventory_mux);q=query;enabled=active;portEXIT_CRITICAL(&inventory_mux);
    if(enabled)ESP_LOGI("opsdeck.ui","INVENTORY_REQUEST slot=%d view=%d page=%d group=%s request=%d",q.slot,q.view,q.page,q.group,q.request_id);
}
bool opsdeck_inventory_accept(const cJSON *o)
{
    opsdeck_inventory_t s={0};
    const cJSON *test=cJSON_GetObjectItemCaseSensitive(o,"test"),*map=cJSON_GetObjectItemCaseSensitive(o,"map_ok");
    if(!cJSON_IsBool(test)||!cJSON_IsBool(map))return false;
    s.test=cJSON_IsTrue(test);s.map_ok=cJSON_IsTrue(map);
    if(!integer(o,"slot",0,1,&s.slot)||!integer(o,"view",0,1,&s.view)||
       !integer(o,"requested_page",0,1999,&s.requested_page)||!integer(o,"page",0,1999,&s.page)||
       !integer(o,"total_pages",0,2000,&s.total_pages)||!integer(o,"total_rows",0,4000,&s.total_rows)||
       !integer(o,"known_resources",-1,4000,&s.known_resources)||!integer(o,"complete_sources",0,4,&s.complete_sources)||
       !integer(o,"source_count",4,4,&s.source_count)||!integer(o,"state",0,6,&s.state)||
       !integer(o,"age_s",-1,INT_MAX,&s.age_s)||!integer(o,"request_id",1,INT_MAX,&s.request_id))return false;
    const cJSON *project_health=cJSON_GetObjectItemCaseSensitive(o,"project_health");
    if(project_health){s.project_health_present=true;if(!integer(o,"project_health",0,3,&s.project_health))return false;}
    const cJSON *project_health_label=cJSON_GetObjectItemCaseSensitive(o,"project_health_label");
    if(cJSON_IsString(project_health_label)){
        size_t n=strlen(project_health_label->valuestring);if(n==0||n>=sizeof(s.project_health_label))return false;
        for(size_t i=0;i<n;i++)if((unsigned char)project_health_label->valuestring[i]<32||(unsigned char)project_health_label->valuestring[i]>126)return false;
        memcpy(s.project_health_label,project_health_label->valuestring,n+1);
    }else if(project_health_label&&!cJSON_IsNull(project_health_label))return false;
    if(!ascii(o,"generation",s.generation,sizeof(s.generation))||!hex(s.generation,8)||
       !ascii(o,"group",s.group,sizeof(s.group))||!group_valid(s.group)||
       !ascii(o,"scope",s.scope,sizeof(s.scope))||!ascii(o,"account_name",s.account_name,sizeof(s.account_name)))return false;
    const char *summary_names[]={"project_workers","project_d1","project_r2","project_pages","project_ok","project_attention","project_degraded","project_unknown","project_health_age_s"};
    int present_summary=0;for(size_t i=0;i<sizeof(summary_names)/sizeof(summary_names[0]);i++)if(cJSON_GetObjectItemCaseSensitive(o,summary_names[i]))present_summary++;
    if(present_summary!=0&&present_summary!=9)return false;
    if(present_summary==9){
        s.project_summary_present=true;
        if(!integer(o,"project_workers",0,4000,&s.project_workers)||!integer(o,"project_d1",0,4000,&s.project_d1)||
           !integer(o,"project_r2",0,4000,&s.project_r2)||!integer(o,"project_pages",0,4000,&s.project_pages)||
           !integer(o,"project_ok",0,4000,&s.project_ok)||!integer(o,"project_attention",0,4000,&s.project_attention)||
           !integer(o,"project_degraded",0,4000,&s.project_degraded)||!integer(o,"project_unknown",0,4000,&s.project_unknown)||
           !integer(o,"project_health_age_s",-1,INT_MAX,&s.project_health_age_s))return false;
    }
    if(s.view==0&&strcmp(s.group,"all"))return false;
    if(s.project_summary_present){
        if(s.view!=1||!strcmp(s.group,"all"))return false;
        if(s.project_workers+s.project_d1+s.project_r2+s.project_pages!=s.total_rows)return false;
        if(s.project_ok+s.project_attention+s.project_degraded+s.project_unknown>s.project_workers+s.project_d1+s.project_r2)return false;
    }
    if(s.total_pages!=(s.total_rows+OPSDECK_INVENTORY_ROWS-1)/OPSDECK_INVENTORY_ROWS)return false;
    int expected_page=s.total_pages?(s.requested_page<s.total_pages?s.requested_page:s.total_pages-1):0;
    if(s.page!=expected_page)return false;
    if(s.known_resources<0&&s.total_rows!=0)return false;
    if(s.known_resources>=0&&s.total_rows>s.known_resources)return false;
    if(s.state==1&&(s.complete_sources!=4||s.known_resources<0||s.age_s<0||s.age_s>OPSDECK_INVENTORY_TTL))return false;
    if(!s.map_ok&&(s.view==0||strcmp(s.group,"all"))&&s.total_rows>0)return false;
    const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"rows");if(!cJSON_IsArray(rows))return false;
    s.row_count=cJSON_GetArraySize(rows);
    int expected_rows=s.total_rows-s.page*OPSDECK_INVENTORY_ROWS;
    if(expected_rows>OPSDECK_INVENTORY_ROWS)expected_rows=OPSDECK_INVENTORY_ROWS;
    if(s.row_count!=expected_rows)return false;
    for(int i=0;i<s.row_count;i++){
        const cJSON *row=cJSON_GetArrayItem(rows,i);
        if(!cJSON_IsObject(row)||!ascii(row,"key",s.rows[i].key,sizeof(s.rows[i].key))||!hex(s.rows[i].key,16)||
           !ascii(row,"label",s.rows[i].label,sizeof(s.rows[i].label))||!ascii(row,"detail",s.rows[i].detail,sizeof(s.rows[i].detail)))return false;
        const cJSON *health=cJSON_GetObjectItemCaseSensitive(row,"health");
        if(health){s.rows[i].health_present=true;if(!integer(row,"health",0,3,&s.rows[i].health))return false;}
        const cJSON *kind=cJSON_GetObjectItemCaseSensitive(row,"kind");
        if(kind&&!cJSON_IsNull(kind)){s.rows[i].kind_present=true;if(!integer(row,"kind",0,3,&s.rows[i].kind))return false;}
        if(s.view==0&&s.rows[i].kind_present)return false;
        for(int j=0;j<i;j++)if(!strcmp(s.rows[i].key,s.rows[j].key))return false;
    }
    s.present=true;s.received_us=esp_timer_get_time();
    portENTER_CRITICAL(&inventory_mux);
    bool match=s.slot==query.slot&&s.view==query.view&&s.requested_page==query.page&&s.request_id==query.request_id&&!strcmp(s.group,query.group);
    if(match){s.sequence=++sequence;latest=s;}
    portEXIT_CRITICAL(&inventory_mux);
    if(!match)return false;
    ESP_LOGI("opsdeck","INVENTORY_RX request=%d slot=%d view=%d page=%d rows=%d state=%d test=%d",s.request_id,s.slot,s.view,s.page,s.row_count,s.state,s.test);
    return true;
}
