#include "opsdeck_details_ui.h"
#include "opsdeck_details.h"
#include <stdio.h>
#include <inttypes.h>
#include <string.h>
static lv_obj_t *title,*age_label,*scope,*note,*pages,*category_label,*accounts[2],*account_labels[2],*previous,*next;
static lv_obj_t *metric_labels[4],*metric_values[4],*metric_units[4];
static uint32_t last_sequence=UINT32_MAX;
static int last_request=-1;static int64_t last_second=-1;
static const char *kinds[]={"WORKER","D1","R2","QUEUES","WORKERS AI","AI GATEWAY"};
static lv_color_t color(uint32_t n){return lv_color_hex(n);}
static lv_obj_t *label(lv_obj_t *p,int x,int y,int w,const char *text,const lv_font_t *font)
{
    lv_obj_t *o=lv_label_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,w);lv_obj_set_height(o,font->line_height);
    lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(0xEDF5FF),0);
    lv_label_set_long_mode(o,LV_LABEL_LONG_DOT);lv_label_set_text(o,text);return o;
}
static lv_obj_t *button(lv_obj_t *p,int x,int y,int w,const char *text,lv_event_cb_t callback,uintptr_t action,lv_obj_t **caption)
{
    lv_obj_t *o=lv_button_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,40);
    lv_obj_set_style_pad_all(o,0,0);lv_obj_set_style_radius(o,10,0);lv_obj_set_style_border_width(o,1,0);
    lv_obj_set_style_border_color(o,color(0x23354C),0);lv_obj_set_style_bg_color(o,color(0x121E30),0);
    lv_obj_set_style_shadow_width(o,0,0);lv_obj_remove_flag(o,LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_t *l=label(o,8,12,w-16,text,&lv_font_montserrat_14);lv_obj_set_style_text_align(l,LV_TEXT_ALIGN_CENTER,0);
    if(caption)*caption=l;
    lv_obj_add_event_cb(o,callback,LV_EVENT_CLICKED,(void*)action);return o;
}
static void age_text(char *out,size_t size,int64_t seconds)
{
    if(seconds<0)snprintf(out,size,"--");
    else if(seconds<60)snprintf(out,size,"%"PRId64"s",seconds);
    else if(seconds<3600)snprintf(out,size,"%"PRId64"m",seconds/60);
    else if(seconds<86400)snprintf(out,size,"%"PRId64"h",seconds/3600);
    else snprintf(out,size,"%"PRId64"d",seconds/86400);
}
static void navigate(lv_event_t *e)
{
    opsdeck_details_t s;opsdeck_details_query_t q;opsdeck_details_copy(&s,&q);
    uintptr_t action=(uintptr_t)lv_event_get_user_data(e);
    if(action<2)opsdeck_details_select((int)action,q.kind,0);
    else if(action==2)opsdeck_details_select(q.slot,(q.kind+1)%OPSDECK_DETAILS_KINDS,0);
    else if(s.present){int page=s.page+(action==3?-1:1);if(page>=0&&page<s.total_pages)opsdeck_details_select(q.slot,q.kind,page);}
}
void opsdeck_details_ui_clear(void)
{
    title=NULL;opsdeck_details_deactivate();last_sequence=UINT32_MAX;last_request=-1;last_second=-1;
}
void opsdeck_details_ui_create(lv_obj_t *parent)
{
    accounts[0]=button(parent,4,0,174,"VetaKeep",navigate,0,&account_labels[0]);
    accounts[1]=button(parent,182,0,174,"Other Projects",navigate,1,&account_labels[1]);
    button(parent,370,0,402,"CATEGORY: WORKER  >",navigate,2,&category_label);
    title=label(parent,12,46,752,"Waiting for DETAILS v2 host",&lv_font_montserrat_16);
    age_label=label(parent,12,68,752,"No matching response; no zero values inferred",&lv_font_montserrat_14);
    scope=label(parent,12,90,752,"Read-only host cache; refresh runs independently",&lv_font_montserrat_14);
    for(int i=0;i<4;i++){
        metric_labels[i]=label(parent,12,117+i*38,350,"--",&lv_font_montserrat_14);
        metric_values[i]=label(parent,374,112+i*38,264,"--",&lv_font_montserrat_20);
        metric_units[i]=label(parent,650,117+i*38,110,"--",&lv_font_montserrat_14);
    }
    note=label(parent,12,272,752,"Panel navigation is read-only; it never triggers an API request",&lv_font_montserrat_12);
    previous=button(parent,4,296,90,LV_SYMBOL_LEFT,navigate,3,NULL);
    next=button(parent,682,296,90,LV_SYMBOL_RIGHT,navigate,4,NULL);
    pages=label(parent,110,308,560,"-- / --",&lv_font_montserrat_14);lv_obj_set_style_text_align(pages,LV_TEXT_ALIGN_CENTER,0);
    opsdeck_details_query_t q;opsdeck_details_copy(NULL,&q);opsdeck_details_select(q.slot,q.kind,q.page);
}
static void metric_text(char *value,size_t value_size,char *unit,size_t unit_size,const opsdeck_detail_metric_t *m)
{
    if(!m->valid){snprintf(value,value_size,"--");snprintf(unit,unit_size,"%s",m->unit);return;}
    double v=m->value;
    if(!strcmp(m->unit,"bytes")){
        if(v>=1073741824.0){snprintf(value,value_size,"%.2f",v/1073741824.0);snprintf(unit,unit_size,"GiB");return;}
        if(v>=1048576.0){snprintf(value,value_size,"%.2f",v/1048576.0);snprintf(unit,unit_size,"MiB");return;}
        if(v>=1024.0){snprintf(value,value_size,"%.2f",v/1024.0);snprintf(unit,unit_size,"KiB");return;}
    }
    snprintf(value,value_size,"%.6g",v);snprintf(unit,unit_size,"%s",m->unit);
}
void opsdeck_details_ui_refresh(int64_t now)
{
    if(!title)return;
    opsdeck_details_t s;opsdeck_details_query_t q;opsdeck_details_copy(&s,&q);
    if(last_sequence==s.sequence&&last_request==q.request_id&&last_second==now/1000000)return;
    last_sequence=s.sequence;last_request=q.request_id;last_second=now/1000000;
    char text[200];snprintf(text,sizeof(text),"CATEGORY: %s  >",kinds[q.kind]);lv_label_set_text(category_label,text);
    for(int i=0;i<2;i++)lv_obj_set_style_border_color(accounts[i],color(i==q.slot?0x44D7EE:0x23354C),0);
    if(s.present)lv_label_set_text(account_labels[q.slot],s.account_name);
    int64_t elapsed=s.present&&now>=s.received_us?(now-s.received_us)/1000000:0;
    bool transport=s.present&&elapsed>=OPSDECK_DETAILS_TRANSPORT_TTL;
    bool stale=s.present&&s.age_s>=0&&(int64_t)s.age_s+elapsed>OPSDECK_DETAILS_TTL;
    static const char *states[]={"SETUP","OBSERVED","ERROR","STALE","NO DATA","PARTIAL","DENIED"};
    snprintf(text,sizeof(text),"%s%s",s.present&&s.test?"TEST DATA / ":"",s.present?s.title:"Waiting for DETAILS v2 host");lv_label_set_text(title,text);
    char source_age[24],usb_age[24];age_text(source_age,sizeof(source_age),s.age_s<0?-1:(int64_t)s.age_s+elapsed);age_text(usb_age,sizeof(usb_age),s.present?elapsed:-1);
    if(!s.present)snprintf(text,sizeof(text),"No matching response. Legacy hosts may omit DETAILS v2 fields.");
    else snprintf(text,sizeof(text),"%s | %s | source %s | USB %s%s%s",s.account_name,states[s.state],source_age,usb_age,stale?" | SOURCE OLD":"",transport?" | USB STALE":"");
    lv_label_set_text(age_label,text);
    lv_obj_set_style_text_color(title,color(s.test?0xFF9977:0xEDF5FF),0);
    lv_obj_set_style_text_color(age_label,color(stale||transport?0xFFBB66:0x8293AD),0);
    lv_label_set_text(scope,s.present?s.scope:"Read-only host cache; refresh runs independently");
    if(s.present&&s.reason[0]&&(s.state!=1||stale))snprintf(text,sizeof(text),"Reason: %s",s.reason);else snprintf(text,sizeof(text),"%s",s.present?s.note:"Panel navigation never triggers API reads or commands");
    lv_label_set_text(note,text);
    for(int i=0;i<4;i++){
        bool visible=s.present&&i<s.row_count;
        lv_label_set_text(metric_labels[i],visible?s.rows[i].label:"--");
        char display_unit[16];
        if(visible)metric_text(text,sizeof(text),display_unit,sizeof(display_unit),&s.rows[i]);else{snprintf(text,sizeof(text),"--");snprintf(display_unit,sizeof(display_unit),"--");}
        lv_label_set_text(metric_units[i],display_unit);
        lv_label_set_text(metric_values[i],text);
        lv_obj_set_style_text_color(metric_values[i],color(stale||transport?0x8293AD:0xEDF5FF),0);
    }
    if(s.present&&s.total_pages)snprintf(text,sizeof(text),"Card %d / %d | %s",s.page+1,s.total_pages,kinds[q.kind]);else snprintf(text,sizeof(text),"-- / -- | %s",kinds[q.kind]);
    lv_label_set_text(pages,text);
    if(!s.present||s.page<=0)lv_obj_add_state(previous,LV_STATE_DISABLED);else lv_obj_remove_state(previous,LV_STATE_DISABLED);
    if(!s.present||s.page+1>=s.total_pages)lv_obj_add_state(next,LV_STATE_DISABLED);else lv_obj_remove_state(next,LV_STATE_DISABLED);
}
