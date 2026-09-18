/* ALAZ OPSDECK: native LVGL telemetry plus explicit, approval-gated Deck agent controls. */
#include "opsdeck_ui.h"
#include "opsdeck_status.h"
#include "opsdeck_task_format.h"
#include "opsdeck_cloud.h"
#include "opsdeck_money.h"
#include "opsdeck_inventory_ui.h"
#include "opsdeck_details_ui.h"
#include "opsdeck_agent_ui.h"
#include "opsdeck_codex_session_ui.h"
#include "opsdeck_process_ui.h"
#include "opsdeck_screen_ui.h"
#include "opsdeck_font.h"
#include "opsdeck_sd.h"
#include "opsdeck_wifi.h"
#include <math.h>
#include <inttypes.h>
#include <stdio.h>
#include <string.h>
#include "esp_lvgl_port.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "esp_psram.h"
#include "freertos/FreeRTOS.h"
static portMUX_TYPE pc_mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_pc_t pc;
static opsdeck_board_t hw;
static lv_obj_t *body,*uptime,*badge,*top_brand,*home_btn,*settings_transport,*settings_wifi;
static lv_obj_t *cpu_arc,*gpu_arc,*cpu_value,*gpu_value,*gpu_temp;
static lv_obj_t *intel_arc,*intel_value,*chassis_temp,*intel_shared,*fan_text;
static lv_obj_t *cpu_temp,*disk_text,*disk_detail[2],*disk_scope,*billing_period,*billing_source,*hosting_scope;
static lv_obj_t *cloud_buttons[3],*cloud_button_labels[3],*cloud_scope,*cloud_health_badge,*cloud_account_health[5];
static int current_page,cloud_selection;
static void show_page(int page);
static void draw_network(void);
static void top_home_event(lv_event_t *e);
static lv_obj_t *ram_bar,*vram_bar,*ram_text,*vram_text,*network,*chart,*overview_net_chart,*device_heap;
static lv_obj_t *ram_arc,*vram_arc,*shared_arc,*ram_gauge_value,*vram_gauge_value,*shared_gauge_value;
static lv_obj_t *fan1_arc,*fan2_arc,*disk_arc,*fan1_value,*fan2_value,*disk_gauge_value,*fan1_rpm_text,*fan2_rpm_text;
static lv_chart_series_t *rx_series,*tx_series,*overview_rx_series,*overview_tx_series;
static uint32_t last_sequence=UINT32_MAX,touch_count;
static int chart_ceiling=10;
static lv_obj_t *agent_value[3],*agent_note[3],*codex_quota_bar,*codex_quota_text,*codex_quota_reset_text,*codex_spark_bar,*codex_spark_text,*codex_spark_reset_text,*task_summary,*task_detail,*task_detail2,*task_age,*link_state_text,*link_counts,*link_last,*settings_sd,*cloud_value[5],*cloud_note[5],*provider_summary;
static int32_t history_rx[60],history_tx[60];static unsigned history_count,history_head;
static uint32_t history_sequence=UINT32_MAX;static int64_t history_last_us;
static const char *state_name(int s){const char *n[]={"SETUP","OK","ERROR","STALE","NO DATA","PARTIAL","DENIED"};return n[(s>=0&&s<=6)?s:4];}
static uint32_t state_color(int s){return s==1?0x73E0A9:((s==2||s==6)?0xFF9977:0x8293AD);}
static lv_obj_t *chart_scale;
#define BG 0x090F1B
#define CARD 0x121E30
#define EDGE 0x23354C
#define INK 0xEDF5FF
#define MUTED 0x8293AD
#define CYAN 0x44D7EE
#define PURPLE 0xB39AFF
#define GREEN 0x73E0A9
#define AMBER 0xFFD166
#define RED 0xFF5C5C
static uint32_t cloud_state_color(int s){return s==1?GREEN:((s==2||s==6)?RED:((s==3||s==5)?AMBER:MUTED));}
static void cloud_age_text(char *out,size_t n,int age){
 if(age<0)snprintf(out,n,"--");else if(age<60)snprintf(out,n,"%ds",age);else if(age<3600)snprintf(out,n,"%dm",age/60);else snprintf(out,n,"%dh",age/3600);
}
static int cloud_effective_state(const opsdeck_metric_t *m,bool present,int cloud_age,int ttl){
 int state=!present?4:(cloud_age>=16?3:m->state);
 if((state==1||(state==5&&m->has_value))&&(m->age_s<0||m->age_s+cloud_age>ttl))state=3;
 return state;
}

