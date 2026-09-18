#include "opsdeck_inventory_ui.h"
#include "opsdeck_inventory.h"
#include "opsdeck_cloud.h"
#include <stdio.h>
#include <string.h>
#include <inttypes.h>
static lv_obj_t *title,*coverage,*empty,*footer,*page_text,*prev,*next,*account_labels[2],*accounts[2],*views[2];
static lv_obj_t *row_buttons[4],*row_labels[4],*row_details[4];static uint32_t last_render_sequence=UINT32_MAX;static int last_render_request=-1;static int64_t last_render_second=-1;
#define BG 0x121E30
#define EDGE 0x23354C
#define INK 0xEDF5FF
#define MUTED 0x8293AD
#define ACCENT 0x44D7EE
#define GOOD 0x55D68A
#define WARN 0xFFBB66
#define BAD 0xFF6B6B
static lv_color_t color(uint32_t n){return lv_color_hex(n);}
static lv_obj_t *label(lv_obj_t *parent,int x,int y,int width,const char *value,const lv_font_t *font)
{
    lv_obj_t *o=lv_label_create(parent);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,width);
    lv_label_set_text(o,value);lv_label_set_long_mode(o,LV_LABEL_LONG_DOT);
    lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(INK),0);return o;
}
static lv_obj_t *button(lv_obj_t *parent,int x,int y,int w,int h,const char *caption,lv_event_cb_t callback,uintptr_t index)
{
    lv_obj_t *o=lv_button_create(parent);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);
    lv_obj_set_style_bg_color(o,color(BG),0);lv_obj_set_style_border_color(o,color(EDGE),0);lv_obj_set_style_border_width(o,1,0);
    lv_obj_set_style_shadow_width(o,0,0);lv_obj_set_style_radius(o,10,0);lv_obj_set_style_pad_all(o,0,0);
    lv_obj_remove_flag(o,LV_OBJ_FLAG_SCROLLABLE);
    if(caption){lv_obj_t *l=label(o,8,10,w-16,caption,&lv_font_montserrat_14);lv_obj_set_style_text_align(l,LV_TEXT_ALIGN_CENTER,0);}
    lv_obj_add_event_cb(o,callback,LV_EVENT_CLICKED,(void*)index);return o;
}
static void age_text(char *out,size_t size,int64_t seconds)
{
    if(seconds<0)snprintf(out,size,"--");
    else if(seconds<60)snprintf(out,size,"%"PRId64"s",seconds);
    else if(seconds<3600)snprintf(out,size,"%"PRId64"m",seconds/60);
    else if(seconds<86400)snprintf(out,size,"%"PRId64"h",seconds/3600);
    else snprintf(out,size,"%"PRId64"d",seconds/86400);
}
static void select_account(lv_event_t *e)
{
    opsdeck_inventory_query_t q;opsdeck_inventory_copy(NULL,&q);
    opsdeck_inventory_select((int)(uintptr_t)lv_event_get_user_data(e),q.view,0,"all");
}
static void select_view(lv_event_t *e)
{
    opsdeck_inventory_query_t q;opsdeck_inventory_copy(NULL,&q);
    opsdeck_inventory_select(q.slot,(int)(uintptr_t)lv_event_get_user_data(e),0,"all");
}
static void turn_page(lv_event_t *e)
{
    opsdeck_inventory_t s;opsdeck_inventory_query_t q;opsdeck_inventory_copy(&s,&q);
    if(!s.present)return;
    if(s.total_pages<=0)return;
    bool forward=(uintptr_t)lv_event_get_user_data(e)!=0;
    int page=forward?(s.page+1>=s.total_pages?0:s.page+1):(s.page<=0?s.total_pages-1:s.page-1);
    opsdeck_inventory_select(q.slot,q.view,page,q.group);
}
static void open_project(lv_event_t *e)
{
    opsdeck_inventory_t s;opsdeck_inventory_query_t q;opsdeck_inventory_copy(&s,&q);
    int row=(int)(uintptr_t)lv_event_get_user_data(e);
    if(!s.present||s.view!=0||row<0||row>=s.row_count)return;
    opsdeck_inventory_select(q.slot,1,0,s.rows[row].key);
}
void opsdeck_inventory_ui_clear(void)
{
    title=NULL;last_render_sequence=UINT32_MAX;last_render_request=-1;last_render_second=-1; /* Deleted page refs are not read while title==NULL. */
}
void opsdeck_inventory_ui_create(lv_obj_t *parent)
{
    for(int i=0;i<2;i++){
        accounts[i]=button(parent,4+i*180,4,172,36,NULL,select_account,(uintptr_t)i);
        account_labels[i]=label(accounts[i],8,10,156,i?"Other Projects":"VetaKeep",&lv_font_montserrat_14);
        lv_obj_set_style_text_align(account_labels[i],LV_TEXT_ALIGN_CENTER,0);
        views[i]=button(parent,370+i*198,4,190,36,i?"ALL RESOURCES":"PROJECTS",select_view,(uintptr_t)i);
    }
    title=label(parent,12,50,752,"Waiting for host",&lv_font_montserrat_16);
    coverage=label(parent,12,76,752,"Resource count unknown",&lv_font_montserrat_14);
    for(int i=0;i<4;i++){
        row_buttons[i]=button(parent,4,104+i*42,768,38,NULL,open_project,(uintptr_t)i);
        row_labels[i]=label(row_buttons[i],10,10,320,"",&lv_font_montserrat_16);
        row_details[i]=label(row_buttons[i],338,11,418,"",&lv_font_montserrat_14);
        lv_obj_add_flag(row_buttons[i],LV_OBJ_FLAG_HIDDEN);
    }
    empty=label(parent,22,154,730,"Waiting for inventory display frames",&lv_font_montserrat_16);
    prev=button(parent,4,283,90,36,LV_SYMBOL_LEFT,turn_page,0);
    next=button(parent,682,283,90,36,LV_SYMBOL_RIGHT,turn_page,1);
    page_text=label(parent,110,294,560,"-- / --",&lv_font_montserrat_14);
    lv_obj_set_style_text_align(page_text,LV_TEXT_ALIGN_CENTER,0);
    footer=label(parent,12,329,752,"Project/resource mapping only | inventory presence, not runtime health",&lv_font_montserrat_14);
    lv_obj_set_style_text_color(footer,color(MUTED),0);
    opsdeck_inventory_query_t q;opsdeck_inventory_copy(NULL,&q);
    opsdeck_inventory_select(q.slot,q.view,q.page,q.group);
}
void opsdeck_inventory_ui_refresh(int64_t now)
{
    if(!title)return;
    opsdeck_inventory_t s;opsdeck_inventory_query_t q;opsdeck_inventory_copy(&s,&q);int64_t second=now/1000000;if(s.sequence==last_render_sequence&&q.request_id==last_render_request&&second==last_render_second)return;last_render_sequence=s.sequence;last_render_request=q.request_id;last_render_second=second;
    char b[192];
    for(int i=0;i<2;i++){
        opsdeck_cloud_t a;opsdeck_cloud_copy(i,&a);
        if(a.received_us)lv_label_set_text(account_labels[i],a.name);
        lv_obj_set_style_border_color(accounts[i],color(i==q.slot?ACCENT:EDGE),0);
        lv_obj_set_style_border_color(views[i],color(i==q.view?ACCENT:EDGE),0);
    }
    const char *states[]={"SETUP","LISTED","ERROR","STALE","NO DATA","PARTIAL","DENIED"};
    const char *healths[]={"UNKNOWN","OK","ATTENTION","DEGRADED"};
    int64_t transit=s.present?(now-s.received_us)/1000000:0;
    bool stale=s.present&&(transit>=16||(s.age_s>=0&&(int64_t)s.age_s+transit>OPSDECK_INVENTORY_TTL));
    int state=stale?3:s.state;
    if(s.present&&q.view==1&&strcmp(q.group,"all")&&s.project_health_present)snprintf(b,sizeof(b),"%sPROJECT: %s | HEALTH %s | %s",s.test?"TEST DATA / ":"",s.scope,s.project_health_label[0]?s.project_health_label:healths[s.project_health],states[state]);
    else if(s.present&&q.view==1&&strcmp(q.group,"all"))snprintf(b,sizeof(b),"%sPROJECT: %s | %s",s.test?"TEST DATA / ":"",s.scope,states[state]);
    else snprintf(b,sizeof(b),"%s%s | %s",s.present&&s.test?"TEST DATA / ":"",s.present?s.scope:"Waiting for host",s.present?states[state]:"WAIT");
    lv_label_set_text(title,b);lv_obj_set_style_text_color(title,color(s.test?0xFF9977:stale?MUTED:INK),0);
    char age[24];age_text(age,sizeof(age),s.present&&s.age_s>=0?(int64_t)s.age_s+transit:-1);const char *lower=s.complete_sources<4?">=":"";
    if(!s.present)snprintf(b,sizeof(b),"No matching response yet. Counts are unknown.");
    else if(s.known_resources<0)snprintf(b,sizeof(b),"%s | inventory counts unknown | %d/4 lists",s.account_name,s.complete_sources);
    else if(q.view==0)snprintf(b,sizeof(b),"%s | %s%d %s | %s%d resources | %d/4 lists | age %s",s.account_name,lower,s.total_rows,s.total_rows==1?"group":"groups",lower,s.known_resources,s.complete_sources,age);
    else if(strcmp(q.group,"all")&&s.project_summary_present)snprintf(b,sizeof(b),"W:%d D1:%d R2:%d P:%d | OK:%d ATT:%d DEG:%d UNK:%d",s.project_workers,s.project_d1,s.project_r2,s.project_pages,s.project_ok,s.project_attention,s.project_degraded,s.project_unknown);
    else if(strcmp(q.group,"all"))snprintf(b,sizeof(b),"%s | %s%d project resources | %d/4 account lists | inventory age %s",s.account_name,lower,s.total_rows,s.complete_sources,age);
    else snprintf(b,sizeof(b),"%s | %s%d resources | %d/4 lists | inventory age %s",s.account_name,lower,s.total_rows,s.complete_sources,age);
    lv_label_set_text(coverage,b);lv_obj_set_style_text_color(coverage,color(stale?MUTED:INK),0);
    if(q.view==0)lv_label_set_text(footer,"Health: OK/PENDING/NO DATA/STALE/ATTENTION/ERROR | Pages = NO HEALTH DATA");
    else if(strcmp(q.group,"all")&&s.project_summary_present){char health_age[24];age_text(health_age,sizeof(health_age),s.project_health_age_s);snprintf(b,sizeof(b),"Health oldest %s | inventory age %s | Pages = NO HEALTH DATA",health_age,age);lv_label_set_text(footer,b);}
    else if(strcmp(q.group,"all"))lv_label_set_text(footer,"Project health uses Worker/D1/R2 only | Pages = NO HEALTH DATA");
    else lv_label_set_text(footer,"Resource health: PENDING/NO DATA/STALE/ATTENTION/ERROR/OK");
    for(int i=0;i<4;i++){
        bool visible=s.present&&i<s.row_count;
        if(visible){
            lv_obj_remove_flag(row_buttons[i],LV_OBJ_FLAG_HIDDEN);
            lv_label_set_text(row_labels[i],s.rows[i].label);lv_label_set_text(row_details[i],s.rows[i].detail);
            uint32_t hc=INK;if(s.rows[i].health_present)hc=s.rows[i].health==1?GOOD:s.rows[i].health==2?WARN:s.rows[i].health==3?BAD:MUTED;
            lv_obj_set_style_text_color(row_details[i],color(stale?MUTED:hc),0);
            lv_obj_set_style_opa(row_buttons[i],stale?LV_OPA_40:LV_OPA_COVER,0);
            if(s.view==0)lv_obj_add_flag(row_buttons[i],LV_OBJ_FLAG_CLICKABLE);else lv_obj_remove_flag(row_buttons[i],LV_OBJ_FLAG_CLICKABLE);
        }else lv_obj_add_flag(row_buttons[i],LV_OBJ_FLAG_HIDDEN);
    }
    if(s.present&&s.row_count>0)lv_obj_add_flag(empty,LV_OBJ_FLAG_HIDDEN);
    else{
        lv_obj_remove_flag(empty,LV_OBJ_FLAG_HIDDEN);
        lv_label_set_text(empty,!s.present?"Waiting for host display response":s.state==0?"Connect this account in the Windows settings":s.state==6?"Read permission denied; check token scope":s.state==2?"Inventory or saved project map unavailable":s.known_resources<0?"Run resource discovery in the Windows app":s.total_rows==0?"No rows in this selection; see source coverage":"No display rows");
    }
    snprintf(b,sizeof(b),s.present&&s.total_pages>0?"Page %d / %d  |  %d %s":"-- / --",s.page+1,s.total_pages,s.total_rows,q.view?"resources":(s.total_rows==1?"group":"groups"));
    lv_label_set_text(page_text,b);
    if(!s.present||s.total_pages<=0){lv_obj_add_state(prev,LV_STATE_DISABLED);lv_obj_add_state(next,LV_STATE_DISABLED);}
    else{lv_obj_remove_state(prev,LV_STATE_DISABLED);lv_obj_remove_state(next,LV_STATE_DISABLED);}
}
