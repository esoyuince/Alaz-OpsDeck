#include "opsdeck_codex_session_ui.h"
#include "opsdeck_codex_sessions.h"
#include "opsdeck_agent_ui.h"
#include "opsdeck_keyboard.h"
#include "opsdeck_font.h"
#include <string.h>
#include <stdio.h>
#include <stdint.h>
#include "esp_timer.h"
#include "esp_log.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define BG 0x090F1B
#define CARD 0x121E30
#define EDGE 0x23354C
#define INK 0xEDF5FF
#define MUTED 0x8293AD
#define CYAN 0x44D7EE
#define PURPLE 0xB39AFF
#define GREEN 0x73E0A9
#define AMBER 0xFFBB66
#define RED 0xFF6B6B
#define LIST_ROWS 4

static lv_obj_t *root,*list_view,*chat_view,*chat_box,*title_label,*subtitle_label,*page_label,*approval_box,*approval_text;
static lv_obj_t *session_btn[LIST_ROWS],*session_text[LIST_ROWS];
static lv_obj_t *older_btn,*newer_btn,*reply_btn,*stop_btn,*resume_btn,*retry_btn,*allow_btn,*deny_btn;
static lv_obj_t *chat_bubble[OPSDECK_CODEX_CHAT_MESSAGES],*chat_who[OPSDECK_CODEX_CHAT_MESSAGES],*chat_msg[OPSDECK_CODEX_CHAT_MESSAGES],*chat_empty;
static lv_obj_t *composer,*composer_ta,*composer_kb,*composer_note;
static int mode,list_page,chat_page;static char selected_key[17],selected_repo[25];static uint32_t last_sessions_seq,last_chat_seq;static int64_t last_chat_request_us;
static lv_color_t color(uint32_t n){return lv_color_hex(n);}
static lv_obj_t *label(lv_obj_t *p,int x,int y,int w,const char *value,const lv_font_t *font,uint32_t c)
{
 lv_obj_t *o=lv_label_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_width(o,w);lv_label_set_text(o,value);lv_obj_set_style_text_font(o,font,0);lv_obj_set_style_text_color(o,color(c),0);return o;
}
static lv_obj_t *button(lv_obj_t *p,int x,int y,int w,int h,const char *value,lv_event_cb_t cb,void *data,lv_obj_t **out_label)
{
 lv_obj_t *o=lv_button_create(p);lv_obj_set_pos(o,x,y);lv_obj_set_size(o,w,h);lv_obj_set_style_bg_color(o,color(CARD),0);lv_obj_set_style_border_color(o,color(EDGE),0);lv_obj_set_style_border_width(o,1,0);lv_obj_set_style_radius(o,12,0);lv_obj_set_style_shadow_width(o,0,0);
 lv_obj_t *l=lv_label_create(o);lv_label_set_text(l,value);lv_obj_set_style_text_font(l,&lv_font_montserrat_14,0);lv_obj_set_style_text_color(l,color(INK),0);lv_obj_center(l);if(out_label)*out_label=l;if(cb)lv_obj_add_event_cb(o,cb,LV_EVENT_CLICKED,data);return o;
}
static void age_text(char *out,size_t n,int sec)
{
 if(sec<60)snprintf(out,n,"%ds",sec);else if(sec<3600)snprintf(out,n,"%dm",sec/60);else if(sec<86400)snprintf(out,n,"%dh",sec/3600);else snprintf(out,n,"%dd",sec/86400);
}
static void set_hidden(lv_obj_t *o,bool hidden){if(!o)return;if(hidden)lv_obj_add_flag(o,LV_OBJ_FLAG_HIDDEN);else lv_obj_remove_flag(o,LV_OBJ_FLAG_HIDDEN);}
#define COMPOSER_KB_Y 218
#define COMPOSER_KB_H 226
#define COMPOSER_KB_HIDDEN_Y 480
static void show_composer_keyboard(void)
{
 if(!composer_kb)return;
 lv_obj_remove_flag(composer_kb,LV_OBJ_FLAG_HIDDEN);
 lv_obj_set_style_align(composer_kb,LV_ALIGN_TOP_LEFT,0);
 lv_obj_set_y(composer_kb,COMPOSER_KB_Y);
 lv_obj_move_foreground(composer_kb);
 lv_obj_update_layout(composer);
 lv_area_t a;lv_obj_get_coords(composer_kb,&a);
 lv_obj_invalidate(composer_kb);
 ESP_LOGI("opsdeck.ui","CODEX_COMPOSER keyboard=show y=%d h=%d coords=%d,%d,%d,%d",COMPOSER_KB_Y,COMPOSER_KB_H,(int)a.x1,(int)a.y1,(int)a.x2,(int)a.y2);
}
static void composer_textarea_event(lv_event_t *e)
{
 lv_event_code_t code=lv_event_get_code(e);if(code==LV_EVENT_FOCUSED||code==LV_EVENT_CLICKED||code==LV_EVENT_PRESSED)show_composer_keyboard();
}
static void close_composer(void){if(composer){lv_obj_delete_async(composer);composer=NULL;composer_ta=composer_kb=composer_note=NULL;}}
bool opsdeck_codex_session_ui_is_open(void){return root!=NULL;}
static void show_chat_loading(const char *message)
{
 if(!chat_empty)return;
 for(int i=0;i<OPSDECK_CODEX_CHAT_MESSAGES;i++)set_hidden(chat_bubble[i],true);
 set_hidden(chat_empty,false);lv_label_set_text(chat_empty,message);lv_obj_scroll_to_y(chat_box,0,LV_ANIM_OFF);
}
static void request_chat(void)
{
 if(selected_key[0]){opsdeck_codex_chat_request(selected_key,chat_page);last_chat_request_us=esp_timer_get_time();}
}
static void show_list(void){mode=0;set_hidden(list_view,false);set_hidden(chat_view,true);}
static void show_chat(void)
{
 mode=1;ESP_LOGI("opsdeck.ui","CODEX_UI stage=show_chat heap=%u stack_min=%u",(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),(unsigned)uxTaskGetStackHighWaterMark(NULL));
 set_hidden(list_view,true);set_hidden(chat_view,false);show_chat_loading("Mesajlar yukleniyor...");request_chat();
}
static void close_event(lv_event_t *e){(void)e;opsdeck_codex_session_ui_close();}
static void new_event(lv_event_t *e){(void)e;opsdeck_agent_ui_open_new_task();}
static void back_sessions(lv_event_t *e){(void)e;show_list();}
static void list_page_event(lv_event_t *e)
{
 int delta=(int)(intptr_t)lv_event_get_user_data(e);opsdeck_codex_sessions_t s;opsdeck_codex_sessions_copy(&s);int pages=(s.session_count+LIST_ROWS-1)/LIST_ROWS;if(pages<1)pages=1;list_page=(list_page+delta+pages)%pages;last_sessions_seq=0;
}
static void session_event(lv_event_t *e)
{
 int slot=(int)(intptr_t)lv_event_get_user_data(e),idx=list_page*LIST_ROWS+slot;opsdeck_codex_sessions_t s;opsdeck_codex_sessions_copy(&s);if(idx<0||idx>=s.session_count)return;
 memcpy(selected_key,s.sessions[idx].key,sizeof(selected_key));memcpy(selected_repo,s.sessions[idx].repo,sizeof(selected_repo));selected_key[sizeof(selected_key)-1]=0;selected_repo[sizeof(selected_repo)-1]=0;
 chat_page=0;last_chat_seq=0;ESP_LOGI("opsdeck.ui","CODEX_UI stage=session_click slot=%d heap=%u",slot,(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));show_chat();
}
static void chat_page_event(lv_event_t *e)
{
 int delta=(int)(intptr_t)lv_event_get_user_data(e);opsdeck_codex_chat_t c;opsdeck_codex_chat_copy(&c);int pages=c.present?c.total_pages:1;int next=chat_page+delta;if(next<0)next=0;if(next>=pages)next=pages-1;if(next!=chat_page){chat_page=next;last_chat_seq=0;show_chat_loading("Sayfa yukleniyor...");request_chat();}
}
static void reply_composer_event(lv_event_t *e)
{
 lv_event_code_t code=lv_event_get_code(e);if(code==LV_EVENT_CANCEL){close_composer();return;}if(code!=LV_EVENT_READY)return;
 const char *prompt=lv_textarea_get_text(composer_ta);size_t bytes=strlen(prompt);if(!bytes||bytes>2048){if(composer_note)lv_label_set_text(composer_note,"Metin gerekli / en fazla 2048 byte");return;}
 if(!opsdeck_codex_send_continue(selected_key,prompt)){if(composer_note)lv_label_set_text(composer_note,"Mesaj gonderilemedi");return;}close_composer();chat_page=0;last_chat_seq=0;show_chat_loading("Codex cevabi bekleniyor...");request_chat();
}
static void reply_event(lv_event_t *e)
{
 (void)e;if(!selected_key[0]||composer)return;composer=lv_obj_create(lv_screen_active());lv_obj_set_pos(composer,0,0);lv_obj_set_size(composer,800,480);lv_obj_set_style_bg_color(composer,color(BG),0);lv_obj_set_style_border_width(composer,0,0);lv_obj_set_style_radius(composer,0,0);lv_obj_set_style_pad_all(composer,0,0);lv_obj_remove_flag(composer,LV_OBJ_FLAG_SCROLLABLE);
 char h[80];snprintf(h,sizeof(h),"CODEX DEVAM  /  %s",selected_repo);label(composer,14,10,760,h,&lv_font_montserrat_20,INK);
 composer_ta=lv_textarea_create(composer);lv_obj_set_pos(composer_ta,12,48);lv_obj_set_size(composer_ta,776,142);lv_textarea_set_placeholder_text(composer_ta,"Devam mesajini yazin...");lv_textarea_set_max_length(composer_ta,1024);lv_obj_add_event_cb(composer_ta,composer_textarea_event,LV_EVENT_ALL,NULL);
 composer_note=label(composer,14,196,760,"OK: gonder | X: kapat",&lv_font_montserrat_12,MUTED);
 composer_kb=lv_keyboard_create(composer);lv_obj_set_style_align(composer_kb,LV_ALIGN_TOP_LEFT,0);lv_obj_set_pos(composer_kb,0,COMPOSER_KB_HIDDEN_Y);lv_obj_set_size(composer_kb,800,COMPOSER_KB_H);opsdeck_keyboard_attach(composer_kb,composer_ta);lv_obj_add_event_cb(composer_kb,reply_composer_event,LV_EVENT_READY,NULL);lv_obj_add_event_cb(composer_kb,reply_composer_event,LV_EVENT_CANCEL,NULL);lv_obj_add_state(composer_ta,LV_STATE_FOCUSED);show_composer_keyboard();
}
static void action_event(lv_event_t *e){int action=(int)(intptr_t)lv_event_get_user_data(e);if(selected_key[0])opsdeck_codex_send_action(selected_key,action);}
static void approval_event(lv_event_t *e)
{
 bool allow=(bool)(intptr_t)lv_event_get_user_data(e);opsdeck_codex_sessions_t s;opsdeck_codex_sessions_copy(&s);if(s.approval_present&&s.approval.id>0)opsdeck_codex_send_approval(s.approval.id,allow);
}
static void rebuild_chat(void)
{
 opsdeck_codex_chat_t c;opsdeck_codex_chat_copy(&c);
 ESP_LOGI("opsdeck.ui","CODEX_UI stage=rebuild_start messages=%d heap=%u stack_min=%u",c.message_count,(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),(unsigned)uxTaskGetStackHighWaterMark(NULL));
 bool valid=c.present&&!strcmp(c.key,selected_key)&&c.page==chat_page;
 for(int i=0;i<OPSDECK_CODEX_CHAT_MESSAGES;i++){
  bool visible=valid&&i<c.message_count;set_hidden(chat_bubble[i],!visible);if(!visible)continue;
  bool user=c.messages[i].role==1;lv_label_set_text(chat_who[i],user?"SEN":"CODEX");lv_obj_set_style_text_color(chat_who[i],color(user?CYAN:PURPLE),0);
  lv_obj_set_style_bg_color(chat_bubble[i],color(user?0x1A2940:0x181F35),0);lv_obj_set_style_border_color(chat_bubble[i],color(user?CYAN:PURPLE),0);lv_label_set_text(chat_msg[i],c.messages[i].text);
 }
 bool empty=valid&&c.message_count==0;set_hidden(chat_empty,!empty);if(empty)lv_label_set_text(chat_empty,"Bu session icin mesaj yok.");
 if(!valid){set_hidden(chat_empty,false);lv_label_set_text(chat_empty,"Mesajlar yukleniyor...");}
 lv_obj_update_layout(chat_box);if(valid&&c.message_count>0&&chat_page==0)lv_obj_scroll_to_view(chat_bubble[c.message_count-1],LV_ANIM_OFF);else lv_obj_scroll_to_y(chat_box,0,LV_ANIM_OFF);
 ESP_LOGI("opsdeck.ui","CODEX_UI stage=rebuild_done heap=%u stack_min=%u",(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),(unsigned)uxTaskGetStackHighWaterMark(NULL));
}
void opsdeck_codex_session_ui_open(void)
{
 if(root)return;
 ESP_LOGI("opsdeck.ui","CODEX_UI stage=center_open heap=%u",(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
 root=lv_obj_create(lv_screen_active());lv_obj_set_pos(root,0,0);lv_obj_set_size(root,800,480);lv_obj_set_style_bg_color(root,color(BG),0);lv_obj_set_style_border_width(root,0,0);lv_obj_set_style_radius(root,0,0);lv_obj_set_style_pad_all(root,0,0);lv_obj_remove_flag(root,LV_OBJ_FLAG_SCROLLABLE);
 button(root,12,10,78,38,"BACK",close_event,NULL,NULL);title_label=label(root,104,12,480,"CODEX SESSIONS",&lv_font_montserrat_20,INK);subtitle_label=label(root,104,36,500,"OpsDeck-owned sessions only",&lv_font_montserrat_12,MUTED);button(root,666,10,122,38,"NEW SESSION",new_event,NULL,NULL);
 list_view=lv_obj_create(root);lv_obj_remove_style_all(list_view);lv_obj_set_pos(list_view,10,58);lv_obj_set_size(list_view,780,412);lv_obj_remove_flag(list_view,LV_OBJ_FLAG_SCROLLABLE);
 for(int i=0;i<LIST_ROWS;i++){session_btn[i]=button(list_view,4,4+i*82,772,74,"--",session_event,(void*)(intptr_t)i,&session_text[i]);lv_obj_set_width(session_text[i],738);lv_label_set_long_mode(session_text[i],LV_LABEL_LONG_WRAP);lv_obj_set_style_text_align(session_text[i],LV_TEXT_ALIGN_LEFT,0);}
 button(list_view,4,340,96,46,"< PREV",list_page_event,(void*)(intptr_t)-1,NULL);page_label=label(list_view,112,352,550,"Page 1/1",&lv_font_montserrat_14,MUTED);lv_obj_set_style_text_align(page_label,LV_TEXT_ALIGN_CENTER,0);button(list_view,680,340,96,46,"NEXT >",list_page_event,(void*)(intptr_t)1,NULL);
 chat_view=lv_obj_create(root);lv_obj_remove_style_all(chat_view);lv_obj_set_pos(chat_view,10,58);lv_obj_set_size(chat_view,780,412);lv_obj_remove_flag(chat_view,LV_OBJ_FLAG_SCROLLABLE);set_hidden(chat_view,true);
 approval_box=lv_obj_create(chat_view);lv_obj_set_pos(approval_box,4,0);lv_obj_set_size(approval_box,772,58);lv_obj_set_style_bg_color(approval_box,color(0x302615),0);lv_obj_set_style_border_color(approval_box,color(AMBER),0);lv_obj_set_style_border_width(approval_box,1,0);lv_obj_set_style_radius(approval_box,10,0);lv_obj_set_style_pad_all(approval_box,0,0);approval_text=label(approval_box,10,8,520,"Approval pending",&lv_font_montserrat_12,AMBER);allow_btn=button(approval_box,540,8,104,40,"ALLOW",approval_event,(void*)(intptr_t)1,NULL);deny_btn=button(approval_box,652,8,104,40,"DENY",approval_event,(void*)(intptr_t)0,NULL);set_hidden(approval_box,true);
 chat_box=lv_obj_create(chat_view);lv_obj_set_pos(chat_box,4,4);lv_obj_set_size(chat_box,772,300);lv_obj_set_style_bg_color(chat_box,color(0x0D1625),0);lv_obj_set_style_border_color(chat_box,color(EDGE),0);lv_obj_set_style_border_width(chat_box,1,0);lv_obj_set_style_radius(chat_box,12,0);lv_obj_set_style_pad_all(chat_box,10,0);lv_obj_set_style_pad_row(chat_box,8,0);lv_obj_set_flex_flow(chat_box,LV_FLEX_FLOW_COLUMN);lv_obj_set_scroll_dir(chat_box,LV_DIR_VER);lv_obj_set_scrollbar_mode(chat_box,LV_SCROLLBAR_MODE_AUTO);
 const lv_font_t *chat_font=opsdeck_font_tr14();
 for(int i=0;i<OPSDECK_CODEX_CHAT_MESSAGES;i++){
  chat_bubble[i]=lv_obj_create(chat_box);lv_obj_set_width(chat_bubble[i],728);lv_obj_set_height(chat_bubble[i],LV_SIZE_CONTENT);lv_obj_set_style_bg_color(chat_bubble[i],color(0x181F35),0);lv_obj_set_style_border_color(chat_bubble[i],color(PURPLE),0);lv_obj_set_style_border_width(chat_bubble[i],1,0);lv_obj_set_style_radius(chat_bubble[i],12,0);lv_obj_set_style_pad_all(chat_bubble[i],9,0);lv_obj_set_flex_flow(chat_bubble[i],LV_FLEX_FLOW_COLUMN);
  chat_who[i]=lv_label_create(chat_bubble[i]);lv_label_set_text(chat_who[i],"CODEX");lv_obj_set_style_text_font(chat_who[i],&lv_font_montserrat_12,0);lv_obj_set_style_text_color(chat_who[i],color(PURPLE),0);
  chat_msg[i]=lv_label_create(chat_bubble[i]);lv_obj_set_width(chat_msg[i],700);lv_label_set_long_mode(chat_msg[i],LV_LABEL_LONG_WRAP);lv_label_set_text(chat_msg[i],"");lv_obj_set_style_text_font(chat_msg[i],chat_font,0);lv_obj_set_style_text_color(chat_msg[i],color(INK),0);set_hidden(chat_bubble[i],true);
 }
 chat_empty=lv_label_create(chat_box);lv_label_set_text(chat_empty,"Mesajlar yukleniyor...");lv_obj_set_width(chat_empty,700);lv_label_set_long_mode(chat_empty,LV_LABEL_LONG_WRAP);lv_obj_set_style_text_font(chat_empty,chat_font,0);lv_obj_set_style_text_color(chat_empty,color(MUTED),0);
 older_btn=button(chat_view,4,316,90,42,"OLDER",chat_page_event,(void*)(intptr_t)1,NULL);newer_btn=button(chat_view,100,316,90,42,"NEWER",chat_page_event,(void*)(intptr_t)-1,NULL);reply_btn=button(chat_view,196,316,110,42,"REPLY",reply_event,NULL,NULL);stop_btn=button(chat_view,312,316,100,42,"STOP",action_event,(void*)(intptr_t)2,NULL);resume_btn=button(chat_view,418,316,112,42,"RESUME",action_event,(void*)(intptr_t)3,NULL);retry_btn=button(chat_view,536,316,100,42,"RETRY",action_event,(void*)(intptr_t)4,NULL);button(chat_view,642,316,134,42,"SESSIONS",back_sessions,NULL,NULL);
 mode=0;list_page=0;chat_page=0;selected_key[0]=0;selected_repo[0]=0;last_sessions_seq=0;last_chat_seq=0;last_chat_request_us=0;opsdeck_codex_session_ui_refresh(esp_timer_get_time());ESP_LOGI("opsdeck.ui","CODEX_UI stage=center_ready heap=%u",(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));
}
void opsdeck_codex_session_ui_close(void)
{
 close_composer();
 if(root){ESP_LOGI("opsdeck.ui","CODEX_UI stage=center_close heap=%u",(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL));lv_obj_delete_async(root);root=list_view=chat_view=chat_box=title_label=subtitle_label=page_label=approval_box=approval_text=chat_empty=NULL;for(int i=0;i<LIST_ROWS;i++)session_btn[i]=session_text[i]=NULL;for(int i=0;i<OPSDECK_CODEX_CHAT_MESSAGES;i++)chat_bubble[i]=chat_who[i]=chat_msg[i]=NULL;}
 selected_key[0]=0;selected_repo[0]=0;
}
static void refresh_list(const opsdeck_codex_sessions_t *s)
{
 int pages=(s->session_count+LIST_ROWS-1)/LIST_ROWS;if(pages<1)pages=1;if(list_page>=pages)list_page=pages-1;char b[180];snprintf(b,sizeof(b),"Page %d/%d | %d session%s",list_page+1,pages,s->session_count,s->session_count==1?"":"s");lv_label_set_text(page_label,b);
 for(int i=0;i<LIST_ROWS;i++){
  int idx=list_page*LIST_ROWS+i;bool visible=idx<s->session_count;set_hidden(session_btn[i],!visible);if(!visible)continue;const opsdeck_codex_session_t *r=&s->sessions[idx];char age[16];age_text(age,sizeof(age),r->age_s);bool approval=s->approval_present&&!strcmp(s->approval.key,r->key);
  snprintf(b,sizeof(b),"%s   %s%s\n%s  |  %s  |  %s",r->repo,r->activity,approval?"  / APPROVAL":"",r->running?"RUNNING":"READY",r->write?"WRITE":"READ ONLY",age);lv_label_set_text(session_text[i],b);lv_obj_set_style_border_color(session_btn[i],color(approval?AMBER:r->running?GREEN:EDGE),0);
 }
 if(s->approval_present)lv_label_set_text(subtitle_label,"Approval pending in an owned Codex session");else lv_label_set_text(subtitle_label,"OpsDeck-owned sessions only");
}
static void refresh_chat(const opsdeck_codex_sessions_t *s,int64_t now)
{
 opsdeck_codex_chat_t c;opsdeck_codex_chat_copy(&c);char b[180];bool approval=s->approval_present&&!strcmp(s->approval.key,selected_key);
 if(approval){set_hidden(approval_box,false);lv_obj_set_pos(chat_box,4,64);lv_obj_set_size(chat_box,772,240);snprintf(b,sizeof(b),"%s: %s",s->approval.kind,s->approval.summary);lv_label_set_text(approval_text,b);}else{set_hidden(approval_box,true);lv_obj_set_pos(chat_box,4,4);lv_obj_set_size(chat_box,772,300);}
 if(c.present&&!strcmp(c.key,selected_key)){snprintf(b,sizeof(b),"%s  |  %s  |  Page %d/%d",c.repo,c.activity,c.page+1,c.total_pages);lv_label_set_text(title_label,b);lv_label_set_text(subtitle_label,c.running?"Managed Codex turn running":"Chat history / continue on this owned session");if(c.sequence!=last_chat_seq){last_chat_seq=c.sequence;rebuild_chat();}}
 else{snprintf(b,sizeof(b),"%s  |  loading",selected_repo);lv_label_set_text(title_label,b);lv_label_set_text(subtitle_label,"Reading owned Codex thread...");}
 int pages=c.present&&!strcmp(c.key,selected_key)?c.total_pages:1;bool older=chat_page+1<pages,newer=chat_page>0;if(older)lv_obj_remove_state(older_btn,LV_STATE_DISABLED);else lv_obj_add_state(older_btn,LV_STATE_DISABLED);if(newer)lv_obj_remove_state(newer_btn,LV_STATE_DISABLED);else lv_obj_add_state(newer_btn,LV_STATE_DISABLED);
 bool valid_chat=c.present&&!strcmp(c.key,selected_key);bool running=valid_chat&&c.running;if(running){lv_obj_remove_state(stop_btn,LV_STATE_DISABLED);lv_obj_add_state(reply_btn,LV_STATE_DISABLED);lv_obj_add_state(resume_btn,LV_STATE_DISABLED);lv_obj_add_state(retry_btn,LV_STATE_DISABLED);}else{lv_obj_add_state(stop_btn,LV_STATE_DISABLED);if(valid_chat){lv_obj_remove_state(reply_btn,LV_STATE_DISABLED);lv_obj_remove_state(resume_btn,LV_STATE_DISABLED);lv_obj_remove_state(retry_btn,LV_STATE_DISABLED);}else{lv_obj_add_state(reply_btn,LV_STATE_DISABLED);lv_obj_add_state(resume_btn,LV_STATE_DISABLED);lv_obj_add_state(retry_btn,LV_STATE_DISABLED);}}
 int64_t refresh_us=chat_page==0?3000000:7000000;if(now-last_chat_request_us>refresh_us&&!composer)request_chat();
}
void opsdeck_codex_session_ui_refresh(int64_t now)
{
 if(!root)return;
 opsdeck_codex_sessions_t s;opsdeck_codex_sessions_copy(&s);if(!s.present)return;if(s.sequence!=last_sessions_seq){last_sessions_seq=s.sequence;if(mode==0)refresh_list(&s);}
 if(mode==1)refresh_chat(&s,now);
}
