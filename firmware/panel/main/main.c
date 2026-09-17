#include "opsdeck_board.h"
#include "opsdeck_ui.h"
#include "opsdeck_status.h"
#include "opsdeck_cloud.h"
#include "opsdeck_inventory.h"
#include "opsdeck_details.h"
#include "opsdeck_agent_control.h"
#include "opsdeck_codex_sessions.h"
#include "opsdeck_processes.h"
#include "opsdeck_opsview.h"
#include "opsdeck_wifi.h"
#include "opsdeck_link_auth.h"
#include "opsdeck_sd.h"
#include <math.h>
#include <string.h>
#include <inttypes.h>
#include "cJSON.h"
#include "driver/uart.h"
#include "soc/uart_pins.h"
#include "esp_log.h"
#include "esp_system.h"
#include "esp_psram.h"
#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "esp_lvgl_port.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
static const char *TAG="opsdeck";
static uint32_t packets;
static int64_t last_usb_line_us;
static bool unique_keys(const cJSON *o)
{
    if(cJSON_IsObject(o)){
        for(const cJSON *a=o->child;a;a=a->next){
            if(!a->string)return false;
            for(const cJSON *b=a->next;b;b=b->next)if(b->string&&!strcmp(a->string,b->string))return false;
        }
    }
    for(const cJSON *child=o->child;child;child=child->next)if(!unique_keys(child))return false;
    return true;
}
static void accept_line(const char *line)
{
    int depth=0;bool quoted=false,escaped=false;
    for(const char *p=line;*p;p++){
        if(quoted){if(escaped)escaped=false;else if(*p=='\\')escaped=true;else if(*p=='"')quoted=false;}
        else if(*p=='"')quoted=true;else if(*p=='{'||*p=='['){if(++depth>8)return;}else if(*p=='}'||*p==']'){if(--depth<0)return;}
    }
    if(depth||quoted)return;
    cJSON *o=cJSON_ParseWithOpts(line,NULL,true);if(!cJSON_IsObject(o)||!unique_keys(o)){cJSON_Delete(o);return;}
    const cJSON *kind=cJSON_GetObjectItemCaseSensitive(o,"type");
    if(!cJSON_IsString(kind)){cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.status.v1")){opsdeck_status_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.cloud.v1")){opsdeck_cloud_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.details.v1")){opsdeck_details_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.agent_control.v1")){opsdeck_agent_control_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.codex_sessions.v1")){opsdeck_codex_sessions_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.codex_chat.v1")){opsdeck_codex_chat_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.inventory.v1")){opsdeck_inventory_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.processes.v1")){opsdeck_processes_accept(o);cJSON_Delete(o);return;}
    if(!strcmp(kind->valuestring,"opsdeck.opsview.v1")){opsdeck_opsview_accept(o);cJSON_Delete(o);return;}
    if(strcmp(kind->valuestring,"opsdeck.pc.v0")&&strcmp(kind->valuestring,"opsdeck.pc.v1")){cJSON_Delete(o);return;}
    opsdeck_pc_t s={0};bool accepted=opsdeck_pc_decode(o,&s);cJSON_Delete(o);if(!accepted)return;
    s.received_us=esp_timer_get_time();s.sequence=++packets;
    opsdeck_pc_publish(&s);
    if(packets==1||packets%10==0)ESP_LOGI(TAG,"PC_RX sequence=%"PRIu32" fields=0x%04"PRIx32" intel=%.1f nvidia=%.1f cpu_sensor=%d chassis_sensor=%d fan_sensor=%d volumes=%d",packets,s.valid,(double)((s.valid&PC_INTEL)?s.intel_gpu:-1),(double)((s.valid&PC_GPU)?s.gpu:-1),s.cpu_sensor,s.chassis_sensor,s.fan_sensor,s.volume_count);
}
static bool accept_wifi_frame(const char *line)
{
    int64_t now=esp_timer_get_time();if(last_usb_line_us>0&&now-last_usb_line_us<8000000)return false;accept_line(line);return true;
}
static void sd_probe(void *unused)
{
    (void)unused;opsdeck_sd_init();vTaskDelete(NULL);
}
static void serial_rx(void *unused)
{
    (void)unused;char line[4096];size_t used=0;bool overflow=false;
    uint8_t data[64];int64_t last_accepted=0;
    for(;;){
        int n=uart_read_bytes(UART_NUM_0,data,sizeof(data),pdMS_TO_TICKS(100));
        for(int i=0;i<n;i++){
            if(data[i]=='\n'){
                if(!overflow&&used){
                    line[used]=0;char ack[160];
                    if(opsdeck_link_auth_accept_pairing(line,ack,sizeof(ack))){uart_write_bytes(UART_NUM_0,ack,strlen(ack));uart_write_bytes(UART_NUM_0,"\n",1);last_usb_line_us=esp_timer_get_time();last_accepted=last_usb_line_us;}
                    else if(esp_timer_get_time()-last_accepted>100000){last_usb_line_us=esp_timer_get_time();accept_line(line);last_accepted=last_usb_line_us;}
                }
                used=0;overflow=false;
            }else if(data[i]==0){overflow=true;}else if(data[i]!='\r'){
                if(used<sizeof(line)-1&&!overflow)line[used++]=(char)data[i];else overflow=true;
            }
        }
    }
}
void app_main(void)
{
    ESP_LOGI(TAG,"BOOT version=M5.13-F idf=%s reset_reason=%d",esp_get_idf_version(),(int)esp_reset_reason());
    ESP_LOGI(TAG,"PSRAM_BYTES=%u",(unsigned)esp_psram_get_size());
    if(esp_psram_get_size()<8*1024*1024){ESP_LOGE(TAG,"PSRAM smaller than expected; stopping");return;}
    if(!heap_caps_check_integrity_all(true)){ESP_LOGE(TAG,"Initial heap integrity failed");return;}
    opsdeck_board_t board;ESP_ERROR_CHECK(opsdeck_board_init(&board));
    if(lvgl_port_lock(3000)){opsdeck_ui_init(&board);lvgl_port_unlock();}
    else {ESP_LOGE(TAG,"LVGL lock timeout");return;}
    if(xTaskCreate(sd_probe,"opsdeck_sd",6144,NULL,1,NULL)!=pdPASS)ESP_LOGW(TAG,"SD probe task allocation failed");
    /* USB transport accepts bounded telemetry plus approval-gated agent control.
       Re-apply UART0 IOMUX/line settings on every boot; do not rely on ROM/esptool residue. */
    const uart_config_t uart_cfg={
        .baud_rate=115200,.data_bits=UART_DATA_8_BITS,.parity=UART_PARITY_DISABLE,
        .stop_bits=UART_STOP_BITS_1,.flow_ctrl=UART_HW_FLOWCTRL_DISABLE,.rx_flow_ctrl_thresh=0,
        .source_clk=UART_SCLK_DEFAULT,
    };
    ESP_ERROR_CHECK(uart_param_config(UART_NUM_0,&uart_cfg));
    ESP_ERROR_CHECK(uart_set_pin(UART_NUM_0,U0TXD_GPIO_NUM,U0RXD_GPIO_NUM,UART_PIN_NO_CHANGE,UART_PIN_NO_CHANGE));
    ESP_ERROR_CHECK(uart_driver_install(UART_NUM_0,8192,0,0,NULL,0));
    ESP_ERROR_CHECK(uart_flush_input(UART_NUM_0));
    ESP_LOGI(TAG,"UART_READY uart=0 tx=%d rx=%d baud=115200",U0TXD_GPIO_NUM,U0RXD_GPIO_NUM);
    if(xTaskCreate(serial_rx,"opsdeck_rx",12288,NULL,2,NULL)!=pdPASS){ESP_LOGE(TAG,"RX task allocation failed");return;}
    opsdeck_wifi_set_frame_handler(accept_wifi_frame);esp_err_t wifi_err=opsdeck_wifi_init();if(wifi_err!=ESP_OK)ESP_LOGW(TAG,"Wi-Fi foundation unavailable: %s",esp_err_to_name(wifi_err));
    esp_err_t auth_err=wifi_err==ESP_OK?opsdeck_link_auth_init():ESP_ERR_INVALID_STATE;if(auth_err!=ESP_OK)ESP_LOGW(TAG,"Wi-Fi link auth unavailable: %s",esp_err_to_name(auth_err));
    ESP_LOGI(TAG,"READY PC_V1=enabled STATUS_V1=enabled CLOUD_V1=enabled INVENTORY_V1=enabled DETAILS_V1=enabled AGENT_CONTROL_V1=enabled CODEX_SESSION_V1=enabled PROCESSES_V1=enabled WIFI_FOUNDATION=%s WIFI_PAIRING=%s WIFI_TELEMETRY=auth_readonly WIFI_CONTROL=disabled USB_CONTROL=enabled",wifi_err==ESP_OK?"enabled":"error",opsdeck_link_auth_paired()?"paired":"waiting_usb");
    unsigned health_ticks=0;
    for(;;){
        vTaskDelay(pdMS_TO_TICKS(5000));
        opsdeck_pc_t snapshot;opsdeck_pc_copy(&snapshot);opsdeck_sd_info_t sd;opsdeck_sd_copy(&sd);
        ESP_LOGI(TAG,"HEALTH uptime_s=%"PRId64" internal_free=%u internal_min=%u psram_free=%u sd=%d packets=%"PRIu32,
            esp_timer_get_time()/1000000,(unsigned)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),
            (unsigned)heap_caps_get_minimum_free_size(MALLOC_CAP_INTERNAL),
            (unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM),(int)sd.state,snapshot.sequence);
        if(++health_ticks%12==0){opsdeck_wifi_status_t w;opsdeck_wifi_copy(&w);ESP_LOGI(TAG,"READY WIFI_MEM stack_min=%"PRIu32" tcp_auth=%d tcp_active=%d",opsdeck_wifi_stack_min(),w.telemetry_authenticated,w.telemetry_active);}
        opsdeck_details_request_current(); /* Active cached detail view. */
        opsdeck_inventory_request_current(); /* Recover the selected page after host/USB restart. */
    }
}