static lv_color_t col(uint32_t c){return lv_color_hex(c);}
static uint32_t load_color(float v,uint32_t normal){return v>=85.0f?RED:(v>=70.0f?AMBER:normal);}
static uint32_t temp_color(float v,float amber_at,float red_at){return v>=red_at?RED:(v>=amber_at?AMBER:GREEN);}
void opsdeck_pc_publish(const opsdeck_pc_t *s){portENTER_CRITICAL(&pc_mux);pc=*s;portEXIT_CRITICAL(&pc_mux);}
void opsdeck_pc_copy(opsdeck_pc_t *s){portENTER_CRITICAL(&pc_mux);*s=pc;portEXIT_CRITICAL(&pc_mux);}
static lv_obj_t *text(lv_obj_t *parent,int x,int y,const char *s,const lv_font_t *f,uint32_t color)
{
    lv_obj_t *o=lv_label_create(parent);lv_label_set_text(o,s);lv_obj_set_pos(o,x,y);
    lv_obj_set_style_text_font(o,f,0);lv_obj_set_style_text_color(o,col(color),0);return o;
}
static lv_obj_t *card(lv_obj_t *parent,int x,int y,int w,int h)
{
    lv_obj_t *o=lv_obj_create(parent);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);
    lv_obj_set_style_bg_color(o,col(CARD),0);lv_obj_set_style_border_color(o,col(EDGE),0);
    lv_obj_set_style_border_width(o,1,0);lv_obj_set_style_radius(o,18,0);
    lv_obj_set_style_pad_all(o,0,0);lv_obj_remove_flag(o,LV_OBJ_FLAG_SCROLLABLE);return o;
}
static lv_obj_t *gauge(lv_obj_t *parent,int x,int y,int size,const char *name,uint32_t color,lv_obj_t **value)
{
    lv_obj_t *a=lv_arc_create(parent);lv_obj_set_pos(a,x,y);lv_obj_set_size(a,size,size);
    lv_arc_set_bg_angles(a,135,45);lv_arc_set_range(a,0,100);lv_arc_set_value(a,0);
    lv_obj_set_style_arc_width(a,9,LV_PART_MAIN);lv_obj_set_style_arc_width(a,9,LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(a,col(EDGE),LV_PART_MAIN);lv_obj_set_style_arc_color(a,col(color),LV_PART_INDICATOR);
    lv_obj_remove_style(a,NULL,LV_PART_KNOB);lv_obj_remove_flag(a,LV_OBJ_FLAG_CLICKABLE);
    lv_obj_t *n=text(parent,0,0,name,&lv_font_montserrat_14,MUTED);
    lv_obj_align_to(n,a,LV_ALIGN_CENTER,0,-16);
    *value=text(parent,0,0,"--",&lv_font_montserrat_24,INK);
    lv_obj_align_to(*value,a,LV_ALIGN_CENTER,0,6);return a;
}
static lv_obj_t *compact_gauge(lv_obj_t *parent,int x,int y,int size,const char *name,uint32_t color,lv_obj_t **value)
{
    lv_obj_t *a=lv_arc_create(parent);lv_obj_set_pos(a,x,y);lv_obj_set_size(a,size,size);
    lv_arc_set_bg_angles(a,135,45);lv_arc_set_range(a,0,100);lv_arc_set_value(a,0);
    lv_obj_set_style_arc_width(a,7,LV_PART_MAIN);lv_obj_set_style_arc_width(a,7,LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(a,col(EDGE),LV_PART_MAIN);lv_obj_set_style_arc_color(a,col(color),LV_PART_INDICATOR);
    lv_obj_remove_style(a,NULL,LV_PART_KNOB);lv_obj_remove_flag(a,LV_OBJ_FLAG_CLICKABLE);
    lv_obj_t *n=text(parent,0,0,name,&lv_font_montserrat_12,MUTED);lv_obj_align_to(n,a,LV_ALIGN_CENTER,0,-15);
    *value=text(parent,0,0,"--",&lv_font_montserrat_20,INK);lv_obj_align_to(*value,a,LV_ALIGN_CENTER,0,5);return a;
}
static lv_obj_t *mini_gauge(lv_obj_t *parent,int x,int y,int size,const char *name,uint32_t color,lv_obj_t **value)
{
    lv_obj_t *a=lv_arc_create(parent);lv_obj_set_pos(a,x,y);lv_obj_set_size(a,size,size);
    lv_arc_set_bg_angles(a,135,45);lv_arc_set_range(a,0,100);lv_arc_set_value(a,0);
    lv_obj_set_style_arc_width(a,7,LV_PART_MAIN);lv_obj_set_style_arc_width(a,7,LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(a,col(EDGE),LV_PART_MAIN);lv_obj_set_style_arc_color(a,col(color),LV_PART_INDICATOR);
    lv_obj_remove_style(a,NULL,LV_PART_KNOB);lv_obj_remove_flag(a,LV_OBJ_FLAG_CLICKABLE);
    lv_obj_t *n=text(parent,0,0,name,&lv_font_montserrat_12,MUTED);lv_obj_align_to(n,a,LV_ALIGN_CENTER,0,-14);
    *value=text(parent,0,0,"--",&lv_font_montserrat_16,INK);lv_obj_align_to(*value,a,LV_ALIGN_CENTER,0,4);return a;
}
static void overview_page_event(lv_event_t *e)
{
    int page=(int)(uintptr_t)lv_event_get_user_data(e);touch_count++;
    ESP_LOGI("opsdeck.ui","OVERVIEW_SHORTCUT page=%d touch_events=%"PRIu32,page,touch_count);show_page(page);
}
static void top_home_event(lv_event_t *e)
{
    (void)e;touch_count++;show_page(0);
}
static void update_top_navigation(void)
{
    if(top_brand){if(current_page==0)lv_obj_remove_flag(top_brand,LV_OBJ_FLAG_HIDDEN);else lv_obj_add_flag(top_brand,LV_OBJ_FLAG_HIDDEN);}
    if(home_btn){if(current_page==0)lv_obj_add_flag(home_btn,LV_OBJ_FLAG_HIDDEN);else lv_obj_remove_flag(home_btn,LV_OBJ_FLAG_HIDDEN);}
}
static lv_obj_t *overview_shortcut_button(lv_obj_t *parent,int x,int y,int w,const char *caption,int page,uint32_t accent)
{
    lv_obj_t *b=lv_button_create(parent);lv_obj_set_pos(b,x,y);lv_obj_set_size(b,w,42);
    lv_obj_set_style_bg_color(b,col(EDGE),0);lv_obj_set_style_border_color(b,col(accent),0);lv_obj_set_style_border_width(b,1,0);lv_obj_set_style_radius(b,10,0);lv_obj_set_style_shadow_width(b,0,0);
    lv_obj_t *l=text(b,0,0,caption,&lv_font_montserrat_12,INK);lv_obj_center(l);lv_obj_add_event_cb(b,overview_page_event,LV_EVENT_CLICKED,(void*)(uintptr_t)page);return b;
}
static void overview_make_shortcut(lv_obj_t *o,int page,uint32_t accent)
{
    lv_obj_add_flag(o,LV_OBJ_FLAG_CLICKABLE);lv_obj_set_style_border_color(o,col(accent),LV_STATE_PRESSED);
    lv_obj_set_style_border_width(o,2,LV_STATE_PRESSED);lv_obj_add_event_cb(o,overview_page_event,LV_EVENT_CLICKED,(void*)(uintptr_t)page);
}
static void screen_event(lv_event_t *e)
{
    touch_count++;opsdeck_screen_ui_open((opsdeck_screen_mode_t)(uintptr_t)lv_event_get_user_data(e));
}
static void screen_shortcut(lv_obj_t *o,opsdeck_screen_mode_t target,uint32_t accent)
{
    lv_obj_add_flag(o,LV_OBJ_FLAG_CLICKABLE);lv_obj_set_style_border_color(o,col(accent),LV_STATE_PRESSED);lv_obj_set_style_border_width(o,2,LV_STATE_PRESSED);lv_obj_add_event_cb(o,screen_event,LV_EVENT_CLICKED,(void*)(uintptr_t)target);
}
static lv_obj_t *screen_button(lv_obj_t *parent,int x,int y,int w,const char *caption,opsdeck_screen_mode_t target,uint32_t accent)
{
    lv_obj_t *b=lv_button_create(parent);lv_obj_set_pos(b,x,y);lv_obj_set_size(b,w,36);lv_obj_set_style_bg_color(b,col(EDGE),0);lv_obj_set_style_border_color(b,col(accent),0);lv_obj_set_style_border_width(b,1,0);lv_obj_set_style_radius(b,9,0);lv_obj_set_style_shadow_width(b,0,0);lv_obj_t *l=text(b,0,0,caption,&lv_font_montserrat_12,INK);lv_obj_center(l);lv_obj_add_event_cb(b,screen_event,LV_EVENT_CLICKED,(void*)(uintptr_t)target);return b;
}
static lv_obj_t *meter(lv_obj_t *parent,int x,int y,int w,uint32_t color)
{
    lv_obj_t *o=lv_bar_create(parent);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,8);
    lv_obj_set_style_bg_color(o,col(EDGE),LV_PART_MAIN);lv_obj_set_style_bg_color(o,col(color),LV_PART_INDICATOR);
    lv_bar_set_range(o,0,100);lv_bar_set_value(o,0,LV_ANIM_OFF);return o;
}
static void pc_panel(lv_obj_t *parent,int x,int y,int w,int h,bool detailed)
{
    lv_obj_t *o=card(parent,x,y,w,h);
    if(!detailed){overview_make_shortcut(o,1,CYAN);text(o,w-28,14,LV_SYMBOL_RIGHT,&lv_font_montserrat_16,CYAN);}
    text(o,16,14,detailed?LV_SYMBOL_IMAGE "  PC / DUAL GPU":LV_SYMBOL_IMAGE "  HP OMEN 16",&lv_font_montserrat_16,INK);
    cpu_arc=gauge(o,12,40,90,"CPU",CYAN,&cpu_value);
    intel_arc=gauge(o,111,40,90,"INTEL",GREEN,&intel_value);
    gpu_arc=gauge(o,210,40,90,"NVIDIA",PURPLE,&gpu_value);
    cpu_temp=text(o,22,131,"CPU -- C",&lv_font_montserrat_12,MUTED);
    chassis_temp=text(o,108,131,"CHASSIS -- C",&lv_font_montserrat_12,MUTED);
    gpu_temp=text(o,221,131,"NVIDIA -- C",&lv_font_montserrat_12,MUTED);
    if(!detailed){
        screen_shortcut(cpu_arc,OPS_SCREEN_CPU,CYAN);screen_shortcut(intel_arc,OPS_SCREEN_INTEL,GREEN);screen_shortcut(gpu_arc,OPS_SCREEN_NVIDIA,PURPLE);
        screen_shortcut(cpu_temp,OPS_SCREEN_THERMALS,CYAN);screen_shortcut(chassis_temp,OPS_SCREEN_THERMALS,GREEN);screen_shortcut(gpu_temp,OPS_SCREEN_THERMALS,PURPLE);
        ram_arc=compact_gauge(o,12,154,82,"RAM",CYAN,&ram_gauge_value);
        vram_arc=compact_gauge(o,210,154,82,"VRAM",PURPLE,&vram_gauge_value);
        shared_arc=compact_gauge(o,111,154,82,"SHARED",GREEN,&shared_gauge_value);
        lv_obj_align_to(ram_gauge_value,ram_arc,LV_ALIGN_CENTER,-10,5);
        lv_obj_align_to(shared_gauge_value,shared_arc,LV_ALIGN_CENTER,-10,5);
        lv_obj_align_to(vram_gauge_value,vram_arc,LV_ALIGN_CENTER,-5,5);
        screen_shortcut(ram_arc,OPS_SCREEN_RAM,CYAN);screen_shortcut(vram_arc,OPS_SCREEN_VRAM,PURPLE);screen_shortcut(shared_arc,OPS_SCREEN_SHARED,GREEN);
        ram_text=text(o,8,237,"--/--G",&lv_font_montserrat_12,MUTED);lv_obj_set_width(ram_text,90);lv_obj_set_style_text_align(ram_text,LV_TEXT_ALIGN_CENTER,0);
        vram_text=text(o,206,237,"--/--G",&lv_font_montserrat_12,MUTED);lv_obj_set_width(vram_text,90);lv_obj_set_style_text_align(vram_text,LV_TEXT_ALIGN_CENTER,0);
        intel_shared=text(o,107,237,"--/--G",&lv_font_montserrat_12,MUTED);lv_obj_set_width(intel_shared,90);lv_obj_set_style_text_align(intel_shared,LV_TEXT_ALIGN_CENTER,0);
        fan1_arc=mini_gauge(o,18,260,70,"FAN1",GREEN,&fan1_value);
        fan2_arc=mini_gauge(o,123,260,70,"FAN2",GREEN,&fan2_value);
        disk_arc=mini_gauge(o,218,260,70,"DISK",CYAN,&disk_gauge_value);
        lv_obj_align_to(fan1_value,fan1_arc,LV_ALIGN_CENTER,-10,4);
        lv_obj_align_to(fan2_value,fan2_arc,LV_ALIGN_CENTER,-10,4);
        lv_obj_align_to(disk_gauge_value,disk_arc,LV_ALIGN_CENTER,-10,4);
        screen_shortcut(fan1_arc,OPS_SCREEN_FANS,GREEN);screen_shortcut(fan2_arc,OPS_SCREEN_FANS,GREEN);screen_shortcut(disk_arc,OPS_SCREEN_STORAGE,CYAN);
        fan1_rpm_text=text(o,11,331,"-- RPM",&lv_font_montserrat_12,MUTED);lv_obj_set_width(fan1_rpm_text,90);lv_obj_set_style_text_align(fan1_rpm_text,LV_TEXT_ALIGN_CENTER,0);
        fan2_rpm_text=text(o,110,331,"-- RPM",&lv_font_montserrat_12,MUTED);lv_obj_set_width(fan2_rpm_text,90);lv_obj_set_style_text_align(fan2_rpm_text,LV_TEXT_ALIGN_CENTER,0);
        disk_text=text(o,199,331,"--G free",&lv_font_montserrat_12,MUTED);lv_obj_set_width(disk_text,96);lv_obj_set_style_text_align(disk_text,LV_TEXT_ALIGN_CENTER,0);
        network=text(o,16,348,"NET RX -- / TX -- Mb/s",&lv_font_montserrat_12,MUTED);lv_obj_set_width(network,284);lv_label_set_long_mode(network,LV_LABEL_LONG_DOT);
        overview_net_chart=lv_chart_create(o);lv_obj_set_pos(overview_net_chart,16,367);lv_obj_set_size(overview_net_chart,284,39);
        lv_obj_set_style_bg_opa(overview_net_chart,LV_OPA_TRANSP,0);lv_obj_set_style_border_width(overview_net_chart,0,0);lv_obj_set_style_line_color(overview_net_chart,col(EDGE),LV_PART_MAIN);lv_obj_set_style_line_opa(overview_net_chart,LV_OPA_30,LV_PART_MAIN);lv_obj_set_style_size(overview_net_chart,0,0,LV_PART_INDICATOR);
        lv_chart_set_type(overview_net_chart,LV_CHART_TYPE_LINE);lv_chart_set_point_count(overview_net_chart,60);lv_chart_set_range(overview_net_chart,LV_CHART_AXIS_PRIMARY_Y,0,10);overview_rx_series=lv_chart_add_series(overview_net_chart,col(CYAN),LV_CHART_AXIS_PRIMARY_Y);overview_tx_series=lv_chart_add_series(overview_net_chart,col(PURPLE),LV_CHART_AXIS_PRIMARY_Y);screen_shortcut(overview_net_chart,OPS_SCREEN_NETWORK,CYAN);draw_network();
    }else{
        text(o,16,178,"RAM",&lv_font_montserrat_14,MUTED);ram_text=text(o,92,178,"-- / -- GiB",&lv_font_montserrat_14,INK);ram_bar=meter(o,16,198,w-32,CYAN);
        text(o,16,216,"NV MEM",&lv_font_montserrat_14,MUTED);vram_text=text(o,92,216,"-- / -- GiB",&lv_font_montserrat_14,INK);vram_bar=meter(o,16,236,w-32,PURPLE);
        intel_shared=text(o,16,254,"Intel shared: -- GiB",&lv_font_montserrat_14,MUTED);
        fan_text=text(o,16,275,"Fan 1: --   Fan 2: -- RPM",&lv_font_montserrat_14,MUTED);
        disk_text=text(o,16,297,"Volume: no data",&lv_font_montserrat_14,MUTED);lv_obj_set_width(disk_text,w-32);lv_label_set_long_mode(disk_text,LV_LABEL_LONG_DOT);
        network=text(o,16,320,"NET -- / -- Mb/s",&lv_font_montserrat_14,MUTED);
    }
    (void)detailed;
}
static void codex_session_event(lv_event_t *e){(void)e;touch_count++;opsdeck_codex_session_ui_open();}
static void process_manager_event(lv_event_t *e){(void)e;touch_count++;opsdeck_process_ui_open();}
static void agent_panel(lv_obj_t *parent,int x,int y,int w,int h,bool detailed)
{
 lv_obj_t *o=card(parent,x,y,w,h);if(!detailed){overview_make_shortcut(o,2,CYAN);text(o,w-28,14,LV_SYMBOL_RIGHT,&lv_font_montserrat_16,CYAN);}
 text(o,16,14,LV_SYMBOL_SETTINGS "  AGENTS",&lv_font_montserrat_16,INK);
 const char *names[]={"ChatGPT","Codex","RDC"};const char *icons[]={LV_SYMBOL_USB,LV_SYMBOL_EDIT,LV_SYMBOL_WIFI};const uint32_t colors[]={CYAN,PURPLE,GREEN};
 int row_h=detailed?48:58,row_step=detailed?54:62;
 for(int i=0;i<3;i++){
  lv_obj_t *a=card(o,12,44+i*row_step,w-24,row_h);text(a,9,7,icons[i],&lv_font_montserrat_16,colors[i]);
  text(a,32,7,names[i],&lv_font_montserrat_14,INK);
  agent_value[i]=text(a,9,detailed?25:((i==2)?24:31),"--",detailed?&lv_font_montserrat_16:&lv_font_montserrat_14,MUTED);
  agent_note[i]=text(a,detailed?145:((i==2)?9:94),detailed?26:((i==2)?41:31),"Waiting",&lv_font_montserrat_12,MUTED);
  lv_obj_set_width(agent_note[i],detailed?w-170:((i==2)?w-48:w-112));lv_label_set_long_mode(agent_note[i],LV_LABEL_LONG_DOT);
  if(i==1){lv_obj_add_flag(a,LV_OBJ_FLAG_CLICKABLE);lv_obj_add_event_cb(a,codex_session_event,LV_EVENT_CLICKED,NULL);lv_obj_set_style_border_color(a,col(PURPLE),0);}
  else if(!detailed)overview_make_shortcut(a,2,colors[i]);
  if(!detailed)text(a,w-52,7,LV_SYMBOL_RIGHT,&lv_font_montserrat_12,colors[i]);
 }
 int qy=detailed?210:246;
 text(o,16,qy,"WORK + CODEX",&lv_font_montserrat_12,MUTED);
 codex_quota_bar=meter(o,16,qy+19,w-32,PURPLE);
 codex_quota_text=text(o,16,qy+32,"Quota --",&lv_font_montserrat_12,MUTED);
 codex_quota_reset_text=text(o,16,qy+47,"Next reset --",&lv_font_montserrat_12,MUTED);
 if(detailed){text(o,16,276,"CODEX SPARK",&lv_font_montserrat_12,MUTED);codex_spark_bar=meter(o,16,295,w-32,CYAN);codex_spark_text=text(o,16,308,"Quota --",&lv_font_montserrat_12,MUTED);codex_spark_reset_text=text(o,16,323,"Next reset --",&lv_font_montserrat_12,MUTED);}
}
static void cloud_panel(lv_obj_t *parent,int x,int y,int w,int h,bool interactive)
{
 lv_obj_t *o=card(parent,x,y,w,h);if(interactive){overview_make_shortcut(o,3,CYAN);text(o,w-28,14,LV_SYMBOL_RIGHT,&lv_font_montserrat_16,CYAN);}
 text(o,16,14,interactive?LV_SYMBOL_WIFI "  CLOUD":LV_SYMBOL_WIFI "  CLOUDFLARE",&lv_font_montserrat_16,INK);
 cloud_health_badge=text(o,interactive?w-101:w-94,16,"SETUP",&lv_font_montserrat_12,MUTED);
 lv_obj_set_width(cloud_health_badge,interactive?72:78);lv_obj_set_style_text_align(cloud_health_badge,LV_TEXT_ALIGN_RIGHT,0);
 const char *names[]={"Workers","D1","R2","Hosting"};
 const char *icons[]={LV_SYMBOL_CHARGE,LV_SYMBOL_LIST,LV_SYMBOL_DRIVE,LV_SYMBOL_WIFI};
 for(int i=0;i<4;i++){
  int cw=(w-36)/2;lv_obj_t *m=card(o,12+(i%2)*(cw+12),49+(i/2)*95,cw,87);
  if(interactive){overview_make_shortcut(m,3,i==0?CYAN:(i==1?GREEN:(i==2?PURPLE:CYAN)));text(m,cw-21,8,LV_SYMBOL_RIGHT,&lv_font_montserrat_12,CYAN);}
  text(m,8,7,icons[i],&lv_font_montserrat_16,MUTED);text(m,30,8,names[i],&lv_font_montserrat_14,INK);
  cloud_value[i]=text(m,8,31,"--",&lv_font_montserrat_20,INK);
  cloud_note[i]=text(m,8,64,"SETUP",&lv_font_montserrat_12,MUTED);lv_obj_set_width(cloud_note[i],cw-16);lv_label_set_long_mode(cloud_note[i],LV_LABEL_LONG_DOT);
 }
 text(o,16,250,"COST SNAPSHOT",&lv_font_montserrat_14,MUTED);
 cloud_value[4]=text(o,16,274,"--",&lv_font_montserrat_20,INK);
 cloud_note[4]=text(o,16,304,"SETUP",&lv_font_montserrat_12,MUTED);
 lv_obj_set_width(cloud_value[4],w-32);lv_label_set_long_mode(cloud_value[4],LV_LABEL_LONG_DOT);
 lv_obj_set_width(cloud_note[4],w-32);lv_label_set_long_mode(cloud_note[4],LV_LABEL_LONG_WRAP);
 if(interactive){overview_shortcut_button(o,12,354,94,"PROJECTS",4,PURPLE);overview_shortcut_button(o,120,354,94,"DETAILS",5,CYAN);}
}
static void compact_value(char *b,size_t n,double v){
 if(v>=1e9)snprintf(b,n,"%.1fB",v/1e9);else if(v>=1e6)snprintf(b,n,"%.1fM",v/1e6);
 else if(v>=1e3)snprintf(b,n,"%.1fK",v/1e3);else snprintf(b,n,"%.1f",v);
}
static void task_status_long(char *b,size_t n,const opsdeck_status_t *s,bool fresh){
 char c[16];
 if(!fresh){snprintf(b,n,"TASK METADATA STALE");return;}
 if(s->locked){snprintf(b,n,"TASK METADATA LOCKED");return;}
 if(!s->task_present){snprintf(b,n,"TASK METADATA SETUP");return;}
 if(s->task_state==1){
  if(s->task_needs_approval>0){opsdeck_task_count_text(c,sizeof(c),s->task_needs_approval);snprintf(b,n,"TASK NEEDS APPROVAL %s",c);}
  else if(s->task_blocked>0){opsdeck_task_count_text(c,sizeof(c),s->task_blocked);snprintf(b,n,"TASK BLOCKED %s",c);}
  else if(s->task_in_progress>0){opsdeck_task_count_text(c,sizeof(c),s->task_in_progress);snprintf(b,n,"TASK RUNNING %s",c);}
  else if(s->task_claimed>0){opsdeck_task_count_text(c,sizeof(c),s->task_claimed);snprintf(b,n,"TASK CLAIMED %s",c);}
  else if(s->task_open>0){opsdeck_task_count_text(c,sizeof(c),s->task_open);snprintf(b,n,"TASK OPEN %s",c);}
  else snprintf(b,n,"TASK IDLE");
 } else snprintf(b,n,"TASK %s",state_name(s->task_state));
}
static uint32_t task_status_color(const opsdeck_status_t *s,bool fresh){
 if(!fresh||s->locked||!s->task_present)return MUTED;
 if(s->task_state!=1)return state_color(s->task_state);
 if(s->task_needs_approval>0)return AMBER;
 if(s->task_blocked>0)return 0xFF9977;
 if(s->task_in_progress>0)return GREEN;
 if(s->task_claimed>0||s->task_open>0)return CYAN;
 return MUTED;
}
static const char *link_action_name(int v){const char *n[]={"NONE","REOPEN","ROM PROBE"};return n[(v>=0&&v<=2)?v:0];}
static const char *link_reason_name(int v){const char *n[]={"NONE","DEVICE SILENT","FORWARD STALL"};return n[(v>=0&&v<=2)?v:0];}
static void refresh_providers(int64_t now){
 opsdeck_status_t s;opsdeck_status_copy(&s);int age=s.received_us?(int)((now-s.received_us)/1000000):999999;
 bool fresh=s.received_us&&age<16;char b[128];
 int st[3]={s.bridge_state,s.codex_state,s.rdc_present?s.rdc_state:0};
 for(int i=0;i<3;i++)if(agent_value[i]){
  int state=!s.received_us?4:(!fresh?3:st[i]);uint32_t color=state_color(state);
  if(s.locked&&fresh)snprintf(b,sizeof(b),"LOCKED");
  else if(i==0&&state==1)snprintf(b,sizeof(b),"MCP OK");
  else if(i==1&&s.managed_codex_present&&fresh){
   if(s.managed_codex_reconcile){snprintf(b,sizeof(b),"RECONCILE");color=AMBER;}
   else if(s.managed_codex_running){snprintf(b,sizeof(b),"RUNNING");color=GREEN;}
   else if(s.managed_codex_runtime){snprintf(b,sizeof(b),"READY");color=GREEN;}
   else if(state==1)snprintf(b,sizeof(b),s.codex_count>=0?"PROC %d":"PROC --",s.codex_count);else snprintf(b,sizeof(b),"%s",state_name(state));
  }
  else if(i==1&&state==1)snprintf(b,sizeof(b),s.codex_count>=0?"PROC %d":"PROC --",s.codex_count);
  else if(i==2&&fresh&&s.rdc_present&&s.rdc_process_count>0&&(state==1||state==5)){snprintf(b,sizeof(b),"LOCAL ACTIVE");color=GREEN;}
  else snprintf(b,sizeof(b),"%s",state_name(state));
  lv_label_set_text(agent_value[i],b);lv_obj_set_style_text_color(agent_value[i],col(color),0);
  if(i==0)snprintf(b,sizeof(b),"task meta");
  else if(i==1)snprintf(b,sizeof(b),s.managed_codex_present&&s.managed_codex_owned>=0?"Owned %d": "Local process",s.managed_codex_owned);
  else if(s.rdc_present&&s.rdc_total_calls>=0&&s.rdc_sessions>=0){char calls[20];compact_value(calls,sizeof(calls),(double)s.rdc_total_calls);snprintf(b,sizeof(b),"%s / %d sess",calls,s.rdc_sessions);}
  else snprintf(b,sizeof(b),"Remote process");
  lv_label_set_text(agent_note[i],b);lv_obj_set_style_text_color(agent_note[i],col(color),0);
 }
 int qstate=!fresh?3:(s.locked?4:(s.codex_usage_present?s.codex_usage_state:0));
 if(codex_quota_bar){
  if(s.codex_usage_present&&s.codex_quota_used>=0&&s.codex_quota_remaining>=0){
   uint32_t qc=qstate==1?load_color((float)s.codex_quota_used,PURPLE):MUTED;
   lv_bar_set_value(codex_quota_bar,s.codex_quota_used,LV_ANIM_ON);lv_obj_set_style_bg_color(codex_quota_bar,col(qc),LV_PART_INDICATOR);
   snprintf(b,sizeof(b),"%d%% used | %d%% left",s.codex_quota_used,s.codex_quota_remaining);lv_label_set_text(codex_quota_text,b);lv_obj_set_style_text_color(codex_quota_text,col(qc),0);
  }else{lv_bar_set_value(codex_quota_bar,0,LV_ANIM_OFF);lv_label_set_text(codex_quota_text,"Quota --");lv_obj_set_style_text_color(codex_quota_text,col(MUTED),0);}
 }
 if(codex_quota_reset_text){snprintf(b,sizeof(b),"Next reset %s",fresh&&s.codex_usage_present&&s.codex_quota_reset[0]?s.codex_quota_reset:"--");lv_label_set_text(codex_quota_reset_text,b);lv_obj_set_style_text_color(codex_quota_reset_text,col(fresh&&s.codex_quota_reset[0]?PURPLE:MUTED),0);}
 if(codex_spark_bar){
  if(s.codex_usage_present&&s.codex_spark_used>=0&&s.codex_spark_remaining>=0){
   uint32_t qc=qstate==1?load_color((float)s.codex_spark_used,CYAN):MUTED;
   lv_bar_set_value(codex_spark_bar,s.codex_spark_used,LV_ANIM_ON);lv_obj_set_style_bg_color(codex_spark_bar,col(qc),LV_PART_INDICATOR);
   snprintf(b,sizeof(b),"%d%% used | %d%% left",s.codex_spark_used,s.codex_spark_remaining);lv_label_set_text(codex_spark_text,b);lv_obj_set_style_text_color(codex_spark_text,col(qc),0);
  }else{lv_bar_set_value(codex_spark_bar,0,LV_ANIM_OFF);lv_label_set_text(codex_spark_text,"Quota --");lv_obj_set_style_text_color(codex_spark_text,col(MUTED),0);}
 }
 if(codex_spark_reset_text){snprintf(b,sizeof(b),"Next reset %s",fresh&&s.codex_usage_present&&s.codex_spark_reset[0]?s.codex_spark_reset:"--");lv_label_set_text(codex_spark_reset_text,b);lv_obj_set_style_text_color(codex_spark_reset_text,col(fresh&&s.codex_spark_reset[0]?CYAN:MUTED),0);}
 if(task_summary){
  task_status_long(b,sizeof(b),&s,fresh);lv_label_set_text(task_summary,b);lv_obj_set_style_text_color(task_summary,col(task_status_color(&s,fresh)),0);
  if(!fresh||s.locked||!s.task_present||s.task_in_progress<0||s.task_needs_approval<0||s.task_blocked<0||s.task_open<0||s.task_claimed<0||s.task_expired_claims<0){
   lv_label_set_text(task_detail,"ACTIVE -- | RUN -- | APP --");lv_label_set_text(task_detail2,"OPEN -- | CLM -- | BLK -- | EXP --");
  } else {
   char active[16],run[16],approval[16],open[16],claim[16],blocked[16],expired[16];
   opsdeck_task_count_text(active,sizeof(active),s.task_claimed+s.task_in_progress+s.task_needs_approval);
   opsdeck_task_count_text(run,sizeof(run),s.task_in_progress);opsdeck_task_count_text(approval,sizeof(approval),s.task_needs_approval);
   opsdeck_task_count_text(open,sizeof(open),s.task_open);opsdeck_task_count_text(claim,sizeof(claim),s.task_claimed);
   opsdeck_task_count_text(blocked,sizeof(blocked),s.task_blocked);opsdeck_task_count_text(expired,sizeof(expired),s.task_expired_claims);
   snprintf(b,sizeof(b),"ACTIVE %s | RUN %s | APP %s",active,run,approval);lv_label_set_text(task_detail,b);
   snprintf(b,sizeof(b),"OPEN %s | CLM %s | BLK %s | EXP %s",open,claim,blocked,expired);lv_label_set_text(task_detail2,b);
  }
  char age_text[16];opsdeck_task_age_text(age_text,sizeof(age_text),fresh&&!s.locked&&s.task_present?s.task_latest_age_s:-1);
  const char *event_label=opsdeck_task_event_label(s.task_latest_event);
  if(strcmp(event_label,"--"))snprintf(b,sizeof(b),"Last %s | age %s",event_label,age_text);else snprintf(b,sizeof(b),"Latest task age: %s",age_text);
  lv_label_set_text(task_age,b);
 }
 if(link_state_text){
  int link_state=!fresh?3:(s.link_present?s.link_state:0);uint32_t link_color=link_state==1?GREEN:(s.link_recovering?AMBER:state_color(link_state));
  if(!fresh)snprintf(b,sizeof(b),"LINK HOST STALE");
  else if(!s.link_present)snprintf(b,sizeof(b),"LINK SETUP");
  else if(s.link_recovering)snprintf(b,sizeof(b),"LINK RECOVERING");
  else if(link_state==1)snprintf(b,sizeof(b),"LINK OK");
  else snprintf(b,sizeof(b),"LINK %s",state_name(link_state));
  lv_label_set_text(link_state_text,b);lv_obj_set_style_text_color(link_state_text,col(link_color),0);
  char reopen[16],probe[16],recovered[16],host_age[16],ack_age[16],recovery_age[16];
  opsdeck_task_count_text(reopen,sizeof(reopen),s.link_present?s.link_reopen_count:-1);opsdeck_task_count_text(probe,sizeof(probe),s.link_present?s.link_rom_probe_count:-1);opsdeck_task_count_text(recovered,sizeof(recovered),s.link_present?s.link_recovered_count:-1);
  opsdeck_task_age_text(host_age,sizeof(host_age),s.link_present?s.link_host_uptime_s:-1);opsdeck_task_age_text(ack_age,sizeof(ack_age),s.link_present?s.link_forward_age_s:-1);opsdeck_task_age_text(recovery_age,sizeof(recovery_age),s.link_present?s.link_recovery_age_s:-1);
  snprintf(b,sizeof(b),"REC %s | REOPEN %s | ROM %s | HOST %s",recovered,reopen,probe,host_age);lv_label_set_text(link_counts,b);
  if(s.link_present&&s.link_last_action>0)snprintf(b,sizeof(b),"ACK %s | LAST %s / %s %s",ack_age,link_action_name(s.link_last_action),link_reason_name(s.link_last_reason),recovery_age);
  else snprintf(b,sizeof(b),"ACK %s | LAST NONE",ack_age);
  lv_label_set_text(link_last,b);
 }
 opsdeck_metric_t metrics[5];memcpy(metrics,s.metrics,sizeof(metrics));
 int cloud_age=age;bool cloud_present=s.received_us>0;bool cloud_enabled=true;
 char scope[48]="ALL + GENERAL HTTPS";
 if(current_page==3&&cloud_selection>0){
  opsdeck_cloud_t a;opsdeck_cloud_copy(cloud_selection-1,&a);memcpy(metrics,a.metrics,sizeof(metrics));
  cloud_present=a.received_us>0;cloud_age=cloud_present?(int)((now-a.received_us)/1000000):999999;cloud_enabled=a.enabled;
  snprintf(scope,sizeof(scope),"%s",cloud_present?a.name:(cloud_selection==1?"VetaKeep":"Other Projects"));
 }
 if(cloud_scope){snprintf(b,sizeof(b),"%s%s",scope,cloud_selection>0&&!cloud_enabled?" / SETUP":"");lv_label_set_text(cloud_scope,b);}
 for(int i=0;i<3;i++)if(cloud_buttons[i]){
  lv_obj_set_style_border_color(cloud_buttons[i],col(i==cloud_selection?CYAN:EDGE),0);
  if(i>0&&cloud_button_labels[i]){opsdeck_cloud_t a;opsdeck_cloud_copy(i-1,&a);if(a.received_us)lv_label_set_text(cloud_button_labels[i],a.name);}
 }
 const int ttl[]={180,180,900,180,7200};const char *units[]={"req/min","rows/5m","GiB","sites","usage"};
 int cloud_states[5];for(int i=0;i<5;i++)cloud_states[i]=cloud_effective_state(&metrics[i],cloud_present,cloud_age,ttl[i]);
 int rollup=cloud_present?1:4;for(int i=0;i<4;i++){int st=cloud_states[i];if(st==2||st==6){rollup=2;break;}if(st==3&&rollup!=2)rollup=3;else if(st==5&&rollup!=2&&rollup!=3)rollup=5;else if((st==0||st==4)&&rollup==1)rollup=4;}
 if(cloud_health_badge){char ca[16];cloud_age_text(ca,sizeof(ca),cloud_present?cloud_age:-1);snprintf(b,sizeof(b),"%s %s",rollup==2?"ISSUE":state_name(rollup),ca);lv_label_set_text(cloud_health_badge,b);lv_obj_set_style_text_color(cloud_health_badge,col(cloud_state_color(rollup)),0);}
 for(int i=0;i<5;i++)if(cloud_value[i]){
  opsdeck_metric_t *m=&metrics[i];int state=cloud_states[i];
  if(m->has_value&&cloud_present){
   if(i==4){char amount[32];opsdeck_money_text(amount,m->value);snprintf(b,sizeof(b),"%.3s %.24s",m->unit,amount);}
   else if(i==3&&m->has_secondary)snprintf(b,sizeof(b),"%.0f/%.0f",m->value,m->secondary);
   else compact_value(b,sizeof(b),m->value);
  }else snprintf(b,sizeof(b),"%s",i==4&&cloud_present&&m->state==4?"NO RECORDS":"--");
  lv_label_set_text(cloud_value[i],b);lv_obj_set_style_text_color(cloud_value[i],col(state==1?INK:cloud_state_color(state)),0);
  if(i==4&&m->note[0]&&cloud_present&&cloud_age<16){
   if(m->has_secondary&&state!=2&&state!=6){char amount[32];opsdeck_money_text(amount,m->secondary);snprintf(b,sizeof(b),"%.40s\nIncl. %.24s %.3s/month",m->note,amount,m->unit);}
   else snprintf(b,sizeof(b),"%s",m->note);
  }
  else if(i==3&&m->note[0]&&cloud_present&&cloud_age<16)snprintf(b,sizeof(b),"%.40s",current_page==3&&cloud_selection>0?"Account":(!strncmp(m->note,"ALL: ",5)?m->note+5:"All URLs"));
  else if(state==5&&m->expected>1){char ca[16];cloud_age_text(ca,sizeof(ca),m->age_s<0?-1:m->age_s+cloud_age);snprintf(b,sizeof(b),"PART %d/%d | %s",m->covered,m->expected,ca);}
  else if(state==1){char ca[16];cloud_age_text(ca,sizeof(ca),m->age_s<0?-1:m->age_s+cloud_age);snprintf(b,sizeof(b),"%s | %s",units[i],ca);}
  else if(state==3){char ca[16];cloud_age_text(ca,sizeof(ca),m->age_s<0?cloud_age:m->age_s+cloud_age);snprintf(b,sizeof(b),"STALE | %s",ca);}
  else snprintf(b,sizeof(b),"%s",state_name(state));
  lv_label_set_text(cloud_note[i],b);
  lv_obj_set_style_text_color(cloud_note[i],col(cloud_state_color(state)),0);
 }
 if(current_page==3){const char *abbr[]={"W","D1","R2","H","$"};for(int i=0;i<5;i++)if(cloud_account_health[i]){int st=cloud_states[i];snprintf(b,sizeof(b),"%s %s",abbr[i],st==1?"OK":st==5?"PART":st==3?"STALE":st==2?"ERR":st==6?"DENY":"--");lv_label_set_text(cloud_account_health[i],b);lv_obj_set_style_text_color(cloud_account_health[i],col(cloud_state_color(st)),0);}}
 if(hosting_scope){snprintf(b,sizeof(b),"HTTPS: %s",cloud_selection>0?"assigned account URLs only":"general + account URLs");lv_label_set_text(hosting_scope,b);}
 if(billing_period){
  opsdeck_metric_t *m=&metrics[4];
  char usage[32],fixed[32];opsdeck_money_text(fixed,m->secondary);opsdeck_money_text(usage,m->value-m->secondary);
  if(m->has_secondary&&m->has_value&&m->covered>0)snprintf(b,sizeof(b),"Usage %.24s + fixed %.24s %.3s/month",usage,fixed,m->unit);
  else if(m->has_secondary)snprintf(b,sizeof(b),"Usage -- | fixed %.24s %.3s/month",fixed,m->unit);
  else snprintf(b,sizeof(b),"Latest usage records / fixed fee not set");
  lv_label_set_text(billing_period,b);
 }
 if(billing_source){opsdeck_metric_t *m=&metrics[4];if(m->source_end[0]&&m->period_start[0]&&m->period_end[0])snprintf(b,sizeof(b),"Source %s | window %s..%s",m->source_end,m->period_start,m->period_end);else if(m->source_end[0])snprintf(b,sizeof(b),"Usage source: %s",m->source_end);else snprintf(b,sizeof(b),"Usage source: no dated record");lv_label_set_text(billing_source,b);}
 if(provider_summary){
  if(current_page==3)snprintf(b,sizeof(b),cloud_present?"Host frame: %d s ago / not a source date":"Waiting for account frames",cloud_present?cloud_age:0);
  else snprintf(b,sizeof(b),"Host updates: %"PRIu32" | Age: %d s",s.sequence,s.received_us?age:0);
  lv_label_set_text(provider_summary,b);
 }
}
static void collect_network(const opsdeck_pc_t *s,bool live){
 if(s->sequence==history_sequence){return;}
 history_sequence=s->sequence;
 if(!live)return;
 if(history_last_us&&s->received_us-history_last_us>5000000){history_rx[history_head]=history_tx[history_head]=LV_CHART_POINT_NONE;history_head=(history_head+1)%60;if(history_count<60)history_count++;}
 history_last_us=s->received_us;bool valid=s->valid&PC_NETWORK;
 history_rx[history_head]=valid?(int32_t)(s->rx_mbps*10):LV_CHART_POINT_NONE;
 history_tx[history_head]=valid?(int32_t)(s->tx_mbps*10):LV_CHART_POINT_NONE;
 history_head=(history_head+1)%60;if(history_count<60)history_count++;
}
static void draw_network(void){
 if(!chart&&!overview_net_chart)return;
 int peak=1;for(unsigned i=0;i<history_count;i++){
  if(history_rx[i]!=LV_CHART_POINT_NONE&&history_rx[i]>peak)peak=history_rx[i];
  if(history_tx[i]!=LV_CHART_POINT_NONE&&history_tx[i]>peak)peak=history_tx[i];}
 int top=10;while(top<peak+peak/5&&top<100000000){if(top*2>=peak+peak/5){top*=2;break;}if(top*5>=peak+peak/5){top*=5;break;}top*=10;}
 chart_ceiling=top;unsigned start=(history_head+60-history_count)%60;
 if(chart){lv_chart_set_range(chart,LV_CHART_AXIS_PRIMARY_Y,0,top);lv_chart_set_all_value(chart,rx_series,LV_CHART_POINT_NONE);lv_chart_set_all_value(chart,tx_series,LV_CHART_POINT_NONE);for(unsigned i=0;i<history_count;i++){unsigned j=(start+i)%60;lv_chart_set_next_value(chart,rx_series,history_rx[j]);lv_chart_set_next_value(chart,tx_series,history_tx[j]);}}
 if(overview_net_chart){lv_chart_set_range(overview_net_chart,LV_CHART_AXIS_PRIMARY_Y,0,top);lv_chart_set_all_value(overview_net_chart,overview_rx_series,LV_CHART_POINT_NONE);lv_chart_set_all_value(overview_net_chart,overview_tx_series,LV_CHART_POINT_NONE);for(unsigned i=0;i<history_count;i++){unsigned j=(start+i)%60;lv_chart_set_next_value(overview_net_chart,overview_rx_series,history_rx[j]);lv_chart_set_next_value(overview_net_chart,overview_tx_series,history_tx[j]);}}
 char b[96];snprintf(b,sizeof(b),"Auto scale: 0 - %.1f Mb/s  |  %u samples",top/10.0,history_count);if(chart_scale)lv_label_set_text(chart_scale,b);
}
static void cloud_select_event(lv_event_t *e)
{
    cloud_selection=(int)(uintptr_t)lv_event_get_user_data(e);touch_count++;
    ESP_LOGI("opsdeck.ui","CLOUD_SELECT slot=%d touch_events=%"PRIu32,cloud_selection,touch_count);
}
static void show_page(int page)
{
    current_page=page;update_top_navigation();opsdeck_inventory_ui_clear();opsdeck_details_ui_clear();opsdeck_agent_ui_clear();
    cpu_temp=disk_text=disk_scope=billing_period=billing_source=hosting_scope=NULL;
    disk_detail[0]=disk_detail[1]=NULL;
    intel_arc=intel_value=chassis_temp=intel_shared=fan_text=cloud_scope=cloud_health_badge=NULL;
    for(int i=0;i<3;i++){cloud_buttons[i]=cloud_button_labels[i]=NULL;}for(int i=0;i<5;i++)cloud_account_health[i]=NULL;
    cpu_arc=gpu_arc=cpu_value=gpu_value=gpu_temp=NULL;
    ram_bar=vram_bar=ram_text=vram_text=network=chart=overview_net_chart=device_heap=NULL;
    ram_arc=vram_arc=shared_arc=ram_gauge_value=vram_gauge_value=shared_gauge_value=NULL;fan1_rpm_text=fan2_rpm_text=NULL;
    fan1_arc=fan2_arc=disk_arc=fan1_value=fan2_value=disk_gauge_value=NULL;
    rx_series=tx_series=overview_rx_series=overview_tx_series=NULL;last_sequence=UINT32_MAX;
    lv_obj_clean(body);chart_scale=NULL;chart_ceiling=10;provider_summary=NULL;codex_quota_bar=codex_quota_text=codex_quota_reset_text=codex_spark_bar=codex_spark_text=codex_spark_reset_text=NULL;task_summary=task_detail=task_detail2=task_age=NULL;link_state_text=link_counts=link_last=settings_sd=settings_transport=settings_wifi=NULL;
    for(int i=0;i<3;i++){agent_value[i]=agent_note[i]=NULL;}for(int i=0;i<5;i++){cloud_value[i]=cloud_note[i]=NULL;}
    if(page==0) {
        pc_panel(body,0,0,316,414,false);
        agent_panel(body,328,0,210,414,false);cloud_panel(body,550,0,226,414,true);
    } else if(page==1) {
        pc_panel(body,0,0,316,414,true);
        lv_obj_t *o=card(body,328,0,448,414);
        text(o,18,16,"NETWORK / rolling history",&lv_font_montserrat_16,INK);
        lv_obj_t *tm=lv_button_create(o);lv_obj_set_pos(tm,286,10);lv_obj_set_size(tm,142,32);lv_obj_set_style_bg_color(tm,col(EDGE),0);lv_obj_set_style_border_color(tm,col(CYAN),0);lv_obj_set_style_border_width(tm,1,0);lv_obj_set_style_radius(tm,9,0);lv_obj_set_style_shadow_width(tm,0,0);lv_obj_add_event_cb(tm,process_manager_event,LV_EVENT_CLICKED,NULL);lv_obj_t *tml=text(tm,0,0,"TASK MANAGER",&lv_font_montserrat_12,INK);lv_obj_center(tml);
        chart=lv_chart_create(o);lv_obj_set_pos(chart,18,48);lv_obj_set_size(chart,408,116);
        lv_obj_set_style_bg_color(chart,col(CARD),0);lv_obj_set_style_border_width(chart,0,0);
        lv_obj_set_style_line_color(chart,col(EDGE),LV_PART_MAIN);lv_obj_set_style_line_width(chart,2,LV_PART_ITEMS);
        lv_obj_set_style_size(chart,0,0,LV_PART_INDICATOR);
        lv_chart_set_type(chart,LV_CHART_TYPE_LINE);lv_chart_set_point_count(chart,60);
        lv_chart_set_range(chart,LV_CHART_AXIS_PRIMARY_Y,0,10);
        rx_series=lv_chart_add_series(chart,col(CYAN),LV_CHART_AXIS_PRIMARY_Y);
        tx_series=lv_chart_add_series(chart,col(PURPLE),LV_CHART_AXIS_PRIMARY_Y);
        lv_chart_set_all_value(chart,rx_series,LV_CHART_POINT_NONE);
        lv_chart_set_all_value(chart,tx_series,LV_CHART_POINT_NONE);
        chart_scale=text(o,18,171,"Auto scale / Mb/s",&lv_font_montserrat_14,MUTED);
        text(o,18,193,"RX / Download",&lv_font_montserrat_14,CYAN);text(o,220,193,"TX / Upload",&lv_font_montserrat_14,PURPLE);
        disk_scope=text(o,18,223,"LOCAL VOLUMES",&lv_font_montserrat_14,MUTED);
        for(int i=0;i<2;i++){disk_detail[i]=text(o,18,249+i*38,"Waiting for volume sample",&lv_font_montserrat_14,INK);lv_obj_set_width(disk_detail[i],408);lv_label_set_long_mode(disk_detail[i],LV_LABEL_LONG_WRAP);}
        screen_shortcut(chart,OPS_SCREEN_NETWORK,CYAN);
        screen_button(o,18,340,126,"HISTORY",OPS_SCREEN_HISTORY,CYAN);screen_button(o,151,340,126,"ALERTS",OPS_SCREEN_ALERTS,AMBER);screen_button(o,284,340,142,"TIMELINE",OPS_SCREEN_TIMELINE,PURPLE);
        draw_network();
    } else if(page==2) {
        agent_panel(body,0,0,330,414,true);
        opsdeck_agent_ui_create(body,342,0,434,414);
    } else if(page==3) {
        cloud_panel(body,0,0,330,414,false);
        lv_obj_t *o=card(body,342,0,434,414);
        text(o,18,18,LV_SYMBOL_WIFI "  CLOUDFLARE ACCOUNTS",&lv_font_montserrat_20,INK);
        const char *labels[]={"ALL","VetaKeep","Other Projects"};
        for(int i=0;i<3;i++){
            cloud_buttons[i]=lv_button_create(o);lv_obj_set_pos(cloud_buttons[i],12+i*139,64);lv_obj_set_size(cloud_buttons[i],131,49);
            lv_obj_set_style_bg_color(cloud_buttons[i],col(EDGE),0);lv_obj_set_style_shadow_width(cloud_buttons[i],0,0);
            lv_obj_set_style_border_width(cloud_buttons[i],1,0);
            cloud_button_labels[i]=text(cloud_buttons[i],0,0,labels[i],&lv_font_montserrat_14,INK);
            lv_obj_set_width(cloud_button_labels[i],113);lv_label_set_long_mode(cloud_button_labels[i],LV_LABEL_LONG_DOT);
            lv_obj_set_style_text_align(cloud_button_labels[i],LV_TEXT_ALIGN_CENTER,0);lv_obj_center(cloud_button_labels[i]);
            lv_obj_add_event_cb(cloud_buttons[i],cloud_select_event,LV_EVENT_CLICKED,(void*)(uintptr_t)i);
        }
        cloud_scope=text(o,18,130,"ALL ACCOUNTS",&lv_font_montserrat_20,INK);lv_obj_set_width(cloud_scope,398);lv_label_set_long_mode(cloud_scope,LV_LABEL_LONG_DOT);
        text(o,18,160,"HEALTH",&lv_font_montserrat_12,MUTED);
        for(int i=0;i<5;i++){cloud_account_health[i]=text(o,18+i*78,179,"--",&lv_font_montserrat_12,MUTED);lv_obj_set_width(cloud_account_health[i],72);lv_obj_set_style_text_align(cloud_account_health[i],LV_TEXT_ALIGN_CENTER,0);}
        hosting_scope=text(o,18,211,"Hosting scope",&lv_font_montserrat_14,MUTED);
        billing_period=text(o,18,236,"Usage / monthly fixed fee",&lv_font_montserrat_14,MUTED);
        billing_source=text(o,18,260,"Waiting for usage records",&lv_font_montserrat_12,MUTED);
        lv_obj_set_width(billing_period,398);lv_label_set_long_mode(billing_period,LV_LABEL_LONG_DOT);
        lv_obj_set_width(billing_source,398);lv_label_set_long_mode(billing_source,LV_LABEL_LONG_DOT);
        text(o,18,288,"COST SNAPSHOT / NOT AN INVOICE",&lv_font_montserrat_14,MUTED);
        provider_summary=text(o,18,319,"Waiting for host",&lv_font_montserrat_14,MUTED);
        lv_obj_set_width(provider_summary,398);lv_label_set_long_mode(provider_summary,LV_LABEL_LONG_DOT);
    } else if(page==4) {
        opsdeck_inventory_ui_create(body);
    } else if(page==5) {
        opsdeck_details_ui_create(body);
    } else {
        lv_obj_t *o=card(body,0,0,776,414);text(o,22,14,LV_SYMBOL_SETTINGS "  SETTINGS",&lv_font_montserrat_20,INK);
        lv_obj_t *dev=card(o,16,48,356,116);screen_shortcut(dev,OPS_SCREEN_DEVICE,CYAN);text(dev,326,10,LV_SYMBOL_RIGHT,&lv_font_montserrat_14,CYAN);text(dev,14,12,"DEVICE",&lv_font_montserrat_16,CYAN);text(dev,14,39,"ESP32-S3 | 800 x 480 | RGB565",&lv_font_montserrat_14,INK);text(dev,14,63,"ESP-IDF 5.5.4 | LVGL 9.3.0",&lv_font_montserrat_12,MUTED);text(dev,14,83,hw.touch_ok?"GT911 touch ready":"Touch unavailable",&lv_font_montserrat_12,hw.touch_ok?GREEN:AMBER);device_heap=text(dev,14,99,"Heap: checking",&lv_font_montserrat_12,MUTED);
        lv_obj_t *storage=card(o,388,48,372,116);screen_shortcut(storage,OPS_SCREEN_SD,PURPLE);text(storage,342,10,LV_SYMBOL_RIGHT,&lv_font_montserrat_14,PURPLE);text(storage,14,12,"STORAGE",&lv_font_montserrat_16,PURPLE);settings_sd=text(storage,14,42,"SD: probing / no format",&lv_font_montserrat_12,MUTED);lv_obj_set_width(settings_sd,344);lv_label_set_long_mode(settings_sd,LV_LABEL_LONG_WRAP);text(storage,14,88,"Read-only mount | never auto-format",&lv_font_montserrat_12,MUTED);
        lv_obj_t *link=card(o,16,178,744,122);screen_shortcut(link,OPS_SCREEN_LINK,GREEN);text(link,714,10,LV_SYMBOL_RIGHT,&lv_font_montserrat_14,GREEN);text(link,14,10,"LINK & TRANSPORT",&lv_font_montserrat_16,GREEN);link_state_text=text(link,14,36,"LINK SETUP",&lv_font_montserrat_14,MUTED);link_counts=text(link,14,59,"REC -- | REOPEN -- | ROM -- | HOST --",&lv_font_montserrat_12,MUTED);link_last=text(link,14,79,"ACK -- | LAST NONE",&lv_font_montserrat_12,MUTED);settings_transport=text(link,14,99,"USB telemetry + agent control | waiting for host",&lv_font_montserrat_12,MUTED);lv_obj_t *link_labels[]={link_state_text,link_counts,link_last,settings_transport};for(int i=0;i<4;i++){lv_obj_set_width(link_labels[i],716);lv_label_set_long_mode(link_labels[i],LV_LABEL_LONG_DOT);}
        lv_obj_t *net=card(o,16,314,356,84);screen_shortcut(net,OPS_SCREEN_WIFI,CYAN);text(net,326,8,LV_SYMBOL_RIGHT,&lv_font_montserrat_14,CYAN);text(net,14,10,"NETWORK / WI-FI",&lv_font_montserrat_14,CYAN);settings_wifi=text(net,14,35,"Wi-Fi: checking",&lv_font_montserrat_12,MUTED);lv_obj_set_width(settings_wifi,322);lv_label_set_long_mode(settings_wifi,LV_LABEL_LONG_DOT);text(net,14,56,"USB remains primary/control transport",&lv_font_montserrat_12,INK);
        lv_obj_t *diag=card(o,388,314,372,84);text(diag,14,10,"DIAGNOSTICS",&lv_font_montserrat_14,PURPLE);screen_button(diag,10,38,106,"HISTORY",OPS_SCREEN_HISTORY,CYAN);screen_button(diag,124,38,106,"ALERTS",OPS_SCREEN_ALERTS,AMBER);screen_button(diag,238,38,122,"TIMELINE",OPS_SCREEN_TIMELINE,PURPLE);
    }
    ESP_LOGI("opsdeck.ui","PAGE index=%d touch_events=%"PRIu32,page,touch_count);
}
static void arc_anim(void *o,int32_t v){lv_arc_set_value((lv_obj_t*)o,v);}
static void arc_to(lv_obj_t *o,float target)
{
    int32_t n=(int32_t)(target+0.5f);lv_anim_delete(o,arc_anim);
    lv_anim_t a;lv_anim_init(&a);lv_anim_set_var(&a,o);lv_anim_set_exec_cb(&a,arc_anim);
    lv_anim_set_values(&a,lv_arc_get_value(o),n);lv_anim_set_duration(&a,500);
    lv_anim_set_path_cb(&a,lv_anim_path_ease_out);lv_anim_start(&a);
}
static void refresh(lv_timer_t *t)
{
    (void)t;opsdeck_pc_t s;opsdeck_pc_copy(&s);
    int64_t now=esp_timer_get_time();bool live=s.received_us>0 && now-s.received_us<5000000;
    static int source_state=-1;
    int next_state=live?1:(s.received_us?2:0);
    if(next_state!=source_state){source_state=next_state;
        ESP_LOGI("opsdeck.ui","SOURCE_STATE %s",live?"live":(s.received_us?"stale":"unavailable"));}
    char b[128];uint32_t secs=(uint32_t)(now/1000000);
    snprintf(b,sizeof(b),"UP %02"PRIu32":%02"PRIu32":%02"PRIu32,secs/3600,(secs/60)%60,secs%60);lv_label_set_text(uptime,b);
    opsdeck_wifi_status_t transport;opsdeck_wifi_copy(&transport);bool wifi_live=live&&transport.telemetry_active;
    lv_label_set_text(badge,wifi_live?"WIFI LIVE":(live?"USB LIVE":(s.received_us?"STALE":"NO HOST")));
    lv_obj_set_style_text_color(badge,col(live?GREEN:MUTED),0);
    if(settings_transport){
        if(wifi_live)snprintf(b,sizeof(b),"Wi-Fi telemetry read-only | USB control unavailable | seq %"PRIu32,s.sequence);
        else if(live)snprintf(b,sizeof(b),"USB telemetry + agent control | seq %"PRIu32" | touch %"PRIu32,s.sequence,touch_count);
        else if(s.received_us)snprintf(b,sizeof(b),"USB telemetry stale | last PC sample %"PRId64" s ago",(now-s.received_us)/1000000);
        else snprintf(b,sizeof(b),"USB telemetry + agent control | awaiting PC host");
        lv_label_set_text(settings_transport,b);lv_obj_set_style_text_color(settings_transport,col(live?GREEN:MUTED),0);
    }
    if(device_heap){snprintf(b,sizeof(b),"Free internal: %u KiB   PSRAM: %u KiB   Touch: %"PRIu32,(unsigned)(heap_caps_get_free_size(MALLOC_CAP_INTERNAL)/1024),(unsigned)(heap_caps_get_free_size(MALLOC_CAP_SPIRAM)/1024),touch_count);lv_label_set_text(device_heap,b);}
    if(settings_sd){opsdeck_sd_info_t sd;opsdeck_sd_copy(&sd);
        if(sd.state==OPSDECK_SD_READY&&sd.total_bytes>0)snprintf(b,sizeof(b),"SD READY | %.1f / %.1f GiB free | OpsDeck writes disabled",sd.free_bytes/1073741824.0,sd.total_bytes/1073741824.0);
        else if(sd.state==OPSDECK_SD_READY)snprintf(b,sizeof(b),"SD READY | capacity unavailable | OpsDeck writes disabled");
        else if(sd.state==OPSDECK_SD_SETUP)snprintf(b,sizeof(b),"SD PROBING | no format / no writes");
        else if(sd.state==OPSDECK_SD_UNAVAILABLE)snprintf(b,sizeof(b),"SD UNAVAILABLE | check card / TF switch | no format attempted");
        else snprintf(b,sizeof(b),"SD ERROR | mount failed safely | no format attempted");
        lv_label_set_text(settings_sd,b);lv_obj_set_style_text_color(settings_sd,col(sd.state==OPSDECK_SD_READY?GREEN:MUTED),0);
    }
    if(settings_wifi){opsdeck_wifi_status_t w;opsdeck_wifi_copy(&w);uint32_t wc=w.connected?GREEN:(w.state==OPSDECK_WIFI_CONNECTING?AMBER:MUTED);
        if(w.connected&&w.host_seen_us>0)snprintf(b,sizeof(b),"%s | %s | host %s",w.ssid,w.ip,w.host_name);
        else if(w.connected)snprintf(b,sizeof(b),"%s | %s | host discovery...",w.ssid,w.ip);
        else if(w.configured)snprintf(b,sizeof(b),"%s | connecting / retry %d",w.ssid,w.retry_count);
        else snprintf(b,sizeof(b),"Not configured | tap to scan/setup");
        lv_label_set_text(settings_wifi,b);lv_obj_set_style_text_color(settings_wifi,col(wc),0);
    }
    refresh_providers(now);opsdeck_agent_ui_refresh(now);opsdeck_codex_session_ui_refresh(now);opsdeck_process_ui_refresh(now);opsdeck_screen_ui_refresh(now);collect_network(&s,live);opsdeck_inventory_ui_refresh(now);opsdeck_details_ui_refresh(now);
    if(!cpu_arc) return;
    uint32_t cpu_c=live&&(s.valid&PC_CPU)?load_color(s.cpu,CYAN):MUTED;
    uint32_t intel_c=live&&(s.valid&PC_INTEL)?load_color(s.intel_gpu,GREEN):MUTED;
    uint32_t gpu_c=live&&(s.valid&PC_GPU)?load_color(s.gpu,PURPLE):MUTED;
    uint32_t ram_c=live&&(s.valid&PC_RAM)?load_color(s.ram_pct,CYAN):MUTED;
    uint32_t vram_c=live&&(s.valid&PC_VRAM)?load_color(s.vram_pct,PURPLE):MUTED;
    float shared_pct=(s.valid&PC_INTEL_SHARED)&&s.intel_shared_limit_gib>0.01f?(s.intel_shared_gib*100.0f/s.intel_shared_limit_gib):0.0f;if(shared_pct<0)shared_pct=0;if(shared_pct>100)shared_pct=100;
    uint32_t shared_c=live&&(s.valid&PC_INTEL_SHARED)&&s.intel_shared_limit_gib>0.01f?load_color(shared_pct,GREEN):MUTED;
    lv_obj_set_style_text_color(cpu_value,col(cpu_c),0);lv_obj_set_style_arc_color(cpu_arc,col(cpu_c),LV_PART_INDICATOR);
    lv_obj_set_style_text_color(intel_value,col(intel_c),0);lv_obj_set_style_arc_color(intel_arc,col(intel_c),LV_PART_INDICATOR);
    lv_obj_set_style_text_color(gpu_value,col(gpu_c),0);lv_obj_set_style_arc_color(gpu_arc,col(gpu_c),LV_PART_INDICATOR);
    if(ram_text)lv_obj_set_style_text_color(ram_text,col(ram_c),0);
    if(ram_bar)lv_obj_set_style_bg_color(ram_bar,col(ram_c),LV_PART_INDICATOR);
    if(vram_text)lv_obj_set_style_text_color(vram_text,col(vram_c),0);
    if(vram_bar)lv_obj_set_style_bg_color(vram_bar,col(vram_c),LV_PART_INDICATOR);
    if(ram_arc){lv_obj_set_style_text_color(ram_gauge_value,col(ram_c),0);lv_obj_set_style_arc_color(ram_arc,col(ram_c),LV_PART_INDICATOR);}
    if(vram_arc){lv_obj_set_style_text_color(vram_gauge_value,col(vram_c),0);lv_obj_set_style_arc_color(vram_arc,col(vram_c),LV_PART_INDICATOR);}
    if(shared_arc){lv_obj_set_style_text_color(shared_gauge_value,col(shared_c),0);lv_obj_set_style_arc_color(shared_arc,col(shared_c),LV_PART_INDICATOR);}
    lv_obj_set_style_text_color(cpu_temp,col(live&&(s.valid&PC_CPU_TEMP)?temp_color(s.cpu_temp,75.0f,90.0f):MUTED),0);
    lv_obj_set_style_text_color(chassis_temp,col(live&&(s.valid&PC_CHASSIS_TEMP)?temp_color(s.chassis_temp,55.0f,65.0f):MUTED),0);
    lv_obj_set_style_text_color(gpu_temp,col(live&&(s.valid&PC_GPU_TEMP)?temp_color(s.gpu_temp,75.0f,85.0f):MUTED),0);
    lv_obj_set_style_text_color(intel_shared,col(live&&(s.valid&PC_INTEL_SHARED)?INK:MUTED),0);
    if(fan_text)lv_obj_set_style_text_color(fan_text,col(live&&(s.valid&(PC_FAN1|PC_FAN2))?GREEN:MUTED),0);
    if(fan1_arc){uint32_t c=live&&(s.valid&PC_FAN1)?GREEN:MUTED;lv_obj_set_style_text_color(fan1_value,col(c),0);lv_obj_set_style_arc_color(fan1_arc,col(c),LV_PART_INDICATOR);}
    if(fan2_arc){uint32_t c=live&&(s.valid&PC_FAN2)?GREEN:MUTED;lv_obj_set_style_text_color(fan2_value,col(c),0);lv_obj_set_style_arc_color(fan2_arc,col(c),LV_PART_INDICATOR);}
    if(network)lv_obj_set_style_text_color(network,col(live&&(s.valid&PC_NETWORK)?INK:MUTED),0);
    if(disk_text)lv_obj_set_style_text_color(disk_text,col(live?INK:MUTED),0);
    lv_obj_t *meters[]={cpu_arc,gpu_arc,intel_arc,ram_bar,vram_bar,ram_arc,vram_arc,shared_arc,fan1_arc,fan2_arc,disk_arc};
    for(size_t i=0;i<sizeof(meters)/sizeof(meters[0]);i++)if(meters[i])lv_obj_set_style_opa(meters[i],live?LV_OPA_COVER:LV_OPA_40,0);
    for(int i=0;i<2;i++)if(disk_detail[i]){
        if(i<s.shown_volumes){opsdeck_volume_t *v=&s.volumes[i];
            bool current=live&&v->state==1&&v->age_s>=0&&v->age_s+(now-s.received_us)/1000000<=30;
            if(v->has_value)snprintf(b,sizeof(b),"%s %.1f%% used | %.1f / %.1f GiB free%s",v->id,v->used_pct,v->free_gib,v->total_gib,current?"":" / STALE");
            else snprintf(b,sizeof(b),"%s capacity unavailable",v->id);
            lv_obj_set_style_text_color(disk_detail[i],col(current?INK:MUTED),0);
        }else snprintf(b,sizeof(b),"%s",i==0?"No local volume data":"");
        lv_label_set_text(disk_detail[i],b);
    }
    if(disk_scope){snprintf(b,sizeof(b),"LOCAL VOLUMES: %d/%d shown (not disk type)",s.shown_volumes,s.volume_count);lv_label_set_text(disk_scope,b);}
    if(disk_text){
        if(s.shown_volumes>0&&s.volumes[0].has_value){opsdeck_volume_t *v=&s.volumes[0];bool current=live&&v->state==1&&v->age_s>=0&&v->age_s+(now-s.received_us)/1000000<=30;
            if(disk_arc){snprintf(b,sizeof(b),"%.1fG free",v->free_gib);lv_label_set_text(disk_text,b);uint32_t dc=current?load_color((float)v->used_pct,CYAN):MUTED;lv_obj_set_style_text_color(disk_text,col(dc),0);lv_obj_set_style_text_color(disk_gauge_value,col(dc),0);lv_obj_set_style_arc_color(disk_arc,col(dc),LV_PART_INDICATOR);}
            else{snprintf(b,sizeof(b),"%s %.1f%% | %.1f GiB free",v->id,v->used_pct,v->free_gib);if(!current)lv_obj_set_style_text_color(disk_text,col(MUTED),0);}
        }else{lv_label_set_text(disk_text,disk_arc?"-- GiB free":"Volume capacity unavailable");if(disk_arc){lv_obj_set_style_text_color(disk_gauge_value,col(MUTED),0);lv_obj_set_style_arc_color(disk_arc,col(MUTED),LV_PART_INDICATOR);}}
    }
    if(s.sequence==last_sequence) return;
    last_sequence=s.sequence;
    if(s.valid&PC_CPU){snprintf(b,sizeof(b),"%.0f%%",(double)s.cpu);lv_label_set_text(cpu_value,b);arc_to(cpu_arc,s.cpu);}else {lv_label_set_text(cpu_value,"--");lv_anim_delete(cpu_arc,arc_anim);lv_arc_set_value(cpu_arc,0);}
    if(s.valid&PC_GPU){snprintf(b,sizeof(b),"%.0f%%",(double)s.gpu);lv_label_set_text(gpu_value,b);arc_to(gpu_arc,s.gpu);}else {lv_label_set_text(gpu_value,"--");lv_anim_delete(gpu_arc,arc_anim);lv_arc_set_value(gpu_arc,0);}
    lv_obj_align_to(cpu_value,cpu_arc,LV_ALIGN_CENTER,0,6);lv_obj_align_to(gpu_value,gpu_arc,LV_ALIGN_CENTER,0,6);
    if(s.valid&PC_INTEL){snprintf(b,sizeof(b),"%.0f%%",(double)s.intel_gpu);lv_label_set_text(intel_value,b);arc_to(intel_arc,s.intel_gpu);}else{lv_label_set_text(intel_value,"--");arc_to(intel_arc,0);}
    lv_obj_align_to(intel_value,intel_arc,LV_ALIGN_CENTER,0,6);
    char cpu_t[12]="--",chassis_t[12]="--";
    if(s.valid&PC_CPU_TEMP)snprintf(cpu_t,sizeof(cpu_t),"%.0f",(double)s.cpu_temp);
    if(s.valid&PC_CHASSIS_TEMP)snprintf(chassis_t,sizeof(chassis_t),"%.0f",(double)s.chassis_temp);
    snprintf(b,sizeof(b),"CPU %s C",cpu_t);lv_label_set_text(cpu_temp,b);
    snprintf(b,sizeof(b),"CHASSIS %s C",chassis_t);lv_label_set_text(chassis_temp,b);
    if(s.valid&PC_GPU_TEMP){snprintf(b,sizeof(b),"NVIDIA %.0f C",(double)s.gpu_temp);lv_label_set_text(gpu_temp,b);}else lv_label_set_text(gpu_temp,"NVIDIA -- C");
    if(s.valid&PC_INTEL_SHARED){
        if(shared_arc){snprintf(b,sizeof(b),"%.0f%%",(double)shared_pct);lv_label_set_text(shared_gauge_value,b);arc_to(shared_arc,shared_pct);snprintf(b,sizeof(b),"%.1f/%.1fG",(double)s.intel_shared_gib,(double)s.intel_shared_limit_gib);lv_label_set_text(intel_shared,b);}
        else {snprintf(b,sizeof(b),"Intel shared: %.2f GiB",(double)s.intel_shared_gib);lv_label_set_text(intel_shared,b);}
    }else{if(shared_arc){lv_label_set_text(shared_gauge_value,"--");lv_anim_delete(shared_arc,arc_anim);lv_arc_set_value(shared_arc,0);lv_label_set_text(intel_shared,"-- / -- G");}else lv_label_set_text(intel_shared,"Intel shared: -- GiB");}
    char f1[16]="--",f2[16]="--";float f1_pct=0.0f,f2_pct=0.0f;
    if(s.valid&PC_FAN1){snprintf(f1,sizeof(f1),"%.0f",(double)s.fan1_rpm);f1_pct=s.fan1_rpm*100.0f/5500.0f;if(f1_pct>100)f1_pct=100;}
    if(s.valid&PC_FAN2){snprintf(f2,sizeof(f2),"%.0f",(double)s.fan2_rpm);f2_pct=s.fan2_rpm*100.0f/5500.0f;if(f2_pct>100)f2_pct=100;}
    if(fan1_arc){if(s.valid&PC_FAN1){snprintf(b,sizeof(b),"%.0f%%",(double)f1_pct);lv_label_set_text(fan1_value,b);arc_to(fan1_arc,f1_pct);if(fan1_rpm_text){snprintf(b,sizeof(b),"%s RPM",f1);lv_label_set_text(fan1_rpm_text,b);}}else{lv_label_set_text(fan1_value,"--");if(fan1_rpm_text)lv_label_set_text(fan1_rpm_text,"-- RPM");lv_anim_delete(fan1_arc,arc_anim);lv_arc_set_value(fan1_arc,0);}}
    if(fan2_arc){if(s.valid&PC_FAN2){snprintf(b,sizeof(b),"%.0f%%",(double)f2_pct);lv_label_set_text(fan2_value,b);arc_to(fan2_arc,f2_pct);if(fan2_rpm_text){snprintf(b,sizeof(b),"%s RPM",f2);lv_label_set_text(fan2_rpm_text,b);}}else{lv_label_set_text(fan2_value,"--");if(fan2_rpm_text)lv_label_set_text(fan2_rpm_text,"-- RPM");lv_anim_delete(fan2_arc,arc_anim);lv_arc_set_value(fan2_arc,0);}}
    if(fan_text){if(s.valid&(PC_FAN1|PC_FAN2))snprintf(b,sizeof(b),"Fan 1: %s   Fan 2: %s RPM",f1,f2);else snprintf(b,sizeof(b),"Fans: no verified RPM source");lv_label_set_text(fan_text,b);}
    if(s.valid&PC_RAM){if(ram_arc){snprintf(b,sizeof(b),"%.0f%%",(double)s.ram_pct);lv_label_set_text(ram_gauge_value,b);arc_to(ram_arc,s.ram_pct);snprintf(b,sizeof(b),"%.1f/%.1fG",(double)s.ram_used_gib,(double)s.ram_total_gib);lv_label_set_text(ram_text,b);}else{snprintf(b,sizeof(b),"%.1f / %.1f GiB",(double)s.ram_used_gib,(double)s.ram_total_gib);lv_label_set_text(ram_text,b);if(ram_bar)lv_bar_set_value(ram_bar,(int)s.ram_pct,LV_ANIM_ON);}}else {lv_label_set_text(ram_text,ram_arc?"-- / -- G":"-- / -- GiB");if(ram_arc){lv_label_set_text(ram_gauge_value,"--");lv_anim_delete(ram_arc,arc_anim);lv_arc_set_value(ram_arc,0);}if(ram_bar)lv_bar_set_value(ram_bar,0,LV_ANIM_OFF);}
    if(s.valid&PC_VRAM){if(vram_arc){snprintf(b,sizeof(b),"%.0f%%",(double)s.vram_pct);lv_label_set_text(vram_gauge_value,b);arc_to(vram_arc,s.vram_pct);snprintf(b,sizeof(b),"%.1f/%.1fG",(double)s.vram_used_gib,(double)s.vram_total_gib);lv_label_set_text(vram_text,b);}else{snprintf(b,sizeof(b),"%.1f / %.1f GiB",(double)s.vram_used_gib,(double)s.vram_total_gib);lv_label_set_text(vram_text,b);if(vram_bar)lv_bar_set_value(vram_bar,(int)s.vram_pct,LV_ANIM_ON);}}else {lv_label_set_text(vram_text,vram_arc?"-- / -- G":"-- / -- GiB");if(vram_arc){lv_label_set_text(vram_gauge_value,"--");lv_anim_delete(vram_arc,arc_anim);lv_arc_set_value(vram_arc,0);}if(vram_bar)lv_bar_set_value(vram_bar,0,LV_ANIM_OFF);}
    if(disk_arc){if(s.shown_volumes>0&&s.volumes[0].has_value){snprintf(b,sizeof(b),"%.0f%%",s.volumes[0].used_pct);lv_label_set_text(disk_gauge_value,b);arc_to(disk_arc,(float)s.volumes[0].used_pct);}else{lv_label_set_text(disk_gauge_value,"--");lv_anim_delete(disk_arc,arc_anim);lv_arc_set_value(disk_arc,0);}}
    if(s.valid&PC_NETWORK){if(overview_net_chart)snprintf(b,sizeof(b),"NET  RX %.1f   TX %.1f Mb/s",(double)s.rx_mbps,(double)s.tx_mbps);else snprintf(b,sizeof(b),"NET  %.1f / %.1f Mb/s",(double)s.rx_mbps,(double)s.tx_mbps);lv_label_set_text(network,b);}
    else lv_label_set_text(network,overview_net_chart?"NET  RX --   TX -- Mb/s":"NET  -- / -- Mb/s");
    if(chart||overview_net_chart)draw_network();

}
void opsdeck_ui_init(const opsdeck_board_t *b)
{
    opsdeck_font_init();
    hw=*b;lv_obj_t *s=lv_screen_active();lv_obj_set_style_bg_color(s,col(BG),0);
    lv_obj_remove_flag(s,LV_OBJ_FLAG_SCROLLABLE);
    top_brand=text(s,16,14,"ALAZ OPSDECK",&lv_font_montserrat_24,INK);lv_obj_add_flag(top_brand,LV_OBJ_FLAG_CLICKABLE);lv_obj_add_event_cb(top_brand,overview_page_event,LV_EVENT_CLICKED,(void*)(uintptr_t)6);
    home_btn=lv_button_create(s);lv_obj_set_pos(home_btn,16,10);lv_obj_set_size(home_btn,116,36);lv_obj_set_style_bg_color(home_btn,col(CARD),0);lv_obj_set_style_border_color(home_btn,col(CYAN),0);lv_obj_set_style_border_width(home_btn,1,0);lv_obj_set_style_radius(home_btn,10,0);lv_obj_set_style_shadow_width(home_btn,0,0);lv_obj_add_event_cb(home_btn,top_home_event,LV_EVENT_CLICKED,NULL);lv_obj_t *hl=text(home_btn,0,0,LV_SYMBOL_HOME "  HOME",&lv_font_montserrat_14,INK);lv_obj_center(hl);lv_obj_add_flag(home_btn,LV_OBJ_FLAG_HIDDEN);
    text(s,274,21,"M5.13-H / PROJECTS V2",&lv_font_montserrat_14,MUTED);
    uptime=text(s,508,20,"UP 00:00:00",&lv_font_montserrat_14,MUTED);
    badge=text(s,690,17,"NO HOST",&lv_font_montserrat_16,MUTED);lv_obj_add_flag(badge,LV_OBJ_FLAG_CLICKABLE);lv_obj_add_event_cb(badge,overview_page_event,LV_EVENT_CLICKED,(void*)(uintptr_t)6);
    body=lv_obj_create(s);lv_obj_remove_style_all(body);lv_obj_set_pos(body,12,54);lv_obj_set_size(body,776,414);lv_obj_remove_flag(body,LV_OBJ_FLAG_SCROLLABLE);
    show_page(0);lv_timer_create(refresh,250,NULL);
    ESP_LOGI("opsdeck.ui","UI_READY brand=ALAZ_OPSDECK native_lvgl=9.3.0 size=800x480 controls=enabled session_center=1 tr_keyboard=1");
}
