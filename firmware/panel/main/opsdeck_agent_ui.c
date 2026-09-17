#include "opsdeck_agent_ui.h"
#include "opsdeck_agent_control.h"
#include "opsdeck_keyboard.h"
#include <string.h>
#include <stdio.h>
#include <stdint.h>

#define CARD 0x121E30
#define EDGE 0x23354C
#define INK 0xEDF5FF
#define MUTED 0x8293AD
#define CYAN 0x44D7EE
#define PURPLE 0xB39AFF
#define GREEN 0x73E0A9
#define AMBER 0xFFD166
#define RED 0xFF5C5C

static lv_obj_t *root,*status_label,*repo_label,*mode_button,*mode_label,*confirm_button,*cancel_button,*history_label[3];
static lv_obj_t *composer,*composer_ta,*composer_kb,*composer_note;
static int selected_workspace,selected_sandbox;
static lv_color_t color(uint32_t v){return lv_color_hex(v);}
static lv_obj_t *label(lv_obj_t *p,int x,int y,int w,const char *s,const lv_font_t *font,uint32_t c)
{
 lv_obj_t *o=lv_label_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,w);lv_label_set_text(o,s);lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(c),0);lv_label_set_long_mode(o,LV_LABEL_LONG_DOT);return o;
}
static lv_obj_t *button(lv_obj_t *p,int x,int y,int w,int h,const char *s,lv_event_cb_t cb,void *data,lv_obj_t **text_out)
{
 lv_obj_t *b=lv_button_create(p);lv_obj_set_pos(b,x,y);lv_obj_set_size(b,w,h);lv_obj_set_style_bg_color(b,color(EDGE),0);lv_obj_set_style_shadow_width(b,0,0);lv_obj_set_style_radius(b,10,0);
 lv_obj_t *t=lv_label_create(b);lv_label_set_text(t,s);lv_obj_set_style_text_font(t,&lv_font_montserrat_12,0);lv_obj_set_style_text_color(t,color(INK),0);lv_obj_center(t);if(text_out)*text_out=t;
 lv_obj_add_event_cb(b,cb,LV_EVENT_CLICKED,data);return b;
}
static void update_mode(void){if(mode_label)lv_label_set_text(mode_label,selected_sandbox?"WRITE":"READ ONLY");}
static void close_composer(void)
{
 if(composer){lv_obj_delete_async(composer);composer=NULL;composer_ta=composer_kb=composer_note=NULL;}
}
static void composer_event(lv_event_t *e)
{
 lv_event_code_t code=lv_event_get_code(e);if(code==LV_EVENT_CANCEL){close_composer();return;}if(code!=LV_EVENT_READY)return;
 opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);if(!s.present||s.workspace_count<=0||selected_workspace<0||selected_workspace>=s.workspace_count){if(composer_note)lv_label_set_text(composer_note,"No workspace from host");return;}
 const char *prompt=lv_textarea_get_text(composer_ta);size_t bytes=strlen(prompt);if(!bytes||bytes>OPSDECK_AGENT_PROMPT_MAX){if(composer_note)lv_label_set_text(composer_note,"Prompt required / max 2048 bytes");return;}
 int request=opsdeck_agent_send_prompt(prompt,s.workspaces[selected_workspace].id,selected_sandbox);if(!request){if(composer_note)lv_label_set_text(composer_note,"Prompt could not be sent");return;}close_composer();
}
void opsdeck_agent_ui_open_new_task(void)
{
 opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);if(!s.present||s.workspace_count<=0)return;if(selected_workspace>=s.workspace_count)selected_workspace=0;
 composer=lv_obj_create(lv_screen_active());lv_obj_set_pos(composer,0,0);lv_obj_set_size(composer,800,480);lv_obj_set_style_bg_color(composer,color(0x090F1B),0);lv_obj_set_style_border_width(composer,0,0);lv_obj_set_style_radius(composer,0,0);lv_obj_set_style_pad_all(composer,0,0);lv_obj_remove_flag(composer,LV_OBJ_FLAG_SCROLLABLE);
 label(composer,14,8,500,"MANAGED CODEX TASK",&lv_font_montserrat_20,INK);
 char b[96];snprintf(b,sizeof(b),"Repo: %s | %s",s.workspaces[selected_workspace].name,selected_sandbox?"WORKSPACE WRITE":"READ ONLY");label(composer,14,35,760,b,&lv_font_montserrat_14,CYAN);
 composer_ta=lv_textarea_create(composer);lv_obj_set_pos(composer_ta,12,59);lv_obj_set_size(composer_ta,776,130);lv_textarea_set_placeholder_text(composer_ta,"Codex gorevini yazin...");lv_textarea_set_max_length(composer_ta,1024);
 composer_note=label(composer,14,193,760,"OK: gonder | X: kapat",&lv_font_montserrat_12,MUTED);
 composer_kb=lv_keyboard_create(composer);lv_obj_set_style_align(composer_kb,LV_ALIGN_TOP_LEFT,0);lv_obj_set_pos(composer_kb,0,232);lv_obj_set_size(composer_kb,800,240);opsdeck_keyboard_attach(composer_kb,composer_ta);lv_obj_add_event_cb(composer_kb,composer_event,LV_EVENT_READY,NULL);lv_obj_add_event_cb(composer_kb,composer_event,LV_EVENT_CANCEL,NULL);lv_obj_add_state(composer_ta,LV_STATE_FOCUSED);
}
static void new_task(lv_event_t *e){(void)e;opsdeck_agent_ui_open_new_task();}
static void repo_change(lv_event_t *e)
{
 opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);if(!s.present||s.workspace_count<=0)return;int delta=(int)(intptr_t)lv_event_get_user_data(e);selected_workspace=(selected_workspace+delta+s.workspace_count)%s.workspace_count;
}
static void mode_toggle(lv_event_t *e){(void)e;selected_sandbox=!selected_sandbox;update_mode();}
static void action_send(lv_event_t *e){int action=(int)(intptr_t)lv_event_get_user_data(e);opsdeck_agent_send_action(action);}
static void confirm_send(lv_event_t *e){(void)e;opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);if(s.present&&s.phase==2&&s.request_id>0)opsdeck_agent_confirm(s.request_id);}
static void cancel_send(lv_event_t *e){(void)e;opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);if(s.present&&s.phase==2&&s.request_id>0)opsdeck_agent_cancel(s.request_id);}
void opsdeck_agent_ui_create(lv_obj_t *parent,int x,int y,int w,int h)
{
 root=lv_obj_create(parent);lv_obj_set_pos(root,x,y);lv_obj_set_size(root,w,h);lv_obj_set_style_bg_color(root,color(CARD),0);lv_obj_set_style_border_color(root,color(EDGE),0);lv_obj_set_style_border_width(root,1,0);lv_obj_set_style_radius(root,18,0);lv_obj_set_style_pad_all(root,0,0);lv_obj_remove_flag(root,LV_OBJ_FLAG_SCROLLABLE);
 label(root,16,12,w-32,"AGENT / TOOL CONTROL",&lv_font_montserrat_16,INK);
 status_label=label(root,16,38,w-32,"Waiting for host",&lv_font_montserrat_16,MUTED);
 repo_label=label(root,16,65,w-32,"Repo: --",&lv_font_montserrat_14,CYAN);
 button(root,16,91,50,36,"<",repo_change,(void*)(intptr_t)-1,NULL);button(root,70,91,50,36,">",repo_change,(void*)(intptr_t)1,NULL);
 mode_button=button(root,126,91,112,36,"READ ONLY",mode_toggle,NULL,&mode_label);button(root,244,91,w-260,36,"NEW TASK",new_task,NULL,NULL);
 button(root,16,137,82,36,"STOP",action_send,(void*)(intptr_t)2,NULL);button(root,104,137,82,36,"RESUME",action_send,(void*)(intptr_t)3,NULL);button(root,192,137,82,36,"RETRY",action_send,(void*)(intptr_t)4,NULL);button(root,280,137,w-296,36,"HANDOFF",action_send,(void*)(intptr_t)5,NULL);
 confirm_button=button(root,16,183,(w-38)/2,38,"CONFIRM",confirm_send,NULL,NULL);cancel_button=button(root,22+(w-38)/2,183,(w-38)/2,38,"CANCEL",cancel_send,NULL,NULL);
 label(root,16,232,w-32,"RECENT COMMANDS",&lv_font_montserrat_12,MUTED);
 for(int i=0;i<3;i++)history_label[i]=label(root,16,252+i*25,w-32,"--",&lv_font_montserrat_12,MUTED);
 update_mode();
}
void opsdeck_agent_ui_refresh(int64_t now)
{
 if(!root)return;
 opsdeck_agent_control_t s;opsdeck_agent_control_copy(&s);char b[128];int age=s.received_us?(int)((now-s.received_us)/1000000):999999;bool fresh=s.present&&age<16;
 uint32_t sc=MUTED;if(!fresh)snprintf(b,sizeof(b),"CONTROL STALE");else if(s.locked)snprintf(b,sizeof(b),"LOCKED");else{const char *ph[]={"IDLE","RECEIVING","CONFIRM","EXECUTING","DONE","FAILED","CANCELLED"};snprintf(b,sizeof(b),"%s | %s",ph[(s.phase>=0&&s.phase<=6)?s.phase:0],s.result);sc=s.phase==4?GREEN:s.phase==5?RED:s.phase==2?AMBER:s.phase==3?CYAN:MUTED;}
 lv_label_set_text(status_label,b);lv_obj_set_style_text_color(status_label,color(sc),0);
 if(fresh&&s.workspace_count>0){if(selected_workspace>=s.workspace_count)selected_workspace=0;snprintf(b,sizeof(b),"Repo %d/%d: %s",selected_workspace+1,s.workspace_count,s.workspaces[selected_workspace].name);}else snprintf(b,sizeof(b),"Repo: --");lv_label_set_text(repo_label,b);
 bool confirm=fresh&&!s.locked&&s.phase==2&&s.request_id>0;if(confirm){lv_obj_remove_state(confirm_button,LV_STATE_DISABLED);lv_obj_remove_state(cancel_button,LV_STATE_DISABLED);}else{lv_obj_add_state(confirm_button,LV_STATE_DISABLED);lv_obj_add_state(cancel_button,LV_STATE_DISABLED);}
 static const char *actions[]={"--","SUBMIT","STOP","RESUME","RETRY","HANDOFF"};
 for(int i=0;i<3;i++){
  if(fresh&&i<s.history_count){opsdeck_agent_history_t *h=&s.history[i];snprintf(b,sizeof(b),"%s | state %d | %s | %ds",actions[(h->action>=0&&h->action<=5)?h->action:0],h->state,h->result,h->age_s);}else snprintf(b,sizeof(b),"--");
  lv_label_set_text(history_label[i],b);
 }
}
void opsdeck_agent_ui_clear(void)
{
 if(composer){lv_obj_delete(composer);composer=NULL;}root=status_label=repo_label=mode_button=mode_label=confirm_button=cancel_button=NULL;composer_ta=composer_kb=composer_note=NULL;for(int i=0;i<3;i++)history_label[i]=NULL;
}
