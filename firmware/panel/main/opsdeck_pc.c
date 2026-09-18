/* Exact host-frame parser; no UI, drivers, writes, network or RTOS. */
#include "opsdeck_pc.h"
#include <math.h>
#include <string.h>
static bool number(const cJSON *o,const char *name,float lo,float hi,float *out)
{
    const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
    if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi) return false;
    *out=(float)v->valuedouble;return true;
}

static bool integer(const cJSON *o,const char *name,int lo,int hi,int *out){
 const cJSON *v=cJSON_GetObjectItemCaseSensitive(o,name);
 if(!cJSON_IsNumber(v)||!isfinite(v->valuedouble)||v->valuedouble<lo||v->valuedouble>hi||floor(v->valuedouble)!=v->valuedouble)return false;
 *out=(int)v->valuedouble;return true;
}
static bool volume_decode(const cJSON *o,opsdeck_volume_t *v){
 const cJSON *id=cJSON_GetObjectItemCaseSensitive(o,"id");
 if(!cJSON_IsObject(o)||!cJSON_IsString(id)||strlen(id->valuestring)!=2||id->valuestring[0]<'A'||id->valuestring[0]>'Z'||id->valuestring[1]!=':')return false;
 strcpy(v->id,id->valuestring);
 if(!integer(o,"state",0,6,&v->state)||!integer(o,"age_s",-1,604800,&v->age_s))return false;
 const char *names[]={"total_gib","free_gib","available_gib"};double *values[]={&v->total_gib,&v->free_gib,&v->available_gib};int found=0;
 for(int i=0;i<3;i++){
  const cJSON *n=cJSON_GetObjectItemCaseSensitive(o,names[i]);if(!n||cJSON_IsNull(n))continue;
  if(!cJSON_IsNumber(n)||!isfinite(n->valuedouble)||n->valuedouble<0||n->valuedouble>1e9)return false;
  *values[i]=n->valuedouble;found++;
 }
 if(found!=0&&found!=3)return false;
 v->has_value=found==3;
 if(v->has_value){if(v->total_gib<=0||v->free_gib>v->total_gib||v->available_gib>v->free_gib)return false;v->used_pct=100*(v->total_gib-v->free_gib)/v->total_gib;}
 if(v->state==1&&(!v->has_value||v->age_s<0||v->age_s>30))return false;
 return true;
}
bool opsdeck_pc_decode(const cJSON *o,opsdeck_pc_t *s){
 if(!cJSON_IsObject(o)||!s)return false;
 memset(s,0,sizeof(*s));
 s->cpu_sensor=4;s->chassis_sensor=4;s->fan_sensor=4;
    if(number(o,"intel_gpu",0,100,&s->intel_gpu))s->valid|=PC_INTEL;
    if(number(o,"intel_temp",0,150,&s->intel_temp))s->valid|=PC_INTEL_TEMP;
    if(number(o,"intel_shared_gib",0,4096,&s->intel_shared_gib)&&number(o,"intel_shared_limit_gib",0.01f,4096,&s->intel_shared_limit_gib)&&s->intel_shared_gib<=s->intel_shared_limit_gib)s->valid|=PC_INTEL_SHARED;
    if(number(o,"fan1_rpm",0,30000,&s->fan1_rpm))s->valid|=PC_FAN1;
    if(number(o,"fan2_rpm",0,30000,&s->fan2_rpm))s->valid|=PC_FAN2;
    if(number(o,"cpu",0,100,&s->cpu)) s->valid|=PC_CPU;
    if(number(o,"gpu",0,100,&s->gpu)) s->valid|=PC_GPU;
    if(number(o,"gpu_temp",0,150,&s->gpu_temp)) s->valid|=PC_GPU_TEMP;
    if(number(o,"ram_used_gib",0,4096,&s->ram_used_gib)&&number(o,"ram_total_gib",0.01f,4096,&s->ram_total_gib)&&s->ram_used_gib<=s->ram_total_gib){
        s->ram_pct=100*s->ram_used_gib/s->ram_total_gib;s->valid|=PC_RAM;
    }
    if(number(o,"vram_used_gib",0,4096,&s->vram_used_gib)&&number(o,"vram_total_gib",0.01f,4096,&s->vram_total_gib)&&s->vram_used_gib<=s->vram_total_gib){
        s->vram_pct=100*s->vram_used_gib/s->vram_total_gib;s->valid|=PC_VRAM;
    }
    if(number(o,"rx_mbps",0,1000000,&s->rx_mbps)&&number(o,"tx_mbps",0,1000000,&s->tx_mbps))s->valid|=PC_NETWORK;

 if(cJSON_HasObjectItem(o,"cpu_sensor")&&!integer(o,"cpu_sensor",0,6,&s->cpu_sensor))return false;
 const cJSON *temp=cJSON_GetObjectItemCaseSensitive(o,"cpu_temp");
 if(temp&&!cJSON_IsNull(temp)){
  if(s->cpu_sensor!=1||!number(o,"cpu_temp",0,150,&s->cpu_temp))return false;
  s->valid|=PC_CPU_TEMP;
 }else if(s->cpu_sensor==1)return false;
 if(cJSON_HasObjectItem(o,"chassis_sensor")&&!integer(o,"chassis_sensor",0,6,&s->chassis_sensor))return false;
 const cJSON *chassis=cJSON_GetObjectItemCaseSensitive(o,"chassis_temp");
 if(chassis&&!cJSON_IsNull(chassis)){
  if(s->chassis_sensor!=1||!number(o,"chassis_temp",0,150,&s->chassis_temp))return false;
  s->valid|=PC_CHASSIS_TEMP;
 }else if(s->chassis_sensor==1)return false;
 s->fan_sensor=(s->valid&(PC_FAN1|PC_FAN2))?1:4;
 if(cJSON_HasObjectItem(o,"fan_sensor")&&!integer(o,"fan_sensor",0,6,&s->fan_sensor))return false;
 if(s->fan_sensor==1&&!(s->valid&(PC_FAN1|PC_FAN2)))return false;
 if(s->fan_sensor!=1&&(s->valid&(PC_FAN1|PC_FAN2)))return false;
 const char *fan_control_names[]={"fan_control_supported","fan_manual_supported","fan_control_mode","fan_control_busy"};int fan_control_present=0;
 for(int i=0;i<4;i++)if(cJSON_HasObjectItem(o,fan_control_names[i]))fan_control_present++;
 if(fan_control_present!=0&&fan_control_present!=4)return false;
 if(fan_control_present==4){
  if(!integer(o,"fan_control_supported",0,1,&s->fan_control_supported)||!integer(o,"fan_manual_supported",0,1,&s->fan_manual_supported)||!integer(o,"fan_control_mode",0,3,&s->fan_control_mode)||!integer(o,"fan_control_busy",0,1,&s->fan_control_busy))return false;
  if(!s->fan_control_supported&&(s->fan_manual_supported||s->fan_control_mode!=0||s->fan_control_busy))return false;
  s->valid|=PC_FAN_CONTROL;
 }
 const cJSON *volumes=cJSON_GetObjectItemCaseSensitive(o,"volumes");
 if(volumes){
  if(!cJSON_IsArray(volumes)||!integer(o,"volume_count",0,26,&s->volume_count))return false;
  s->shown_volumes=cJSON_GetArraySize(volumes);
  if(s->shown_volumes>2||s->shown_volumes>s->volume_count||s->shown_volumes!=(s->volume_count>2?2:s->volume_count))return false;
  for(int i=0;i<s->shown_volumes;i++){
   if(!volume_decode(cJSON_GetArrayItem(volumes,i),&s->volumes[i]))return false;
   if(i&&strcmp(s->volumes[0].id,s->volumes[i].id)==0)return false;
  }
 }else if(cJSON_HasObjectItem(o,"volume_count"))return false;
 return s->valid!=0||s->shown_volumes>0;
}
