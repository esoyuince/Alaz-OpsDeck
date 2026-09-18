#include "opsdeck_details.h"
#include <string.h>
#include <math.h>
#include <limits.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_details_t latest;
static opsdeck_details_query_t query={.slot=0,.kind=0,.page=0,.request_id=1,.group="all"};
static uint32_t sequence;
static bool active;
static bool unique(const cJSON *o,int depth)
{
    if(depth>4)return false;
    if(cJSON_IsObject(o))for(const cJSON *a=o->child;a;a=a->next){
        if(!a->string)return false;
        for(const cJSON *b=a->next;b;b=b->next)if(b->string&&!strcmp(a->string,b->string))return false;
    }
    for(const cJSON *a=o->child;a;a=a->next)if(!unique(a,depth+1))return false;
    return true;
}
static bool integer(const cJSON *o,const char *name,int low,int high,int *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<low||v->valuedouble>high||floor(v->valuedouble)!=v->valuedouble)return false;
    *out=(int)v->valuedouble;return true;
}
static bool ascii(const cJSON *o,const char *name,char *out,size_t size)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);if(!cJSON_IsString(v))return false;
    size_t n=strlen(v->valuestring);if(!n||n>=size)return false;
    bool nonspace=false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32||c>126)return false;if(c!=32)nonspace=true;}
    if(!nonspace)return false;
    memcpy(out,v->valuestring,n+1);return true;
}
static bool optional_ascii(const cJSON *o,const char *name,char *out,size_t size)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);out[0]=0;
    if(!v||cJSON_IsNull(v))return true;
    if(!cJSON_IsString(v))return false;
    size_t n=strlen(v->valuestring);if(n>=size)return false;if(!n)return true;bool nonspace=false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32||c>126)return false;if(c!=32)nonspace=true;}
    if(!nonspace)return false;
    memcpy(out,v->valuestring,n+1);return true;
}
static bool hex(const char *s,size_t n)
{
    if(strlen(s)!=n)return false;
    for(size_t i=0;i<n;i++)if(!((s[i]>='0'&&s[i]<='9')||(s[i]>='a'&&s[i]<='f')))return false;
    return true;
}
static bool group_valid(const char *s){return !strcmp(s,"all")||hex(s,16);}
static bool project_valid(const char *s)
{
    size_t n=strlen(s);if(!n||n>=49)return false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)s[i];if(c<32||c>126)return false;}
    return true;
}
void opsdeck_details_copy(opsdeck_details_t *s,opsdeck_details_query_t *q)
{
    portENTER_CRITICAL(&mux);if(s)*s=latest;if(q)*q=query;portEXIT_CRITICAL(&mux);
}
void opsdeck_details_deactivate(void)
{
    portENTER_CRITICAL(&mux);active=false;memset(&latest,0,sizeof(latest));portEXIT_CRITICAL(&mux);
}
void opsdeck_details_request_current(void)
{
    opsdeck_details_query_t q;bool enabled;
    portENTER_CRITICAL(&mux);q=query;enabled=active;portEXIT_CRITICAL(&mux);
    if(enabled){
        if(strcmp(q.group,"all"))ESP_LOGI("opsdeck.ui","DETAILS_REQUEST slot=%d kind=%d page=%d group=%s request=%d",q.slot,q.kind,q.page,q.group,q.request_id);
        else ESP_LOGI("opsdeck.ui","DETAILS_REQUEST slot=%d kind=%d page=%d request=%d",q.slot,q.kind,q.page,q.request_id);
    }
}
void opsdeck_details_select(int slot,int kind,int page)
{
    if(slot<0||slot>1||kind<0||kind>=OPSDECK_DETAILS_KINDS||page<0||page>1023)return;
    portENTER_CRITICAL(&mux);
    int request=query.request_id==INT_MAX?1:query.request_id+1;
    memset(&query,0,sizeof(query));query.slot=slot;query.kind=kind;query.page=page;query.request_id=request;strcpy(query.group,"all");
    active=true;memset(&latest,0,sizeof(latest));portEXIT_CRITICAL(&mux);
    opsdeck_details_request_current();
}
void opsdeck_details_select_project(int slot,int kind,int page,const char *group,const char *project)
{
    if(slot<0||slot>1||kind<0||kind>2||page<0||page>1023||!group||!project||!hex(group,16)||!project_valid(project))return;
    portENTER_CRITICAL(&mux);
    int request=query.request_id==INT_MAX?1:query.request_id+1;
    memset(&query,0,sizeof(query));query.slot=slot;query.kind=kind;query.page=page;query.request_id=request;strcpy(query.group,group);strcpy(query.project,project);
    active=true;memset(&latest,0,sizeof(latest));portEXIT_CRITICAL(&mux);
    opsdeck_details_request_current();
}
bool opsdeck_details_accept(const cJSON *o)
{
    if(!cJSON_IsObject(o)||!unique(o,0))return false;
    const cJSON *type=cJSON_GetObjectItemCaseSensitive(o,"type"),*test=cJSON_GetObjectItemCaseSensitive(o,"test");
    if(!cJSON_IsString(type)||strcmp(type->valuestring,"opsdeck.details.v1")||!cJSON_IsBool(test))return false;
    opsdeck_details_t s={0};s.test=cJSON_IsTrue(test);
    if(!integer(o,"slot",0,1,&s.slot)||!integer(o,"kind",0,5,&s.kind)||!integer(o,"requested_page",0,1023,&s.requested_page)||
       !integer(o,"page",0,1023,&s.page)||!integer(o,"total_pages",0,1024,&s.total_pages)||!integer(o,"request_id",1,INT_MAX,&s.request_id)||
       !integer(o,"state",0,6,&s.state)||!integer(o,"age_s",-1,INT_MAX,&s.age_s))return false;
    const cJSON *group=cJSON_GetObjectItemCaseSensitive(o,"group");
    if(!group||cJSON_IsNull(group))strcpy(s.group,"all");
    else if(!ascii(o,"group",s.group,sizeof(s.group))||!group_valid(s.group))return false;
    if(!ascii(o,"generation",s.generation,sizeof(s.generation))||!hex(s.generation,8)||
       !ascii(o,"key",s.key,sizeof(s.key))||!hex(s.key,16)||!ascii(o,"account_name",s.account_name,sizeof(s.account_name))||
       !ascii(o,"title",s.title,sizeof(s.title))||!ascii(o,"scope",s.scope,sizeof(s.scope))||!ascii(o,"note",s.note,sizeof(s.note))||
       !optional_ascii(o,"reason",s.reason,sizeof(s.reason)))return false;
    if(s.page!=(s.total_pages?(s.requested_page<s.total_pages?s.requested_page:s.total_pages-1):0))return false;
    const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"rows");if(!cJSON_IsArray(rows))return false;
    s.row_count=cJSON_GetArraySize(rows);if(s.row_count>4)return false;
    if(s.total_pages==0&&(s.row_count||s.age_s!=-1||s.state==1||s.state==3||s.state==5))return false;
    if(s.total_pages>0&&(!s.row_count||s.age_s<0))return false;
    if(s.state==1&&s.age_s>OPSDECK_DETAILS_TTL)return false;
    for(int i=0;i<s.row_count;i++){
        const cJSON *row=cJSON_GetArrayItem(rows,i);if(!cJSON_IsObject(row))return false;
        opsdeck_detail_metric_t *m=&s.rows[i];
        if(!ascii(row,"label",m->label,sizeof(m->label))||!ascii(row,"unit",m->unit,sizeof(m->unit)))return false;
        for(int j=0;j<i;j++)if(!strcmp(m->label,s.rows[j].label))return false;
        const cJSON *value=cJSON_GetObjectItemCaseSensitive(row,"value");if(!value)return false;
        if(!cJSON_IsNull(value)){
            if(!cJSON_IsNumber(value)||!isfinite(value->valuedouble)||value->valuedouble<0||value->valuedouble>1e16)return false;
            m->valid=true;m->value=value->valuedouble;
        }
    }
    s.present=true;s.received_us=esp_timer_get_time();
    portENTER_CRITICAL(&mux);
    bool match=active&&s.slot==query.slot&&s.kind==query.kind&&s.requested_page==query.page&&s.request_id==query.request_id&&!strcmp(s.group,query.group);
    if(match){s.sequence=++sequence;latest=s;}portEXIT_CRITICAL(&mux);
    if(match)ESP_LOGI("opsdeck","DETAILS_RX request=%d slot=%d kind=%d page=%d group=%s rows=%d state=%d test=%d",s.request_id,s.slot,s.kind,s.page,s.group,s.row_count,s.state,s.test);
    return match;
}
