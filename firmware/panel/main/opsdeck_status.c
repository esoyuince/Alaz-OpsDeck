/* Bounded read-only provider snapshots. No commands, credentials or raw logs. */
#include "opsdeck_status.h"
#include <math.h>
#include <string.h>
#include <inttypes.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_status_t latest;
static bool integer(const cJSON *o,const char *n,int lo,int hi,int *out){
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,n);
 if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;
 *out=(int)v->valuedouble;return true;
}
static bool optional_number(const cJSON *o,const char *n,double *out,bool *has){
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,n);*has=false;
 if(!v||cJSON_IsNull(v))return true;
 if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<0||v->valuedouble>1e16)return false;
 *out=v->valuedouble;*has=true;return true;
}
static bool optional_text(const cJSON *o,const char *name,char *out,size_t capacity,bool date){
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);out[0]=0;
 if(!v||cJSON_IsNull(v))return true;
 if(!cJSON_IsString(v)||strlen(v->valuestring)>=capacity)return false;
 const char *s=v->valuestring;
 for(const char *p=s;*p;p++)if((unsigned char)*p<32||(unsigned char)*p>126)return false;
 if(date){
  if(strlen(s)!=10)return false;
  for(int i=0;i<10;i++){if(i==4||i==7){if(s[i]!='-')return false;}else if(s[i]<'0'||s[i]>'9')return false;}
  int year=(s[0]-'0')*1000+(s[1]-'0')*100+(s[2]-'0')*10+s[3]-'0';
  int month=(s[5]-'0')*10+s[6]-'0',day=(s[8]-'0')*10+s[9]-'0';
  const int days[]={31,28,31,30,31,30,31,31,30,31,30,31};
  if(year<1||month<1||month>12||day<1)return false;
  int max=days[month-1]+(month==2&&(year%4==0&&(year%100!=0||year%400==0)));
  if(day>max)return false;
 }
 strcpy(out,s);return true;
}
static bool optional_reset(const cJSON *o,const char *name,char *out,size_t cap){
 if(!optional_text(o,name,out,cap,false))return false;
 if(!out[0])return true;
 if(strlen(out)!=11)return false;
 for(int i=0;i<11;i++){if(i==2){if(out[i]!='.')return false;}else if(i==5){if(out[i]!=' ')return false;}else if(i==8){if(out[i]!=':')return false;}else if(out[i]<'0'||out[i]>'9')return false;}
 int day=(out[0]-'0')*10+out[1]-'0',month=(out[3]-'0')*10+out[4]-'0',hour=(out[6]-'0')*10+out[7]-'0',minute=(out[9]-'0')*10+out[10]-'0';
 return day>=1&&day<=31&&month>=1&&month<=12&&hour<=23&&minute<=59;
}
static bool task_event_valid(const char *s){
 const char *allowed[]={"created","context","claimed","runner_claimed","status","result","runner_approved","e2e_requeued"};
 for(size_t i=0;i<sizeof(allowed)/sizeof(allowed[0]);i++)if(!strcmp(s,allowed[i]))return true;
 return false;
}
bool opsdeck_metric_parse(const cJSON *o,opsdeck_metric_t *m){
 if(!cJSON_IsObject(o)||!integer(o,"state",0,6,&m->state)||!integer(o,"age_s",-1,604800,&m->age_s))return false;
 if(!optional_number(o,"value",&m->value,&m->has_value)||!optional_number(o,"secondary",&m->secondary,&m->has_secondary))return false;
 const cJSON *u=cJSON_GetObjectItemCaseSensitive(o,"unit");
 if(!cJSON_IsString(u)||strlen(u->valuestring)>16)return false;
 for(const char *p=u->valuestring;*p;p++)if((unsigned char)*p<32||(unsigned char)*p>126)return false;
 strcpy(m->unit,u->valuestring);m->covered=m->state==1?1:0;m->expected=1;
 if(cJSON_HasObjectItem(o,"covered")&&!integer(o,"covered",0,2,&m->covered))return false;
 if(cJSON_HasObjectItem(o,"expected")&&!integer(o,"expected",1,2,&m->expected))return false;
 if(m->covered>m->expected)return false;
 if(!optional_text(o,"note",m->note,sizeof(m->note),false)||
    !optional_text(o,"period_start",m->period_start,sizeof(m->period_start),true)||
    !optional_text(o,"period_end",m->period_end,sizeof(m->period_end),true)||
    !optional_text(o,"source_end",m->source_end,sizeof(m->source_end),true))return false;
 if(m->period_start[0]&&m->period_end[0]&&strcmp(m->period_end,m->period_start)<=0)return false;
 return true;
}
bool opsdeck_status_accept(const cJSON *o){
 opsdeck_status_t s={0};const cJSON *a=cJSON_GetObjectItemCaseSensitive(o,"agents"),*l=cJSON_GetObjectItemCaseSensitive(o,"locked");
 if(!cJSON_IsObject(a)||!cJSON_IsBool(l)||!integer(a,"codex_count",-1,128,&s.codex_count)||!integer(a,"codex_state",0,6,&s.codex_state)||!integer(a,"bridge_state",0,6,&s.bridge_state))return false;
 s.rdc_state=0;s.rdc_process_count=s.rdc_total_calls=s.rdc_sessions=s.rdc_success_permille=s.rdc_last_action_age_s=-1;
 const char *rdc_names[]={"rdc_state","rdc_process_count","rdc_total_calls","rdc_sessions","rdc_success_permille","rdc_last_action_age_s"};
 int rdc_fields=0;for(int i=0;i<6;i++)if(cJSON_HasObjectItem(a,rdc_names[i]))rdc_fields++;if(rdc_fields!=0&&rdc_fields!=6)return false;
 if(rdc_fields==6){s.rdc_present=true;if(!integer(a,"rdc_state",0,6,&s.rdc_state)||!integer(a,"rdc_process_count",-1,16,&s.rdc_process_count)||!integer(a,"rdc_total_calls",-1,2000000000,&s.rdc_total_calls)||!integer(a,"rdc_sessions",-1,10000000,&s.rdc_sessions)||!integer(a,"rdc_success_permille",-1,1000,&s.rdc_success_permille)||!integer(a,"rdc_last_action_age_s",-1,604800,&s.rdc_last_action_age_s))return false;}
 s.managed_codex_state=0;s.managed_codex_owned=-1;const char *managed_names[]={"managed_codex_state","managed_codex_owned","managed_codex_running","managed_codex_runtime","managed_codex_reconcile"};
 int managed_fields=0;for(int i=0;i<5;i++)if(cJSON_HasObjectItem(a,managed_names[i]))managed_fields++;if(managed_fields!=0&&managed_fields!=5)return false;
 if(managed_fields==5){const cJSON *running=cJSON_GetObjectItemCaseSensitive(a,"managed_codex_running"),*runtime=cJSON_GetObjectItemCaseSensitive(a,"managed_codex_runtime"),*reconcile=cJSON_GetObjectItemCaseSensitive(a,"managed_codex_reconcile");if(!integer(a,"managed_codex_state",0,6,&s.managed_codex_state)||!integer(a,"managed_codex_owned",-1,1024,&s.managed_codex_owned)||!cJSON_IsBool(running)||!cJSON_IsBool(runtime)||!cJSON_IsBool(reconcile))return false;s.managed_codex_present=true;s.managed_codex_running=cJSON_IsTrue(running);s.managed_codex_runtime=cJSON_IsTrue(runtime);s.managed_codex_reconcile=cJSON_IsTrue(reconcile);}
 s.codex_usage_state=0;s.codex_usage_age_s=s.codex_quota_used=s.codex_quota_remaining=s.codex_spark_used=s.codex_spark_remaining=-1;s.codex_quota_window_m=s.codex_spark_window_m=0;
 const char *quota_names[]={"codex_usage_state","codex_usage_age_s","codex_quota_used","codex_quota_remaining","codex_quota_window_m","codex_spark_used","codex_spark_remaining","codex_spark_window_m"};
 int quota_fields=0;for(int i=0;i<8;i++)if(cJSON_HasObjectItem(a,quota_names[i]))quota_fields++;
 if(quota_fields!=0&&quota_fields!=8)return false;
 if(quota_fields==8){
  s.codex_usage_present=true;
  if(!integer(a,"codex_usage_state",0,6,&s.codex_usage_state)||!integer(a,"codex_usage_age_s",-1,604800,&s.codex_usage_age_s)||
     !integer(a,"codex_quota_used",-1,100,&s.codex_quota_used)||!integer(a,"codex_quota_remaining",-1,100,&s.codex_quota_remaining)||!integer(a,"codex_quota_window_m",0,525600,&s.codex_quota_window_m)||
     !integer(a,"codex_spark_used",-1,100,&s.codex_spark_used)||!integer(a,"codex_spark_remaining",-1,100,&s.codex_spark_remaining)||!integer(a,"codex_spark_window_m",0,525600,&s.codex_spark_window_m))return false;
  if((s.codex_quota_used<0)!=(s.codex_quota_remaining<0)||(s.codex_quota_used>=0&&s.codex_quota_used+s.codex_quota_remaining!=100))return false;
  if((s.codex_spark_used<0)!=(s.codex_spark_remaining<0)||(s.codex_spark_used>=0&&s.codex_spark_used+s.codex_spark_remaining!=100))return false;
  if(!optional_reset(a,"codex_quota_reset_local",s.codex_quota_reset,sizeof(s.codex_quota_reset))||!optional_reset(a,"codex_spark_reset_local",s.codex_spark_reset,sizeof(s.codex_spark_reset)))return false;
 }
 s.task_state=0;s.task_open=s.task_claimed=s.task_in_progress=s.task_needs_approval=s.task_blocked=s.task_expired_claims=s.task_latest_age_s=-1;s.task_latest_event[0]=0;
 const char *task_names[]={"task_state","task_open","task_claimed","task_in_progress","task_needs_approval","task_blocked","task_expired_claims","task_latest_age_s"};
 int task_fields=0;for(int i=0;i<8;i++)if(cJSON_HasObjectItem(a,task_names[i]))task_fields++;
 if(task_fields!=0&&task_fields!=8)return false;
 if(task_fields==8){
  s.task_present=true;
  if(!integer(a,"task_state",0,6,&s.task_state)||!integer(a,"task_open",-1,1000000,&s.task_open)||!integer(a,"task_claimed",-1,1000000,&s.task_claimed)||
     !integer(a,"task_in_progress",-1,1000000,&s.task_in_progress)||!integer(a,"task_needs_approval",-1,1000000,&s.task_needs_approval)||
     !integer(a,"task_blocked",-1,1000000,&s.task_blocked)||!integer(a,"task_expired_claims",-1,1000000,&s.task_expired_claims)||
     !integer(a,"task_latest_age_s",-1,604800,&s.task_latest_age_s)||!optional_text(a,"task_latest_event",s.task_latest_event,sizeof(s.task_latest_event),false))return false;
  if(s.task_latest_event[0]&&!task_event_valid(s.task_latest_event))return false;
 } else if(cJSON_HasObjectItem(a,"task_latest_event")&&!cJSON_IsNull(cJSON_GetObjectItemCaseSensitive(a,"task_latest_event")))return false;
 s.locked=cJSON_IsTrue(l);if(s.locked){s.codex_count=-1;s.codex_state=4;s.bridge_state=4;s.rdc_state=4;s.rdc_process_count=s.rdc_total_calls=s.rdc_sessions=s.rdc_success_permille=s.rdc_last_action_age_s=-1;s.managed_codex_state=4;s.managed_codex_owned=-1;s.managed_codex_running=s.managed_codex_runtime=s.managed_codex_reconcile=false;s.codex_usage_state=4;s.codex_usage_age_s=s.codex_quota_used=s.codex_quota_remaining=s.codex_spark_used=s.codex_spark_remaining=-1;s.codex_quota_window_m=s.codex_spark_window_m=0;s.codex_quota_reset[0]=s.codex_spark_reset[0]=0;s.task_state=4;s.task_open=s.task_claimed=s.task_in_progress=s.task_needs_approval=s.task_blocked=s.task_expired_claims=s.task_latest_age_s=-1;s.task_latest_event[0]=0;}
 s.link_state=0;s.link_forward_age_s=s.link_recovery_age_s=-1;
 const cJSON *link=cJSON_GetObjectItemCaseSensitive(o,"link");
 if(link){
  const cJSON *recovering=cJSON_GetObjectItemCaseSensitive(link,"recovering");
  if(!cJSON_IsObject(link)||!cJSON_IsBool(recovering)||!integer(link,"state",0,6,&s.link_state)||!integer(link,"reopen_count",0,1000000,&s.link_reopen_count)||
     !integer(link,"rom_probe_count",0,1000000,&s.link_rom_probe_count)||!integer(link,"recovered_count",0,1000000,&s.link_recovered_count)||
     !integer(link,"last_action",0,2,&s.link_last_action)||!integer(link,"last_reason",0,2,&s.link_last_reason)||
     !integer(link,"host_uptime_s",0,2678400,&s.link_host_uptime_s)||!integer(link,"forward_age_s",-1,604800,&s.link_forward_age_s)||
     !integer(link,"recovery_age_s",-1,604800,&s.link_recovery_age_s))return false;
  s.link_present=true;s.link_recovering=cJSON_IsTrue(recovering);
 }
 const char *names[]={"workers","d1","r2","hosting","cost"};
 for(int i=0;i<5;i++)if(!opsdeck_metric_parse(cJSON_GetObjectItemCaseSensitive(o,names[i]),&s.metrics[i]))return false;
 if(!opsdeck_cost_valid(&s.metrics[4]))return false;
 s.received_us=esp_timer_get_time();portENTER_CRITICAL(&mux);s.sequence=latest.sequence+1;latest=s;portEXIT_CRITICAL(&mux);
 if(s.sequence==1||s.sequence%5==0)ESP_LOGI("opsdeck","STATUS_RX sequence=%"PRIu32" codex=%d bridge=%d rdc=%d rdc_proc=%d managed=%d managed_run=%d quota=%d spark=%d task=%d running=%d approval=%d link=%d recovering=%d locked=%d cost_state=%d cost_value=%d",s.sequence,s.codex_count,s.bridge_state,s.rdc_state,s.rdc_process_count,s.managed_codex_state,s.managed_codex_running,s.codex_quota_used,s.codex_spark_used,s.task_state,s.task_in_progress,s.task_needs_approval,s.link_state,s.link_recovering,s.locked,s.metrics[4].state,s.metrics[4].has_value);
 return true;
}
void opsdeck_status_copy(opsdeck_status_t *s){portENTER_CRITICAL(&mux);*s=latest;portEXIT_CRITICAL(&mux);}

bool opsdeck_cost_valid(const opsdeck_metric_t *m){
 if(m->has_secondary&&(!m->has_value||m->secondary>m->value))return false;
 if(m->has_value){if(strlen(m->unit)!=3)return false;for(int i=0;i<3;i++)if(m->unit[i]<'A'||m->unit[i]>'Z')return false;}
 return true;
}
