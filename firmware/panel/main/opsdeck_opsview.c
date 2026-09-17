#include "opsdeck_opsview.h"
#include <string.h>
#include <math.h>
#include <limits.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_opsview_t latest;
static opsdeck_opsview_query_t query={.kind=0,.window=1,.metric=0,.request_id=1};
static uint32_t sequence;static bool active;
static bool integer(const cJSON *o,const char *name,int low,int high,int *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<low||v->valuedouble>high||floor(v->valuedouble)!=v->valuedouble)return false;
    *out=(int)v->valuedouble;return true;
}
static bool bounded_text(const cJSON *o,const char *name,char *out,size_t size,bool optional)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);out[0]=0;
    if((!v||cJSON_IsNull(v))&&optional)return true;
    if(!cJSON_IsString(v))return false;
    size_t n=strlen(v->valuestring);if((!n&&!optional)||n>=size)return false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)v->valuestring[i];if(c<32||c==127)return false;}
    memcpy(out,v->valuestring,n+1);return true;
}
static bool nullable_number(const cJSON *o,const char *name,bool *valid,double *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);*valid=false;*out=0;
    if(!v||cJSON_IsNull(v))return true;
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||fabs(v->valuedouble)>1e12)return false;
    *valid=true;*out=v->valuedouble;return true;
}
void opsdeck_opsview_copy(opsdeck_opsview_t *s,opsdeck_opsview_query_t *q)
{
    portENTER_CRITICAL(&mux);if(s)*s=latest;if(q)*q=query;portEXIT_CRITICAL(&mux);
}
void opsdeck_opsview_deactivate(void)
{
    portENTER_CRITICAL(&mux);active=false;memset(&latest,0,sizeof(latest));portEXIT_CRITICAL(&mux);
}
void opsdeck_opsview_request_current(void)
{
    opsdeck_opsview_query_t q;bool enabled;portENTER_CRITICAL(&mux);q=query;enabled=active;portEXIT_CRITICAL(&mux);
    if(enabled)ESP_LOGI("opsdeck.ui","OPSVIEW_REQUEST kind=%d window=%d metric=%d variant=%d request=%d",q.kind,q.window,q.metric,q.variant,q.request_id);
}
void opsdeck_opsview_select(int kind,int window,int metric,int variant)
{
    if(kind<0||kind>2||window<0||window>3||metric<0||metric>9||variant<0||variant>1)return;
    portENTER_CRITICAL(&mux);query=(opsdeck_opsview_query_t){kind,window,metric,variant,query.request_id==INT_MAX?1:query.request_id+1};
    active=true;memset(&latest,0,sizeof(latest));portEXIT_CRITICAL(&mux);opsdeck_opsview_request_current();
}
static bool parse_points(const cJSON *o,opsdeck_opsview_t *s)
{
    const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"points");if(!cJSON_IsArray(rows))return false;
    int n=cJSON_GetArraySize(rows);if(n<0||n>OPSDECK_OPSVIEW_POINTS)return false;s->point_count=n;
    for(int i=0;i<n;i++){
        const cJSON *row=cJSON_GetArrayItem(rows,i);if(!cJSON_IsArray(row)||cJSON_GetArraySize(row)!=3)return false;
        const cJSON *a=cJSON_GetArrayItem(row,0),*b=cJSON_GetArrayItem(row,1),*c=cJSON_GetArrayItem(row,2);
        if(!cJSON_IsNull(a)){if(!cJSON_IsNumber(a)||!isfinite(a->valuedouble)||fabs(a->valuedouble)>1e9)return false;s->points[i].a_valid=true;s->points[i].a=(float)a->valuedouble;}
        if(!cJSON_IsNull(b)){if(!cJSON_IsNumber(b)||!isfinite(b->valuedouble)||fabs(b->valuedouble)>1e9)return false;s->points[i].b_valid=true;s->points[i].b=(float)b->valuedouble;}
        if(!cJSON_IsNull(c)){if(!cJSON_IsNumber(c)||!isfinite(c->valuedouble)||fabs(c->valuedouble)>1e9)return false;s->points[i].c_valid=true;s->points[i].c=(float)c->valuedouble;}
    }
    return true;
}
static bool parse_alerts(const cJSON *o,opsdeck_opsview_t *s)
{
    const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"items");if(!cJSON_IsArray(rows))return false;
    int n=cJSON_GetArraySize(rows);if(n<0||n>OPSDECK_OPSVIEW_ALERTS||s->count<n)return false;
    for(int i=0;i<n;i++){
        const cJSON *r=cJSON_GetArrayItem(rows,i);if(!cJSON_IsObject(r))return false;opsdeck_ops_alert_t *a=&s->alerts[i];
        if(!integer(r,"level",1,3,&a->level)||!bounded_text(r,"code",a->code,sizeof(a->code),false)||
           !bounded_text(r,"source",a->source,sizeof(a->source),false)||!bounded_text(r,"text",a->text,sizeof(a->text),false))return false;
    }
    return true;
}
static bool parse_events(const cJSON *o,opsdeck_opsview_t *s)
{
    const cJSON *rows=cJSON_GetObjectItemCaseSensitive(o,"events");if(!cJSON_IsArray(rows))return false;
    int n=cJSON_GetArraySize(rows);if(n<0||n>OPSDECK_OPSVIEW_EVENTS||s->count!=n)return false;
    for(int i=0;i<n;i++){
        const cJSON *r=cJSON_GetArrayItem(rows,i);if(!cJSON_IsObject(r))return false;opsdeck_ops_event_t *e=&s->events[i];
        if(!integer(r,"age_s",0,INT_MAX,&e->age_s)||!integer(r,"severity",0,2,&e->severity)||!integer(r,"domain",0,7,&e->domain)||
           !bounded_text(r,"code",e->code,sizeof(e->code),false)||!bounded_text(r,"summary",e->summary,sizeof(e->summary),false))return false;
    }
    return true;
}
bool opsdeck_opsview_accept(const cJSON *o)
{
    if(!cJSON_IsObject(o))return false;
    const cJSON *type=cJSON_GetObjectItemCaseSensitive(o,"type");
    if(!cJSON_IsString(type)||strcmp(type->valuestring,"opsdeck.opsview.v1"))return false;
    opsdeck_opsview_t s={0};if(!integer(o,"kind",0,2,&s.kind)||!integer(o,"request_id",1,INT_MAX,&s.request_id))return false;
    opsdeck_opsview_query_t q;portENTER_CRITICAL(&mux);q=query;bool enabled=active;portEXIT_CRITICAL(&mux);
    if(!enabled||s.kind!=q.kind||s.request_id!=q.request_id)return false;
    if(s.kind==0){
        if(!integer(o,"window",0,3,&s.window)||!integer(o,"metric",0,9,&s.metric)||!integer(o,"variant",0,1,&s.variant)||!integer(o,"volume_count",0,2,&s.volume_count)||!integer(o,"state",0,6,&s.state)||
           !integer(o,"minute_rows",0,10080,&s.minute_rows)||!integer(o,"sample_rows",0,10080,&s.sample_rows)||!integer(o,"expected_minutes",15,10080,&s.expected_minutes)||!integer(o,"bucket_count",0,OPSDECK_OPSVIEW_POINTS,&s.bucket_count)||!integer(o,"covered_buckets",0,OPSDECK_OPSVIEW_POINTS,&s.covered_buckets)||
           !integer(o,"coverage_state",0,2,&s.coverage_state)||!integer(o,"coverage_pct",0,100,&s.coverage_pct)||!integer(o,"last_age_s",-1,INT_MAX,&s.last_age_s)||s.window!=q.window||s.metric!=q.metric||s.variant!=q.variant||s.covered_buckets>s.bucket_count)return false;
        if(!bounded_text(o,"label",s.label,sizeof(s.label),false)||!bounded_text(o,"unit",s.unit,sizeof(s.unit),false)||
           !bounded_text(o,"label2",s.label2,sizeof(s.label2),true)||!bounded_text(o,"unit2",s.unit2,sizeof(s.unit2),true)||
           !bounded_text(o,"label3",s.label3,sizeof(s.label3),true)||!bounded_text(o,"unit3",s.unit3,sizeof(s.unit3),true)||
           !bounded_text(o,"first_local",s.first_local,sizeof(s.first_local),true)||!bounded_text(o,"last_local",s.last_local,sizeof(s.last_local),true))return false;
        if(!nullable_number(o,"min",&s.min_valid,&s.min)||!nullable_number(o,"avg",&s.avg_valid,&s.avg)||!nullable_number(o,"max",&s.max_valid,&s.max)||
           !nullable_number(o,"min2",&s.min2_valid,&s.min2)||!nullable_number(o,"avg2",&s.avg2_valid,&s.avg2)||!nullable_number(o,"max2",&s.max2_valid,&s.max2)||
           !nullable_number(o,"min3",&s.min3_valid,&s.min3)||!nullable_number(o,"avg3",&s.avg3_valid,&s.avg3)||!nullable_number(o,"max3",&s.max3_valid,&s.max3)||!parse_points(o,&s)||s.point_count!=s.bucket_count)return false;
    }else if(s.kind==1){
        if(!integer(o,"level",0,3,&s.level)||!integer(o,"count",0,16,&s.count)||!parse_alerts(o,&s))return false;
    }else{
        if(!integer(o,"count",0,OPSDECK_OPSVIEW_EVENTS,&s.count)||!parse_events(o,&s))return false;
    }
    s.present=true;s.received_us=esp_timer_get_time();
    portENTER_CRITICAL(&mux);s.sequence=++sequence;latest=s;portEXIT_CRITICAL(&mux);
    ESP_LOGI("opsdeck","OPSVIEW_RX kind=%d request=%d metric=%d window=%d count=%d points=%d",s.kind,s.request_id,s.metric,s.window,s.count,s.point_count);
    return true;
}
