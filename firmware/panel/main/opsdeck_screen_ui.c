#include "opsdeck_screen_ui.h"
#include "opsdeck_opsview.h"
#include "opsdeck_pc.h"
#include "opsdeck_ui.h"
#include "opsdeck_status.h"
#include "opsdeck_sd.h"
#include "opsdeck_wifi.h"
#include "opsdeck_link_auth.h"
#include "opsdeck_keyboard.h"
#include "esp_timer.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#include <stdio.h>
#include <string.h>
#include <math.h>
#include <limits.h>
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
static lv_obj_t *root,*title,*live_value,*live_note,*age_label,*stats[3],*history_note,*legend_note,*chart,*row[7],*row_title[7],*row_text[7],*window_btn[4],*window_label[4],*metric_button,*metric_label,*variant_btn[2],*variant_label[2];
static lv_obj_t *wifi_state_label,*wifi_ssid_label,*wifi_ip_label,*wifi_host_label,*wifi_scan_btn,*wifi_config_btn,*wifi_connect_btn,*wifi_forget_btn,*wifi_ap_btn[OPSDECK_WIFI_SCAN_MAX],*wifi_ap_label[OPSDECK_WIFI_SCAN_MAX];
static lv_obj_t *wifi_composer,*wifi_ssid_ta,*wifi_pass_ta,*wifi_kb,*wifi_comp_note;static lv_obj_t *fan_control_btn[5],*fan_control_label[5];
static lv_chart_series_t *series_a,*series_b,*series_c;static opsdeck_screen_mode_t mode;static int window_index=1,history_metric=0,history_variant=0;static uint32_t last_ops_sequence=UINT32_MAX,last_live_pc_sequence=UINT32_MAX,last_local_pc_sequence=UINT32_MAX,last_wifi_sequence=UINT32_MAX;static int64_t last_request_us,last_live_second=-1,last_local_second=-1,last_wifi_second=-1;static int last_live_id=-1,last_live_pending=-1,last_local_mode=-1;static int fan_control_request=1,fan_control_pending_mode,fan_control_pending_speed,fan_manual_target=70;static int64_t fan_control_pending_us;
static lv_color_t color(uint32_t v){return lv_color_hex(v);}
static lv_obj_t *label(lv_obj_t *p,int x,int y,int w,const char *s,const lv_font_t *font,uint32_t c)
{
    lv_obj_t *o=lv_label_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,w);lv_label_set_text(o,s);lv_label_set_long_mode(o,LV_LABEL_LONG_DOT);lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(c),0);return o;
}
static lv_obj_t *button(lv_obj_t *p,int x,int y,int w,int h,const char *s,lv_event_cb_t cb,void *data,lv_obj_t **caption)
{
    lv_obj_t *b=lv_button_create(p);lv_obj_set_pos(b,x,y);lv_obj_set_size(b,w,h);lv_obj_set_style_bg_color(b,color(CARD),0);lv_obj_set_style_border_color(b,color(EDGE),0);lv_obj_set_style_border_width(b,1,0);lv_obj_set_style_radius(b,10,0);lv_obj_set_style_shadow_width(b,0,0);lv_obj_set_style_pad_all(b,0,0);lv_obj_t *l=label(b,0,0,w,s,&lv_font_montserrat_12,INK);lv_obj_set_style_text_align(l,LV_TEXT_ALIGN_CENTER,0);lv_obj_center(l);if(caption)*caption=l;lv_obj_add_event_cb(b,cb,LV_EVENT_CLICKED,data);return b;
}
static void wifi_composer_close(void)
{
    if(wifi_composer){lv_obj_delete_async(wifi_composer);wifi_composer=NULL;}wifi_ssid_ta=wifi_pass_ta=wifi_kb=wifi_comp_note=NULL;
}
static void wifi_focus_event(lv_event_t *e)
{
    if(wifi_kb)lv_keyboard_set_textarea(wifi_kb,lv_event_get_target_obj(e));
}
static void wifi_cancel_event(lv_event_t *e){(void)e;wifi_composer_close();}
static void wifi_save_event(lv_event_t *e)
{
    (void)e;if(!wifi_ssid_ta||!wifi_pass_ta)return;const char *ssid=lv_textarea_get_text(wifi_ssid_ta),*pass=lv_textarea_get_text(wifi_pass_ta);
    esp_err_t err=opsdeck_wifi_set_credentials(ssid,pass);if(err==ESP_OK){wifi_composer_close();opsdeck_wifi_connect();return;}
    if(wifi_comp_note){char b[96];snprintf(b,sizeof(b),"SAVE FAILED: %s | WPA password 8-63 chars or empty",esp_err_to_name(err));lv_label_set_text(wifi_comp_note,b);lv_obj_set_style_text_color(wifi_comp_note,color(RED),0);}
}
static void wifi_open_composer(const char *ssid)
{
    if(wifi_composer)return;
    wifi_composer=lv_obj_create(lv_screen_active());lv_obj_set_pos(wifi_composer,0,0);lv_obj_set_size(wifi_composer,800,480);lv_obj_set_style_bg_color(wifi_composer,color(BG),0);lv_obj_set_style_border_width(wifi_composer,0,0);lv_obj_set_style_radius(wifi_composer,0,0);lv_obj_set_style_pad_all(wifi_composer,0,0);lv_obj_remove_flag(wifi_composer,LV_OBJ_FLAG_SCROLLABLE);
    label(wifi_composer,16,10,460,"WI-FI CONFIGURATION",&lv_font_montserrat_20,INK);button(wifi_composer,610,8,82,36,"CANCEL",wifi_cancel_event,NULL,NULL);button(wifi_composer,702,8,82,36,"SAVE",wifi_save_event,NULL,NULL);
    label(wifi_composer,16,52,100,"SSID",&lv_font_montserrat_12,MUTED);wifi_ssid_ta=lv_textarea_create(wifi_composer);lv_obj_set_pos(wifi_ssid_ta,112,46);lv_obj_set_size(wifi_ssid_ta,470,42);lv_textarea_set_one_line(wifi_ssid_ta,true);lv_textarea_set_max_length(wifi_ssid_ta,32);lv_textarea_set_text(wifi_ssid_ta,ssid?ssid:"");
    label(wifi_composer,16,104,100,"PASSWORD",&lv_font_montserrat_12,MUTED);wifi_pass_ta=lv_textarea_create(wifi_composer);lv_obj_set_pos(wifi_pass_ta,112,98);lv_obj_set_size(wifi_pass_ta,470,42);lv_textarea_set_one_line(wifi_pass_ta,true);lv_textarea_set_max_length(wifi_pass_ta,63);lv_textarea_set_password_mode(wifi_pass_ta,true);
    wifi_comp_note=label(wifi_composer,16,146,760,"Password is masked and never logged. NVS at-rest encryption is not enabled yet.",&lv_font_montserrat_12,MUTED);
    wifi_kb=lv_keyboard_create(wifi_composer);lv_obj_set_style_align(wifi_kb,LV_ALIGN_TOP_LEFT,0);lv_obj_set_pos(wifi_kb,0,210);lv_obj_set_size(wifi_kb,800,234);opsdeck_keyboard_attach(wifi_kb,wifi_ssid_ta);lv_obj_move_foreground(wifi_kb);lv_obj_add_event_cb(wifi_ssid_ta,wifi_focus_event,LV_EVENT_FOCUSED,NULL);lv_obj_add_event_cb(wifi_pass_ta,wifi_focus_event,LV_EVENT_FOCUSED,NULL);
    if(ssid&&ssid[0]){lv_keyboard_set_textarea(wifi_kb,wifi_pass_ta);lv_obj_add_state(wifi_pass_ta,LV_STATE_FOCUSED);}
}
static void wifi_scan_event(lv_event_t *e){(void)e;opsdeck_wifi_scan();}
static void wifi_config_event(lv_event_t *e){(void)e;opsdeck_wifi_status_t s;opsdeck_wifi_copy(&s);wifi_open_composer(s.configured?s.ssid:"");}
static void wifi_connect_event(lv_event_t *e){(void)e;opsdeck_wifi_connect();}
static void wifi_forget_event(lv_event_t *e){(void)e;opsdeck_wifi_forget();}
static void wifi_ap_event(lv_event_t *e)
{
    int i=(int)(intptr_t)lv_event_get_user_data(e);opsdeck_wifi_status_t s;opsdeck_wifi_copy(&s);if(i>=0&&i<s.scan_count)wifi_open_composer(s.scan[i].ssid);
}
static const char *window_name(int i){static const char *n[]={"15m","1h","24h","7d"};return n[(i>=0&&i<4)?i:1];}
static int history_id(void)
{
    if(mode==OPS_SCREEN_HISTORY)return history_metric;
    return mode==OPS_SCREEN_CPU?0:mode==OPS_SCREEN_INTEL?1:mode==OPS_SCREEN_NVIDIA?2:mode==OPS_SCREEN_RAM?3:mode==OPS_SCREEN_VRAM?4:mode==OPS_SCREEN_THERMALS?5:mode==OPS_SCREEN_FANS?6:mode==OPS_SCREEN_NETWORK?7:mode==OPS_SCREEN_STORAGE?8:9;
}
static const char *metric_name(int id)
{
    static const char *n[]={"CPU","INTEL GPU","NVIDIA GPU","RAM","VRAM","THERMALS","FANS","NETWORK","STORAGE","INTEL SHARED"};return n[(id>=0&&id<10)?id:0];
}
static bool local_mode(void){return mode>=OPS_SCREEN_DEVICE;}
static const char *screen_title(void)
{
    if(mode==OPS_SCREEN_ALERTS)return "ALERT CENTER";
    if(mode==OPS_SCREEN_TIMELINE)return "EVENT TIMELINE";
    if(mode==OPS_SCREEN_HISTORY)return "HISTORY";
    if(mode==OPS_SCREEN_DEVICE)return "DEVICE STATUS";
    if(mode==OPS_SCREEN_SD)return "SD / STORAGE STATUS";
    if(mode==OPS_SCREEN_LINK)return "LINK & TRANSPORT";
    if(mode==OPS_SCREEN_WIFI)return "NETWORK / WI-FI";
    return metric_name(history_id());
}
static uint32_t alert_color(int level){return level>=3?RED:level==2?CYAN:level==1?AMBER:GREEN;}
static void request_view(void)
{
    if(local_mode()){last_request_us=esp_timer_get_time();last_ops_sequence=UINT32_MAX;return;}
    if(mode==OPS_SCREEN_ALERTS)opsdeck_opsview_select(1,0,0,0);
    else if(mode==OPS_SCREEN_TIMELINE)opsdeck_opsview_select(2,0,0,0);
    else opsdeck_opsview_select(0,window_index,history_id(),history_id()==8?history_variant:0);
    last_request_us=esp_timer_get_time();last_ops_sequence=UINT32_MAX;
}
static void close_event(lv_event_t *e){(void)e;opsdeck_screen_ui_close();}
static void window_event(lv_event_t *e)
{
    window_index=(int)(intptr_t)lv_event_get_user_data(e);request_view();
}
static void metric_event(lv_event_t *e)
{
    int d=(int)(intptr_t)lv_event_get_user_data(e);history_metric=(history_metric+d+10)%10;if(history_metric!=8){history_variant=0;for(int i=0;i<2;i++)if(variant_btn[i])lv_obj_add_flag(variant_btn[i],LV_OBJ_FLAG_HIDDEN);}request_view();
}
static void variant_event(lv_event_t *e)
{
    history_variant=(int)(intptr_t)lv_event_get_user_data(e);request_view();
}
static void fan_control_event(lv_event_t *e)
{
    int action=(int)(intptr_t)lv_event_get_user_data(e);if(mode!=OPS_SCREEN_FANS||action<1||action>5)return;
    opsdeck_pc_t p;opsdeck_pc_copy(&p);int64_t now=esp_timer_get_time();
    bool pending=fan_control_pending_us>0&&now-fan_control_pending_us<15000000;
    if(pending||!(p.valid&PC_FAN_CONTROL)||!p.fan_control_supported||p.fan_control_busy)return;
    int requested_mode=action==1?1:action==2?2:3;
    if(requested_mode==3&&!p.fan_manual_supported)return;
    if(action==3)fan_manual_target=fan_manual_target<=50?50:fan_manual_target-5;
    else if(action==5)fan_manual_target=fan_manual_target>=100?100:fan_manual_target+5;
    const char *name=requested_mode==1?"auto":requested_mode==2?"max":"manual";int request=fan_control_request;
    fan_control_request=fan_control_request==INT_MAX?1:fan_control_request+1;fan_control_pending_us=now;fan_control_pending_mode=requested_mode;fan_control_pending_speed=requested_mode==3?fan_manual_target:0;last_live_pc_sequence=UINT32_MAX;
    if(requested_mode==3)ESP_LOGI("opsdeck.ui","FAN_CONTROL_REQUEST mode=manual speed=%d request=%d",fan_manual_target,request);
    else ESP_LOGI("opsdeck.ui","FAN_CONTROL_REQUEST mode=%s request=%d",name,request);
}
bool opsdeck_screen_ui_is_open(void){return root!=NULL;}
void opsdeck_screen_ui_close(void)
{
    wifi_composer_close();if(root){lv_obj_delete_async(root);root=NULL;}title=live_value=live_note=age_label=history_note=legend_note=chart=metric_button=metric_label=NULL;series_a=series_b=series_c=NULL;for(int i=0;i<2;i++)variant_btn[i]=variant_label[i]=NULL;for(int i=0;i<5;i++)fan_control_btn[i]=fan_control_label[i]=NULL;for(int i=0;i<7;i++)row[i]=row_title[i]=row_text[i]=NULL;last_live_pc_sequence=UINT32_MAX;last_local_pc_sequence=UINT32_MAX;last_wifi_sequence=UINT32_MAX;last_live_second=last_local_second=last_wifi_second=-1;last_live_id=last_live_pending=last_local_mode=-1;
    wifi_state_label=wifi_ssid_label=wifi_ip_label=wifi_host_label=wifi_scan_btn=wifi_config_btn=wifi_connect_btn=wifi_forget_btn=NULL;for(int i=0;i<OPSDECK_WIFI_SCAN_MAX;i++)wifi_ap_btn[i]=wifi_ap_label[i]=NULL;opsdeck_opsview_deactivate();
}
void opsdeck_screen_ui_open(opsdeck_screen_mode_t next)
{
    opsdeck_screen_ui_close();mode=next;root=lv_obj_create(lv_screen_active());lv_obj_set_pos(root,0,0);lv_obj_set_size(root,800,480);lv_obj_set_style_bg_color(root,color(BG),0);lv_obj_set_style_border_width(root,0,0);lv_obj_set_style_radius(root,0,0);lv_obj_set_style_pad_all(root,0,0);lv_obj_remove_flag(root,LV_OBJ_FLAG_SCROLLABLE);
    button(root,12,10,86,38,"BACK",close_event,NULL,NULL);title=label(root,116,12,390,screen_title(),&lv_font_montserrat_20,INK);
    age_label=label(root,610,18,176,"WAITING",&lv_font_montserrat_12,MUTED);lv_obj_set_style_text_align(age_label,LV_TEXT_ALIGN_RIGHT,0);
    if(mode==OPS_SCREEN_ALERTS||mode==OPS_SCREEN_TIMELINE){
        live_note=label(root,16,62,760,mode==OPS_SCREEN_ALERTS?"Read-only host alert policy":"Sanitised operational events",&lv_font_montserrat_14,MUTED);
        for(int i=0;i<7;i++){row[i]=lv_obj_create(root);lv_obj_set_pos(row[i],12,91+i*48);lv_obj_set_size(row[i],776,43);lv_obj_set_style_bg_color(row[i],color(CARD),0);lv_obj_set_style_border_color(row[i],color(EDGE),0);lv_obj_set_style_border_width(row[i],1,0);lv_obj_set_style_radius(row[i],9,0);lv_obj_set_style_pad_all(row[i],0,0);lv_obj_remove_flag(row[i],LV_OBJ_FLAG_SCROLLABLE);row_title[i]=label(row[i],10,5,210,"--",&lv_font_montserrat_12,MUTED);row_text[i]=label(row[i],224,5,540,"--",&lv_font_montserrat_12,INK);}
        history_note=label(root,16,437,760,mode==OPS_SCREEN_ALERTS?"No invented temperature limits; warning policy remains host-authoritative.":"Time-related, not causal.",&lv_font_montserrat_12,MUTED);
    }else if(mode==OPS_SCREEN_WIFI){
        live_note=label(root,16,58,760,"Wi-Fi STA | authenticated LAN telemetry | USB control primary",&lv_font_montserrat_14,MUTED);
        wifi_state_label=label(root,16,83,760,"STATE --",&lv_font_montserrat_14,INK);wifi_ssid_label=label(root,16,106,760,"SSID --",&lv_font_montserrat_12,MUTED);wifi_ip_label=label(root,16,127,760,"IP -- | RSSI --",&lv_font_montserrat_12,MUTED);wifi_host_label=label(root,16,148,760,"HOST discovery --",&lv_font_montserrat_12,MUTED);
        wifi_scan_btn=button(root,16,173,120,34,"SCAN",wifi_scan_event,NULL,NULL);wifi_config_btn=button(root,146,173,144,34,"CONFIGURE",wifi_config_event,NULL,NULL);wifi_connect_btn=button(root,300,173,120,34,"CONNECT",wifi_connect_event,NULL,NULL);wifi_forget_btn=button(root,430,173,120,34,"FORGET",wifi_forget_event,NULL,NULL);
        label(root,16,216,760,"NEARBY NETWORKS",&lv_font_montserrat_12,MUTED);
        for(int i=0;i<OPSDECK_WIFI_SCAN_MAX;i++){wifi_ap_btn[i]=button(root,16,238+i*39,768,34,"--",wifi_ap_event,(void*)(intptr_t)i,&wifi_ap_label[i]);lv_obj_add_flag(wifi_ap_btn[i],LV_OBJ_FLAG_HIDDEN);}
        history_note=label(root,16,440,760,"LAN telemetry: HMAC auth + integrity, read-only, not TLS. Remote TLS/Tunnel planned.",&lv_font_montserrat_12,MUTED);
    }else if(local_mode()){
        live_note=label(root,16,62,760,"Live Deck status",&lv_font_montserrat_14,MUTED);
        for(int i=0;i<6;i++){row[i]=lv_obj_create(root);lv_obj_set_pos(row[i],12,96+i*54);lv_obj_set_size(row[i],776,48);lv_obj_set_style_bg_color(row[i],color(CARD),0);lv_obj_set_style_border_color(row[i],color(EDGE),0);lv_obj_set_style_border_width(row[i],1,0);lv_obj_set_style_radius(row[i],10,0);lv_obj_set_style_pad_all(row[i],0,0);lv_obj_remove_flag(row[i],LV_OBJ_FLAG_SCROLLABLE);row_title[i]=label(row[i],12,7,210,"--",&lv_font_montserrat_12,MUTED);row_text[i]=label(row[i],230,7,532,"--",&lv_font_montserrat_14,INK);}
        history_note=label(root,16,430,760,mode==OPS_SCREEN_WIFI?"Wi-Fi transport is not active yet; no SSID/IP is invented.":"Read-only status screen.",&lv_font_montserrat_12,MUTED);
    }else{
        live_value=label(root,20,62,400,"--",&lv_font_montserrat_24,INK);live_note=label(root,20,102,420,"Waiting for live sample",&lv_font_montserrat_14,MUTED);
        const char *wn[]={"15m","1h","24h","7d"};for(int i=0;i<4;i++)window_btn[i]=button(root,456+i*82,58,76,34,wn[i],window_event,(void*)(intptr_t)i,&window_label[i]);
        for(int i=0;i<2;i++){variant_btn[i]=button(root,456+i*72,101,66,32,i==0?"C:":"D:",variant_event,(void*)(intptr_t)i,&variant_label[i]);if(history_id()!=8)lv_obj_add_flag(variant_btn[i],LV_OBJ_FLAG_HIDDEN);}
        if(mode==OPS_SCREEN_FANS){fan_control_btn[0]=button(root,456,101,62,32,"AUTO",fan_control_event,(void*)(intptr_t)1,&fan_control_label[0]);fan_control_btn[1]=button(root,524,101,62,32,"MAX",fan_control_event,(void*)(intptr_t)2,&fan_control_label[1]);fan_control_btn[2]=button(root,592,101,44,32,"-5",fan_control_event,(void*)(intptr_t)3,&fan_control_label[2]);fan_control_btn[3]=button(root,642,101,94,32,"MAN 70%",fan_control_event,(void*)(intptr_t)4,&fan_control_label[3]);fan_control_btn[4]=button(root,742,101,44,32,"+5",fan_control_event,(void*)(intptr_t)5,&fan_control_label[4]);}
        if(mode==OPS_SCREEN_HISTORY){button(root,600,101,82,32,"< METRIC",metric_event,(void*)(intptr_t)-1,NULL);metric_button=button(root,688,101,100,32,"METRIC >",metric_event,(void*)(intptr_t)1,&metric_label);}
        stats[0]=label(root,20,145,240,"MIN --",&lv_font_montserrat_16,CYAN);stats[1]=label(root,280,145,240,"AVG --",&lv_font_montserrat_16,GREEN);stats[2]=label(root,540,145,240,"MAX --",&lv_font_montserrat_16,PURPLE);
        chart=lv_chart_create(root);lv_obj_set_pos(chart,18,180);lv_obj_set_size(chart,764,208);lv_obj_set_style_bg_color(chart,color(CARD),0);lv_obj_set_style_border_color(chart,color(EDGE),0);lv_obj_set_style_border_width(chart,1,0);lv_obj_set_style_line_color(chart,color(EDGE),LV_PART_MAIN);lv_obj_set_style_size(chart,0,0,LV_PART_INDICATOR);lv_chart_set_type(chart,LV_CHART_TYPE_LINE);lv_chart_set_point_count(chart,48);lv_chart_set_range(chart,LV_CHART_AXIS_PRIMARY_Y,0,100);series_a=lv_chart_add_series(chart,color(CYAN),LV_CHART_AXIS_PRIMARY_Y);series_b=lv_chart_add_series(chart,color(GREEN),LV_CHART_AXIS_PRIMARY_Y);series_c=lv_chart_add_series(chart,color(PURPLE),LV_CHART_AXIS_PRIMARY_Y);
        history_note=label(root,20,399,760,"Waiting for host history",&lv_font_montserrat_12,MUTED);legend_note=label(root,20,421,760,"",&lv_font_montserrat_12,MUTED);
    }
    request_view();opsdeck_screen_ui_refresh(esp_timer_get_time());
}
static void update_live(int64_t now)
{
    if(!live_value)return;
    opsdeck_pc_t p;opsdeck_pc_copy(&p);bool live=p.received_us>0&&now-p.received_us<5000000;int id=history_id();int64_t second=now/1000000;if(p.sequence==last_live_pc_sequence&&second==last_live_second&&id==last_live_id&&fan_control_pending_mode==last_live_pending)return;last_live_pc_sequence=p.sequence;last_live_second=second;last_live_id=id;last_live_pending=fan_control_pending_mode;char b[128],n[160];
    int age=p.received_us?(int)((now-p.received_us)/1000000):-1;if(live)snprintf(b,sizeof(b),"LIVE | %ds",age);else if(age>=0)snprintf(b,sizeof(b),"STALE | %ds",age);else snprintf(b,sizeof(b),"NO DATA");lv_label_set_text(age_label,b);lv_obj_set_style_text_color(age_label,color(live?GREEN:MUTED),0);
    snprintf(b,sizeof(b),"--");snprintf(n,sizeof(n),"Waiting for live sample");
    if(id==0&&(p.valid&PC_CPU)){snprintf(b,sizeof(b),"%.0f%%",(double)p.cpu);if(p.valid&PC_CPU_TEMP)snprintf(n,sizeof(n),"CPU temp %.0f C",(double)p.cpu_temp);}
    else if(id==1&&(p.valid&PC_INTEL)){snprintf(b,sizeof(b),"%.0f%%",(double)p.intel_gpu);if(p.valid&PC_INTEL_SHARED)snprintf(n,sizeof(n),"Shared %.1f / %.1f GiB",(double)p.intel_shared_gib,(double)p.intel_shared_limit_gib);}
    else if(id==2&&(p.valid&PC_GPU)){snprintf(b,sizeof(b),"%.0f%%",(double)p.gpu);if((p.valid&PC_GPU_TEMP)&&(p.valid&PC_VRAM))snprintf(n,sizeof(n),"%.0f C | VRAM %.1f / %.1f GiB",(double)p.gpu_temp,(double)p.vram_used_gib,(double)p.vram_total_gib);}
    else if(id==3&&(p.valid&PC_RAM)){snprintf(b,sizeof(b),"%.1f / %.1f GiB",(double)p.ram_used_gib,(double)p.ram_total_gib);snprintf(n,sizeof(n),"%.0f%% used",(double)p.ram_pct);}
    else if(id==4&&(p.valid&PC_VRAM)){snprintf(b,sizeof(b),"%.1f / %.1f GiB",(double)p.vram_used_gib,(double)p.vram_total_gib);snprintf(n,sizeof(n),"%.0f%% used",(double)p.vram_pct);}
    else if(id==5&&(p.valid&PC_CPU_TEMP)){snprintf(b,sizeof(b),"CPU %.0f C",(double)p.cpu_temp);snprintf(n,sizeof(n),"Chassis %s | NVIDIA %s",(p.valid&PC_CHASSIS_TEMP)?"live":"--",(p.valid&PC_GPU_TEMP)?"live":"--");if((p.valid&PC_CHASSIS_TEMP)&&(p.valid&PC_GPU_TEMP))snprintf(n,sizeof(n),"Chassis %.0f C | NVIDIA %.0f C",(double)p.chassis_temp,(double)p.gpu_temp);}
    else if(id==6&&(p.valid&(PC_FAN1|PC_FAN2))){snprintf(b,sizeof(b),"F1 %.0f | F2 %.0f RPM",(double)p.fan1_rpm,(double)p.fan2_rpm);if(p.valid&PC_FAN_CONTROL){const char *cm=p.fan_control_mode==1?"AUTO":p.fan_control_mode==2?"MAX":p.fan_control_mode==3?"MANUAL":"UNKNOWN";snprintf(n,sizeof(n),"%.0f%% / %.0f%% of 5500 RPM | %s%s",(double)(p.fan1_rpm*100.0/5500.0),(double)(p.fan2_rpm*100.0/5500.0),p.fan_control_supported?cm:"CONTROL UNAVAILABLE",p.fan_control_busy?" | APPLYING":"");}else snprintf(n,sizeof(n),"%.0f%% / %.0f%% of 5500 RPM",(double)(p.fan1_rpm*100.0/5500.0),(double)(p.fan2_rpm*100.0/5500.0));}
    else if(id==7&&(p.valid&PC_NETWORK)){snprintf(b,sizeof(b),"RX %.1f / TX %.1f",(double)p.rx_mbps,(double)p.tx_mbps);snprintf(n,sizeof(n),"Mb/s | live interface throughput");}
    else if(id==8&&p.shown_volumes>history_variant&&p.volumes[history_variant].has_value){opsdeck_volume_t *v=&p.volumes[history_variant];snprintf(b,sizeof(b),"%s %.1f%% used",v->id,v->used_pct);snprintf(n,sizeof(n),"%.1f / %.1f GiB free",v->free_gib,v->total_gib);}
    else if(id==9&&(p.valid&PC_INTEL_SHARED)){double pct=p.intel_shared_limit_gib>0?p.intel_shared_gib*100.0/p.intel_shared_limit_gib:0;snprintf(b,sizeof(b),"%.1f / %.1f GiB",(double)p.intel_shared_gib,(double)p.intel_shared_limit_gib);snprintf(n,sizeof(n),"%.0f%% shared memory used",pct);}
    lv_label_set_text(live_value,b);lv_label_set_text(live_note,n);lv_obj_set_style_text_color(live_value,color(live?INK:MUTED),0);
    if(mode==OPS_SCREEN_FANS&&fan_control_btn[0]){
        bool pending=fan_control_pending_us>0&&now-fan_control_pending_us<15000000;
        if(!pending&&(p.valid&PC_FAN_CONTROL)&&p.fan_manual_supported&&p.fan_control_speed_pct>=50&&p.fan_control_speed_pct<=100)fan_manual_target=p.fan_control_speed_pct;
        if(pending&&(p.valid&PC_FAN_CONTROL)&&!p.fan_control_busy&&p.fan_control_mode==fan_control_pending_mode&&(fan_control_pending_mode!=3||p.fan_control_speed_pct==fan_control_pending_speed)){fan_control_pending_us=0;fan_control_pending_mode=0;fan_control_pending_speed=0;pending=false;}
        if(fan_control_pending_us>0&&now-fan_control_pending_us>=15000000){fan_control_pending_us=0;fan_control_pending_mode=0;fan_control_pending_speed=0;pending=false;}
        bool control=live&&(p.valid&PC_FAN_CONTROL)&&p.fan_control_supported;bool manual=control&&p.fan_manual_supported;int selected=p.fan_control_mode==1?0:p.fan_control_mode==2?1:p.fan_control_mode==3?3:-1;int requested=fan_control_pending_mode==1?0:fan_control_pending_mode==2?1:fan_control_pending_mode==3?3:-1;
        snprintf(b,sizeof(b),"MAN %d%%",fan_manual_target);lv_label_set_text(fan_control_label[3],b);
        for(int i=0;i<5;i++){
            bool disabled=!control||p.fan_control_busy||pending||i==selected;
            if(i==2)disabled=!manual||p.fan_control_busy||pending||fan_manual_target<=50;
            else if(i==4)disabled=!manual||p.fan_control_busy||pending||fan_manual_target>=100;
            else if(i==3)disabled=!manual||p.fan_control_busy||pending||(selected==3&&p.fan_control_speed_pct==fan_manual_target);
            if(disabled)lv_obj_add_state(fan_control_btn[i],LV_STATE_DISABLED);else lv_obj_remove_state(fan_control_btn[i],LV_STATE_DISABLED);
            uint32_t bc=(pending&&i==requested)?AMBER:(i==selected?CYAN:EDGE);uint32_t tc=!control||(i>=2&&!manual)?MUTED:((pending&&i==requested)?AMBER:(i==selected?CYAN:INK));
            lv_obj_set_style_border_color(fan_control_btn[i],color(bc),0);lv_obj_set_style_text_color(fan_control_label[i],color(tc),0);
        }
    }
    if(mode==OPS_SCREEN_HISTORY){snprintf(b,sizeof(b),"HISTORY / %s",metric_name(id));lv_label_set_text(title,b);}
}
static double y_ceiling(const opsdeck_opsview_t *s,int id)
{
    if(id<=2)return 100;
    if(id==6)return 5500;
    if(id==8)return 100;
    double peak=0;
    for(int i=0;i<s->point_count;i++){if(s->points[i].a_valid&&s->points[i].a>peak)peak=s->points[i].a;if(s->points[i].b_valid&&s->points[i].b>peak)peak=s->points[i].b;if(s->points[i].c_valid&&s->points[i].c>peak)peak=s->points[i].c;}
    if(s->max_valid&&s->max>peak)peak=s->max;
    if(s->max2_valid&&s->max2>peak)peak=s->max2;
    if(s->max3_valid&&s->max3>peak)peak=s->max3;
    if(id==5)return fmax(100,ceil((peak+10)/10)*10);
    if(id==7)return fmax(10,ceil((peak*1.2+1)/10)*10);
    return fmax(10,ceil(peak*1.2+1));
}
static void stat_text(lv_obj_t *o,const char *name,bool valid,double value,const char *unit)
{
    char b[64];if(valid)snprintf(b,sizeof(b),"%s %.1f %s",name,value,unit);else snprintf(b,sizeof(b),"%s --",name);lv_label_set_text(o,b);
}
static void render_history(const opsdeck_opsview_t *s)
{
    if(!chart||!s->present||s->kind!=0)return;
    int id=history_id();char b[200];
    for(int i=0;i<4;i++){lv_obj_set_style_border_color(window_btn[i],color(i==window_index?CYAN:EDGE),0);lv_obj_set_style_text_color(window_label[i],color(i==window_index?CYAN:INK),0);}
    opsdeck_pc_t pc;opsdeck_pc_copy(&pc);bool storage=id==8;
    for(int i=0;i<2;i++)if(variant_btn[i]){
        bool visible=storage&&i<s->volume_count;if(visible)lv_obj_remove_flag(variant_btn[i],LV_OBJ_FLAG_HIDDEN);else lv_obj_add_flag(variant_btn[i],LV_OBJ_FLAG_HIDDEN);
        if(visible){const char *name=(i<pc.shown_volumes&&pc.volumes[i].id[0])?pc.volumes[i].id:(i==0?"VOL 1":"VOL 2");lv_label_set_text(variant_label[i],name);lv_obj_set_style_border_color(variant_btn[i],color(i==history_variant?CYAN:EDGE),0);lv_obj_set_style_text_color(variant_label[i],color(i==history_variant?CYAN:INK),0);}
    }
    if(storage){snprintf(b,sizeof(b),mode==OPS_SCREEN_HISTORY?"HISTORY / STORAGE / %s":"STORAGE / %s",s->label);lv_label_set_text(title,b);}
    stat_text(stats[0],"MIN",s->min_valid,s->min,s->unit);stat_text(stats[1],"AVG",s->avg_valid,s->avg,s->unit);stat_text(stats[2],"MAX",s->max_valid,s->max,s->unit);
    int count=s->point_count>1?s->point_count:48;lv_chart_set_point_count(chart,count);double ceiling=y_ceiling(s,id);lv_chart_set_range(chart,LV_CHART_AXIS_PRIMARY_Y,0,(int32_t)ceil(ceiling));
    lv_chart_set_all_value(chart,series_a,LV_CHART_POINT_NONE);lv_chart_set_all_value(chart,series_b,LV_CHART_POINT_NONE);lv_chart_set_all_value(chart,series_c,LV_CHART_POINT_NONE);
    if(s->point_count>0)for(int i=0;i<s->point_count;i++){
        lv_chart_set_next_value(chart,series_a,s->points[i].a_valid?(int32_t)lround(s->points[i].a):LV_CHART_POINT_NONE);
        lv_chart_set_next_value(chart,series_b,s->points[i].b_valid?(int32_t)lround(s->points[i].b):LV_CHART_POINT_NONE);
        lv_chart_set_next_value(chart,series_c,s->points[i].c_valid?(int32_t)lround(s->points[i].c):LV_CHART_POINT_NONE);
    }
    const char *coverage=s->coverage_state==2?"FULL WINDOW":s->coverage_state==1?"PARTIAL WINDOW":"NO HISTORY";
    if(s->coverage_state>0){
        if(s->last_age_s>=0)snprintf(b,sizeof(b),"%s %d%% | %d/%d min | %s -> %s | last %ds",coverage,s->coverage_pct,s->sample_rows,s->expected_minutes,s->first_local,s->last_local,s->last_age_s);
        else snprintf(b,sizeof(b),"%s %d%% | %d/%d min | %s -> %s",coverage,s->coverage_pct,s->sample_rows,s->expected_minutes,s->first_local,s->last_local);
    }else snprintf(b,sizeof(b),"NO HISTORY | 0/%d min | %s window",s->expected_minutes,window_name(window_index));
    lv_label_set_text(history_note,b);lv_obj_set_style_text_color(history_note,color(s->coverage_state==2?GREEN:(s->coverage_state==1?AMBER:MUTED)),0);
    if(id==5)lv_label_set_text(legend_note,"CPU cyan | CHASSIS green | NVIDIA purple");
    else if(id==6)lv_label_set_text(legend_note,"FAN1 cyan | FAN2 green | AUTO/MAX verified | MANUAL locked");
    else if(id==7)lv_label_set_text(legend_note,"RX cyan | TX green");
    else if(id==8){snprintf(b,sizeof(b),"%s used %% history | select volume above",s->label);lv_label_set_text(legend_note,b);}
    else if(id==9&&s->coverage_state==0)lv_label_set_text(legend_note,"Intel Shared history begins with M5.10-AB; older values are not invented.");
    else lv_label_set_text(legend_note,"");
    lv_chart_refresh(chart);
}
static const char *domain_name(int v){static const char *n[]={"HOST","LINK","AGENT","CLOUD","HTTPS","SESSION","POWER","DEVICE"};return n[(v>=0&&v<8)?v:0];}
static const char *source_state(int v){static const char *n[]={"SETUP","OK","ERROR","STALE","NO DATA","PARTIAL","DENIED"};return n[(v>=0&&v<=6)?v:4];}
static void local_row(int index,const char *name,const char *value,uint32_t c)
{
    if(index<0||index>=6||!row[index])return;
    lv_label_set_text(row_title[index],name);lv_label_set_text(row_text[index],value);lv_obj_set_style_text_color(row_text[index],color(c),0);lv_obj_remove_flag(row[index],LV_OBJ_FLAG_HIDDEN);
}
static const char *wifi_state_name(opsdeck_wifi_state_t s)
{
    return s==OPSDECK_WIFI_CONNECTED?"CONNECTED":s==OPSDECK_WIFI_CONNECTING?"CONNECTING":s==OPSDECK_WIFI_NO_CONFIG?"NO CONFIG":s==OPSDECK_WIFI_ERROR?"ERROR":"DISABLED";
}
static void render_wifi(int64_t now)
{
    opsdeck_wifi_status_t s;opsdeck_wifi_copy(&s);char b[192];uint32_t c=s.connected?GREEN:(s.state==OPSDECK_WIFI_ERROR?RED:(s.state==OPSDECK_WIFI_CONNECTING?AMBER:MUTED));
    snprintf(b,sizeof(b),"%s%s%s%s%s",wifi_state_name(s.state),s.radio_active?" | RADIO ON":" | RADIO IDLE",opsdeck_link_auth_paired()?" | PAIRED":" | USB PAIR WAIT",s.scanning?" | SCANNING":"",s.retry_count>0?" | RETRY":"");lv_label_set_text(wifi_state_label,b);lv_obj_set_style_text_color(wifi_state_label,color(c),0);
    snprintf(b,sizeof(b),"SSID %s%s",s.configured?s.ssid:"NOT CONFIGURED",s.configured?" | saved":"");lv_label_set_text(wifi_ssid_label,b);
    if(s.connected)snprintf(b,sizeof(b),"IP %s | RSSI %d dBm | retries %d",s.ip[0]?s.ip:"--",s.rssi,s.retry_count);else snprintf(b,sizeof(b),"IP -- | disconnect reason %d | retries %d",s.last_disconnect_reason,s.retry_count);lv_label_set_text(wifi_ip_label,b);
    if(s.host_seen_us>0){int age=(int)((now-s.host_seen_us)/1000000);const char *tcp=s.telemetry_active?"LAN TELEMETRY ACTIVE":s.telemetry_authenticated?"LAN AUTH / USB PRIMARY":opsdeck_link_auth_paired()?"LAN AUTH WAIT":"USB PAIR WAIT";snprintf(b,sizeof(b),"HOST %s | %s:%u | %s | %ds",s.host_name,s.host_ip,(unsigned)s.host_port,tcp,age);lv_label_set_text(wifi_host_label,b);lv_obj_set_style_text_color(wifi_host_label,color(s.telemetry_authenticated?GREEN:CYAN),0);}
    else{lv_label_set_text(wifi_host_label,"HOST discovery -- | waiting for OpsDeck host beacon");lv_obj_set_style_text_color(wifi_host_label,color(MUTED),0);}
    if(age_label){lv_label_set_text(age_label,s.connected?"WIFI LIVE":(s.configured?"WIFI WAIT":"WIFI SETUP"));lv_obj_set_style_text_color(age_label,color(c),0);}
    if(wifi_connect_btn){if(s.configured)lv_obj_remove_state(wifi_connect_btn,LV_STATE_DISABLED);else lv_obj_add_state(wifi_connect_btn,LV_STATE_DISABLED);}
    if(wifi_forget_btn){if(s.configured)lv_obj_remove_state(wifi_forget_btn,LV_STATE_DISABLED);else lv_obj_add_state(wifi_forget_btn,LV_STATE_DISABLED);}
    if(wifi_scan_btn){if(s.scanning)lv_obj_add_state(wifi_scan_btn,LV_STATE_DISABLED);else lv_obj_remove_state(wifi_scan_btn,LV_STATE_DISABLED);}
    for(int i=0;i<OPSDECK_WIFI_SCAN_MAX;i++)if(wifi_ap_btn[i]){
        if(i<s.scan_count){opsdeck_wifi_ap_t *a=&s.scan[i];snprintf(b,sizeof(b),"%s   |   %d dBm   |   %s",a->ssid,a->rssi,a->secure?"SECURED":"OPEN");lv_label_set_text(wifi_ap_label[i],b);lv_obj_remove_flag(wifi_ap_btn[i],LV_OBJ_FLAG_HIDDEN);}
        else lv_obj_add_flag(wifi_ap_btn[i],LV_OBJ_FLAG_HIDDEN);
    }
}
static void render_local(int64_t now)
{
    char b[160];opsdeck_pc_t p;opsdeck_pc_copy(&p);int pc_age=p.received_us?(int)((now-p.received_us)/1000000):-1;bool pc_live=pc_age>=0&&pc_age<5;
    lv_label_set_text(age_label,"LOCAL LIVE");lv_obj_set_style_text_color(age_label,color(GREEN),0);
    if(mode==OPS_SCREEN_DEVICE){
        lv_label_set_text(live_note,"Deck hardware and runtime");local_row(0,"MCU / DISPLAY","ESP32-S3 | 800 x 480 | RGB565",INK);local_row(1,"RUNTIME","ESP-IDF 5.5.4 | LVGL 9.3.0",INK);
        snprintf(b,sizeof(b),"%lld s",(long long)(now/1000000));local_row(2,"DECK UPTIME",b,CYAN);snprintf(b,sizeof(b),"%u KiB free",(unsigned)(heap_caps_get_free_size(MALLOC_CAP_INTERNAL)/1024));local_row(3,"INTERNAL HEAP",b,GREEN);
        snprintf(b,sizeof(b),"%.1f MiB free",heap_caps_get_free_size(MALLOC_CAP_SPIRAM)/1048576.0);local_row(4,"PSRAM",b,PURPLE);if(pc_live)snprintf(b,sizeof(b),"LIVE | seq %lu | age %ds",(unsigned long)p.sequence,pc_age);else if(pc_age>=0)snprintf(b,sizeof(b),"STALE | age %ds",pc_age);else snprintf(b,sizeof(b),"NO HOST");local_row(5,"PC TELEMETRY",b,pc_live?GREEN:MUTED);
    }else if(mode==OPS_SCREEN_SD){
        opsdeck_sd_info_t sd;opsdeck_sd_copy(&sd);const char *st=sd.state==OPSDECK_SD_READY?"READY":sd.state==OPSDECK_SD_SETUP?"PROBING":sd.state==OPSDECK_SD_UNAVAILABLE?"UNAVAILABLE":"ERROR";lv_label_set_text(live_note,"Deck SD card status");local_row(0,"STATE",st,sd.state==OPSDECK_SD_READY?GREEN:(sd.state==OPSDECK_SD_ERROR?AMBER:MUTED));
        if(sd.total_bytes>0)snprintf(b,sizeof(b),"%.1f / %.1f GiB free",sd.free_bytes/1073741824.0,sd.total_bytes/1073741824.0);else snprintf(b,sizeof(b),"Capacity unavailable");local_row(1,"CAPACITY",b,INK);local_row(2,"MOUNT","/sdcard | SDSPI",INK);local_row(3,"WRITE POLICY","OpsDeck writes disabled",MUTED);local_row(4,"FORMAT POLICY","Never auto-format",MUTED);local_row(5,"ROLE","Optional local storage",MUTED);
    }else if(mode==OPS_SCREEN_LINK){
        opsdeck_status_t s;opsdeck_status_copy(&s);int age=s.received_us?(int)((now-s.received_us)/1000000):-1;bool fresh=age>=0&&age<16;lv_label_set_text(live_note,"USB link recovery and transport evidence");snprintf(b,sizeof(b),"%s%s",fresh&&s.link_present?source_state(s.link_state):"NO DATA",fresh&&s.link_recovering?" / RECOVERING":"");local_row(0,"LINK STATE",b,fresh&&s.link_state==1?GREEN:(s.link_recovering?AMBER:MUTED));
        snprintf(b,sizeof(b),"%d s",fresh&&s.link_present?s.link_forward_age_s:-1);local_row(1,"FORWARD ACK AGE",b,INK);snprintf(b,sizeof(b),"reopen %d | ROM %d | recovered %d",s.link_reopen_count,s.link_rom_probe_count,s.link_recovered_count);local_row(2,"RECOVERY COUNTS",b,INK);snprintf(b,sizeof(b),"%d s",s.link_host_uptime_s);local_row(3,"HOST UPTIME",b,INK);snprintf(b,sizeof(b),"action %d | reason %d | age %d s",s.link_last_action,s.link_last_reason,s.link_recovery_age_s);local_row(4,"LAST RECOVERY",b,MUTED);if(pc_live)snprintf(b,sizeof(b),"USB PC frame seq %lu | %ds",(unsigned long)p.sequence,pc_age);else snprintf(b,sizeof(b),"PC frame stale / unavailable");local_row(5,"TRANSPORT",b,pc_live?GREEN:MUTED);
    }else if(mode==OPS_SCREEN_WIFI){
        lv_label_set_text(live_note,"Network/Wi-Fi transport preparation");local_row(0,"WI-FI TRANSPORT","NOT ACTIVE YET",AMBER);local_row(1,"ACTIVE DECK LINK","USB telemetry + agent control",GREEN);
        if(pc_live&&(p.valid&PC_NETWORK))snprintf(b,sizeof(b),"RX %.1f | TX %.1f Mb/s",(double)p.rx_mbps,(double)p.tx_mbps);else snprintf(b,sizeof(b),"PC network telemetry unavailable");local_row(2,"PC NETWORK",b,pc_live?CYAN:MUTED);local_row(3,"SSID","NOT CONFIGURED",MUTED);local_row(4,"DECK IP","NOT ASSIGNED",MUTED);local_row(5,"USB / WI-FI FAILOVER","NOT ENABLED YET",MUTED);
    }
}
static void clear_rows(void){for(int i=0;i<7;i++){if(row[i])lv_obj_add_flag(row[i],LV_OBJ_FLAG_HIDDEN);}}
static void age_text(char *b,size_t n,int age)
{
    if(age<0)snprintf(b,n,"--");else if(age<60)snprintf(b,n,"%ds",age);else if(age<3600)snprintf(b,n,"%dm",age/60);else snprintf(b,n,"%dh",age/3600);
}
static void render_alerts(const opsdeck_opsview_t *s)
{
    if(!s->present||s->kind!=1)return;
    clear_rows();char b[96];int shown=s->count<OPSDECK_OPSVIEW_ALERTS?s->count:OPSDECK_OPSVIEW_ALERTS;
    snprintf(b,sizeof(b),"%s | %d active%s",s->level==3?"DEGRADED":s->level==2?"RECOVERING":s->level==1?"ATTENTION":"INFO",s->count,s->count>shown?" | top 6":"");lv_label_set_text(live_note,b);lv_obj_set_style_text_color(live_note,color(alert_color(s->level)),0);
    if(shown==0){lv_obj_remove_flag(row[0],LV_OBJ_FLAG_HIDDEN);lv_label_set_text(row_title[0],"INFO");lv_label_set_text(row_text[0],"No active alerts");lv_obj_set_style_border_color(row[0],color(GREEN),0);return;}
    for(int i=0;i<shown;i++){lv_obj_remove_flag(row[i],LV_OBJ_FLAG_HIDDEN);opsdeck_ops_alert_t *a=(opsdeck_ops_alert_t*)&s->alerts[i];snprintf(b,sizeof(b),"%s | %s",a->level>=3?"DEGRADED":a->level==2?"RECOVERING":"ATTENTION",a->source);lv_label_set_text(row_title[i],b);lv_label_set_text(row_text[i],a->text);uint32_t c=alert_color(a->level);lv_obj_set_style_text_color(row_title[i],color(c),0);lv_obj_set_style_border_color(row[i],color(c),0);}
}
static void render_timeline(const opsdeck_opsview_t *s)
{
    if(!s->present||s->kind!=2)return;
    clear_rows();char b[96],age[24];
    if(s->count==0){lv_obj_remove_flag(row[0],LV_OBJ_FLAG_HIDDEN);lv_label_set_text(row_title[0],"NO EVENTS");lv_label_set_text(row_text[0],"Operational timeline is empty");return;}
    for(int i=0;i<s->count&&i<OPSDECK_OPSVIEW_EVENTS;i++){lv_obj_remove_flag(row[i],LV_OBJ_FLAG_HIDDEN);opsdeck_ops_event_t *e=(opsdeck_ops_event_t*)&s->events[i];age_text(age,sizeof(age),e->age_s);snprintf(b,sizeof(b),"%s | %s | %s",age,domain_name(e->domain),e->code);lv_label_set_text(row_title[i],b);lv_label_set_text(row_text[i],e->summary);uint32_t c=e->severity>=2?RED:e->severity==1?AMBER:MUTED;lv_obj_set_style_text_color(row_title[i],color(c),0);lv_obj_set_style_border_color(row[i],color(e->severity?c:EDGE),0);}
}
void opsdeck_screen_ui_refresh(int64_t now)
{
    if(!root)return;
    if(mode==OPS_SCREEN_WIFI){opsdeck_wifi_status_t w;opsdeck_wifi_copy(&w);int64_t second=now/1000000;if(w.sequence==last_wifi_sequence&&second==last_wifi_second)return;last_wifi_sequence=w.sequence;last_wifi_second=second;render_wifi(now);return;}
    if(local_mode()){opsdeck_pc_t p;opsdeck_pc_copy(&p);int64_t second=now/1000000;if(p.sequence==last_local_pc_sequence&&second==last_local_second&&last_local_mode==(int)mode)return;last_local_pc_sequence=p.sequence;last_local_second=second;last_local_mode=(int)mode;render_local(now);return;}
    if(now-last_request_us>10000000){opsdeck_opsview_request_current();last_request_us=now;}
    if(mode!=OPS_SCREEN_ALERTS&&mode!=OPS_SCREEN_TIMELINE)update_live(now);
    opsdeck_opsview_t s;opsdeck_opsview_copy(&s,NULL);int age=s.present?(int)((now-s.received_us)/1000000):-1;char b[64];
    if(mode==OPS_SCREEN_ALERTS||mode==OPS_SCREEN_TIMELINE){if(s.present)snprintf(b,sizeof(b),"HOST DATA | %ds",age<0?0:age);else snprintf(b,sizeof(b),"WAITING FOR HOST");lv_label_set_text(age_label,b);}
    if(!s.present||s.sequence==last_ops_sequence)return;
    last_ops_sequence=s.sequence;
    if(mode==OPS_SCREEN_ALERTS)render_alerts(&s);else if(mode==OPS_SCREEN_TIMELINE)render_timeline(&s);else render_history(&s);
}
