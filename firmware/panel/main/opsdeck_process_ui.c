#include "opsdeck_process_ui.h"
#include "opsdeck_processes.h"
#include <stdio.h>
#include <inttypes.h>
#include "esp_timer.h"

#define BG 0x090F1B
#define CARD 0x121E30
#define EDGE 0x23354C
#define INK 0xEDF5FF
#define MUTED 0x8293AD
#define CYAN 0x44D7EE
#define GREEN 0x73E0A9
#define AMBER 0xFFD166
#define RED 0xFF5C5C
#define DISPLAY_ROWS 10

static lv_obj_t *root,*summary,*age_label,*row_name[DISPLAY_ROWS],*row_pid[DISPLAY_ROWS],*row_cpu[DISPLAY_ROWS],*row_ram[DISPLAY_ROWS],*row_bar[DISPLAY_ROWS];
static uint32_t last_sequence=UINT32_MAX;static int64_t last_second=-1;
static lv_color_t color(uint32_t n){return lv_color_hex(n);}
static lv_obj_t *label(lv_obj_t *p,int x,int y,int w,const char *value,const lv_font_t *font,uint32_t c)
{
    lv_obj_t *o=lv_label_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,w);lv_label_set_text(o,value);
    lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(c),0);lv_label_set_long_mode(o,LV_LABEL_LONG_DOT);return o;
}
static uint32_t cpu_color(float v){return v>=70?RED:(v>=35?AMBER:(v>=5?GREEN:CYAN));}
static void close_event(lv_event_t *e){(void)e;opsdeck_process_ui_close();}
bool opsdeck_process_ui_is_open(void){return root!=NULL;}
void opsdeck_process_ui_close(void)
{
    if(root){lv_obj_delete_async(root);root=NULL;}summary=age_label=NULL;last_sequence=UINT32_MAX;last_second=-1;
    for(int i=0;i<DISPLAY_ROWS;i++)row_name[i]=row_pid[i]=row_cpu[i]=row_ram[i]=row_bar[i]=NULL;
}
void opsdeck_process_ui_open(void)
{
    if(root)return;
    root=lv_obj_create(lv_screen_active());lv_obj_set_pos(root,0,0);lv_obj_set_size(root,800,480);
    lv_obj_set_style_bg_color(root,color(BG),0);lv_obj_set_style_border_width(root,0,0);lv_obj_set_style_radius(root,0,0);lv_obj_set_style_pad_all(root,0,0);lv_obj_remove_flag(root,LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_t *back=lv_button_create(root);lv_obj_set_pos(back,12,10);lv_obj_set_size(back,82,38);lv_obj_set_style_bg_color(back,color(CARD),0);lv_obj_set_style_border_color(back,color(EDGE),0);lv_obj_set_style_border_width(back,1,0);lv_obj_set_style_radius(back,10,0);lv_obj_set_style_shadow_width(back,0,0);lv_obj_add_event_cb(back,close_event,LV_EVENT_CLICKED,NULL);
    lv_obj_t *bt=label(back,0,0,70,"BACK",&lv_font_montserrat_14,INK);lv_obj_set_style_text_align(bt,LV_TEXT_ALIGN_CENTER,0);lv_obj_center(bt);
    label(root,110,11,430,"TASK MANAGER / HP OMEN 16",&lv_font_montserrat_20,INK);
    summary=label(root,110,36,500,"Waiting for process snapshot",&lv_font_montserrat_12,MUTED);
    age_label=label(root,626,20,160,"--",&lv_font_montserrat_12,MUTED);lv_obj_set_style_text_align(age_label,LV_TEXT_ALIGN_RIGHT,0);
    label(root,16,64,350,"APPLICATION",&lv_font_montserrat_12,MUTED);label(root,385,64,74,"PID",&lv_font_montserrat_12,MUTED);label(root,475,64,112,"CPU",&lv_font_montserrat_12,MUTED);label(root,625,64,150,"RAM",&lv_font_montserrat_12,MUTED);
    for(int i=0;i<DISPLAY_ROWS;i++){
        int y=86+i*34;lv_obj_t *r=lv_obj_create(root);lv_obj_set_pos(r,12,y);lv_obj_set_size(r,776,30);lv_obj_set_style_bg_color(r,color(CARD),0);lv_obj_set_style_border_color(r,color(EDGE),0);lv_obj_set_style_border_width(r,1,0);lv_obj_set_style_radius(r,8,0);lv_obj_set_style_pad_all(r,0,0);lv_obj_remove_flag(r,LV_OBJ_FLAG_SCROLLABLE);
        row_name[i]=label(r,8,7,350,"--",&lv_font_montserrat_14,INK);row_pid[i]=label(r,373,7,76,"--",&lv_font_montserrat_12,MUTED);
        row_cpu[i]=label(r,463,4,112,"--",&lv_font_montserrat_14,INK);row_ram[i]=label(r,613,7,148,"--",&lv_font_montserrat_12,MUTED);
        row_bar[i]=lv_bar_create(r);lv_obj_set_pos(row_bar[i],463,22);lv_obj_set_size(row_bar[i],112,4);lv_bar_set_range(row_bar[i],0,100);lv_bar_set_value(row_bar[i],0,LV_ANIM_OFF);lv_obj_set_style_bg_color(row_bar[i],color(EDGE),LV_PART_MAIN);lv_obj_set_style_bg_color(row_bar[i],color(CYAN),LV_PART_INDICATOR);
    }
    label(root,16,438,760,"Read-only | top processes by CPU, then RAM | no terminate action | 2 s refresh",&lv_font_montserrat_12,MUTED);
    opsdeck_process_ui_refresh(esp_timer_get_time());
}

void opsdeck_process_ui_refresh(int64_t now)
{
    if(!root)return;
    opsdeck_processes_t s;opsdeck_processes_copy(&s);int64_t second=now/1000000;
    if(s.sequence==last_sequence&&second==last_second)return;
    last_sequence=s.sequence;last_second=second;
    char b[96];int age=s.present&&now>=s.received_us?(int)((now-s.received_us)/1000000):-1;
    if(!s.present){lv_label_set_text(summary,"Waiting for process snapshot");lv_label_set_text(age_label,"NO DATA");}
    else{snprintf(b,sizeof(b),"Showing %d of %d active processes",s.row_count<DISPLAY_ROWS?s.row_count:DISPLAY_ROWS,s.total_count);lv_label_set_text(summary,b);snprintf(b,sizeof(b),"%ds ago",age);lv_label_set_text(age_label,b);}
    for(int i=0;i<DISPLAY_ROWS;i++){
        bool visible=s.present&&i<s.row_count;if(!visible){lv_label_set_text(row_name[i],"--");lv_label_set_text(row_pid[i],"--");lv_label_set_text(row_cpu[i],"--");lv_label_set_text(row_ram[i],"--");lv_bar_set_value(row_bar[i],0,LV_ANIM_OFF);continue;}
        const opsdeck_process_row_t *r=&s.rows[i];lv_label_set_text(row_name[i],r->name);snprintf(b,sizeof(b),"%d",r->pid);lv_label_set_text(row_pid[i],b);snprintf(b,sizeof(b),"%.1f%%",(double)r->cpu_pct);lv_label_set_text(row_cpu[i],b);snprintf(b,sizeof(b),"%.0f MiB",(double)r->ram_mib);lv_label_set_text(row_ram[i],b);
        uint32_t c=cpu_color(r->cpu_pct);lv_obj_set_style_text_color(row_cpu[i],color(c),0);lv_obj_set_style_bg_color(row_bar[i],color(c),LV_PART_INDICATOR);lv_bar_set_value(row_bar[i],(int)(r->cpu_pct+0.5f),LV_ANIM_ON);
    }
}
