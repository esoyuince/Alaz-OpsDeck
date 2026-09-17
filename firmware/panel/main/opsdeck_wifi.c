#include "opsdeck_wifi.h"
#include "opsdeck_link_auth.h"
#include <string.h>
#include <stdlib.h>
#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <inttypes.h>
#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"
#include "lwip/inet.h"
#include "esp_tls.h"
#include "esp_tls_errors.h"

#define DISCOVERY_PORT 47230
#define WIFI_NAMESPACE "opswifi"
#define WIFI_SSID_KEY "ssid"
#define WIFI_PASS_KEY "pass"

static const char *TAG="opsdeck.wifi";
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_wifi_status_t status;
static esp_netif_t *sta_netif;
static bool foundation_initialized,radio_initialized;
static char configured_ssid[33];
static char configured_pass[65];
static int64_t next_retry_us;
static opsdeck_wifi_frame_handler_t frame_handler;
static volatile bool scan_done_pending;
static wifi_ap_record_t scan_records[OPSDECK_WIFI_SCAN_MAX];
static char telemetry_rx_buffer[4096];
static size_t telemetry_rx_used;
static UBaseType_t runtime_stack_min=(UBaseType_t)-1;
static void bump(void){status.sequence++;}
static void set_state(opsdeck_wifi_state_t s){portENTER_CRITICAL(&mux);status.state=s;bump();portEXIT_CRITICAL(&mux);}
void opsdeck_wifi_set_frame_handler(opsdeck_wifi_frame_handler_t handler){frame_handler=handler;}
void opsdeck_wifi_copy(opsdeck_wifi_status_t *out)
{
    if(!out)return;
    portENTER_CRITICAL(&mux);*out=status;portEXIT_CRITICAL(&mux);
}
uint32_t opsdeck_wifi_stack_min(void)
{
    UBaseType_t v=runtime_stack_min;return v==(UBaseType_t)-1?0:(uint32_t)v;
}
static bool valid_ssid(const char *s)
{
    if(!s)return false;
    size_t n=strlen(s);if(n<1||n>32)return false;
    for(size_t i=0;i<n;i++)if((unsigned char)s[i]<32)return false;
    return true;
}
static bool valid_password(const char *s)
{
    if(!s)return false;
    size_t n=strlen(s);if(n==0)return true;if(n<8||n>63)return false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)s[i];if(c<32||c>126)return false;}
    return true;
}
static esp_err_t load_credentials(void)
{
    nvs_handle_t h;esp_err_t err=nvs_open(WIFI_NAMESPACE,NVS_READONLY,&h);
    if(err==ESP_ERR_NVS_NOT_FOUND)return ESP_OK;
    if(err!=ESP_OK)return err;
    size_t ssid_n=sizeof(configured_ssid),pass_n=sizeof(configured_pass);
    err=nvs_get_str(h,WIFI_SSID_KEY,configured_ssid,&ssid_n);
    if(err==ESP_OK){esp_err_t p=nvs_get_str(h,WIFI_PASS_KEY,configured_pass,&pass_n);if(p==ESP_ERR_NVS_NOT_FOUND)configured_pass[0]=0;else if(p!=ESP_OK)err=p;}
    nvs_close(h);if(err==ESP_ERR_NVS_NOT_FOUND){configured_ssid[0]=0;configured_pass[0]=0;return ESP_OK;}return err;
}
static esp_err_t save_credentials(const char *ssid,const char *password)
{
    nvs_handle_t h;esp_err_t err=nvs_open(WIFI_NAMESPACE,NVS_READWRITE,&h);if(err!=ESP_OK)return err;
    err=nvs_set_str(h,WIFI_SSID_KEY,ssid);if(err==ESP_OK)err=nvs_set_str(h,WIFI_PASS_KEY,password);if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);return err;
}
static esp_err_t erase_credentials(void)
{
    nvs_handle_t h;esp_err_t err=nvs_open(WIFI_NAMESPACE,NVS_READWRITE,&h);
    if(err==ESP_ERR_NVS_NOT_FOUND)return ESP_OK;
    if(err!=ESP_OK)return err;
    esp_err_t a=nvs_erase_key(h,WIFI_SSID_KEY);if(a!=ESP_OK&&a!=ESP_ERR_NVS_NOT_FOUND)err=a;
    esp_err_t b=nvs_erase_key(h,WIFI_PASS_KEY);if(b!=ESP_OK&&b!=ESP_ERR_NVS_NOT_FOUND)err=b;
    if(err==ESP_OK)err=nvs_commit(h);
    nvs_close(h);return err;
}
static esp_err_t apply_config(void)
{
    if(!configured_ssid[0])return ESP_ERR_INVALID_STATE;
    wifi_config_t cfg={0};strlcpy((char*)cfg.sta.ssid,configured_ssid,sizeof(cfg.sta.ssid));strlcpy((char*)cfg.sta.password,configured_pass,sizeof(cfg.sta.password));
    cfg.sta.scan_method=WIFI_ALL_CHANNEL_SCAN;cfg.sta.sort_method=WIFI_CONNECT_AP_BY_SIGNAL;cfg.sta.threshold.authmode=configured_pass[0]?WIFI_AUTH_WPA2_PSK:WIFI_AUTH_OPEN;cfg.sta.pmf_cfg.capable=true;cfg.sta.pmf_cfg.required=false;
    return esp_wifi_set_config(WIFI_IF_STA,&cfg);
}
static void update_rssi(void)
{
    wifi_ap_record_t ap;if(esp_wifi_sta_get_ap_info(&ap)!=ESP_OK)return;
    portENTER_CRITICAL(&mux);status.rssi=ap.rssi;bump();portEXIT_CRITICAL(&mux);
}
static void process_scan_done(void)
{
    uint16_t n=OPSDECK_WIFI_SCAN_MAX;memset(scan_records,0,sizeof(scan_records));
    if(esp_wifi_scan_get_ap_records(&n,scan_records)!=ESP_OK)n=0;
    opsdeck_wifi_ap_t next[OPSDECK_WIFI_SCAN_MAX]={0};int count=0;
    for(uint16_t i=0;i<n&&count<OPSDECK_WIFI_SCAN_MAX;i++){
        if(!scan_records[i].ssid[0])continue;
        bool duplicate=false;for(int j=0;j<count;j++)if(!strcmp(next[j].ssid,(const char*)scan_records[i].ssid)){duplicate=true;break;}
        if(duplicate)continue;
        strlcpy(next[count].ssid,(const char*)scan_records[i].ssid,sizeof(next[count].ssid));next[count].rssi=scan_records[i].rssi;next[count].authmode=(int)scan_records[i].authmode;next[count].secure=scan_records[i].authmode!=WIFI_AUTH_OPEN;count++;
    }
    portENTER_CRITICAL(&mux);memcpy(status.scan,next,sizeof(next));status.scan_count=count;status.scanning=false;bump();portEXIT_CRITICAL(&mux);
}
static void wifi_event(void *arg,esp_event_base_t base,int32_t id,void *data)
{
    (void)arg;
    if(base==WIFI_EVENT&&id==WIFI_EVENT_SCAN_DONE){scan_done_pending=true;return;}
    if(base==WIFI_EVENT&&id==WIFI_EVENT_STA_DISCONNECTED){
        wifi_event_sta_disconnected_t *e=(wifi_event_sta_disconnected_t*)data;int retry=0;bool configured;
        portENTER_CRITICAL(&mux);configured=status.configured;status.connected=false;status.ip[0]=0;status.rssi=0;status.last_disconnect_reason=e?e->reason:0;
        if(configured)retry=++status.retry_count;else status.retry_count=0;status.state=configured?OPSDECK_WIFI_CONNECTING:OPSDECK_WIFI_NO_CONFIG;bump();portEXIT_CRITICAL(&mux);
        if(configured){int backoff=1<<(retry>5?5:retry);if(backoff>30)backoff=30;next_retry_us=esp_timer_get_time()+(int64_t)backoff*1000000;}else next_retry_us=0;return;
    }
    if(base==IP_EVENT&&id==IP_EVENT_STA_GOT_IP){
        ip_event_got_ip_t *e=(ip_event_got_ip_t*)data;char ip[16]={0};if(e)esp_ip4addr_ntoa(&e->ip_info.ip,ip,sizeof(ip));
        portENTER_CRITICAL(&mux);status.connected=true;status.state=OPSDECK_WIFI_CONNECTED;status.retry_count=0;status.last_disconnect_reason=0;strlcpy(status.ip,ip,sizeof(status.ip));bump();portEXIT_CRITICAL(&mux);next_retry_us=0;update_rssi();
    }
}
static bool parse_beacon(char *line,char *name,size_t name_n,uint16_t *port)
{
    const char *prefix="OPSDECK_HOST_V1|";size_t pfx=strlen(prefix);if(strncmp(line,prefix,pfx))return false;
    char *host=line+pfx,*sep=strchr(host,'|');if(!sep)return false;*sep=0;char *port_text=sep+1;
    size_t n=strlen(host);if(n<1||n>=name_n)return false;for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)host[i];if(c<32||c>126)return false;}
    char *end=NULL;long v=strtol(port_text,&end,10);if(!end||*end||v<1||v>65535)return false;strlcpy(name,host,name_n);*port=(uint16_t)v;return true;
}
static int open_discovery_socket(void)
{
    int sock=socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);if(sock<0)return -1;
    int yes=1;setsockopt(sock,SOL_SOCKET,SO_REUSEADDR,&yes,sizeof(yes));
    int flags=fcntl(sock,F_GETFL,0);if(flags<0){close(sock);return -1;}if(fcntl(sock,F_SETFL,flags|O_NONBLOCK)<0){close(sock);return -1;}
    struct sockaddr_in local={.sin_family=AF_INET,.sin_port=htons(DISCOVERY_PORT),.sin_addr.s_addr=htonl(INADDR_ANY)};
    if(bind(sock,(struct sockaddr*)&local,sizeof(local))<0){close(sock);return -1;}
    return sock;
}
static void receive_beacon(int sock)
{
    if(sock<0)return;
    char buf[160];struct sockaddr_in from={0};socklen_t fl=sizeof(from);
    int n=recvfrom(sock,buf,sizeof(buf)-1,0,(struct sockaddr*)&from,&fl);if(n<=0)return;buf[n]=0;
    opsdeck_wifi_status_t snap;opsdeck_wifi_copy(&snap);if(!snap.connected)return;
    char name[64];uint16_t port=0;if(!parse_beacon(buf,name,sizeof(name),&port))return;char ip[16]={0};inet_ntop(AF_INET,&from.sin_addr,ip,sizeof(ip));
    portENTER_CRITICAL(&mux);strlcpy(status.host_name,name,sizeof(status.host_name));strlcpy(status.host_ip,ip,sizeof(status.host_ip));status.host_port=port;status.host_seen_us=esp_timer_get_time();bump();portEXIT_CRITICAL(&mux);
}
static bool tls_write_bytes(esp_tls_t *tls,const char *data,size_t n)
{
    size_t off=0;int64_t deadline=esp_timer_get_time()+3000000;
    while(off<n){ssize_t w=esp_tls_conn_write(tls,data+off,n-off);if(w>0){off+=(size_t)w;continue;}if((w==ESP_TLS_ERR_SSL_WANT_READ||w==ESP_TLS_ERR_SSL_WANT_WRITE)&&esp_timer_get_time()<deadline){vTaskDelay(pdMS_TO_TICKS(1));continue;}return false;}return true;
}
static bool tls_send_line(esp_tls_t *tls,const char *text)
{
    return tls_write_bytes(tls,text,strlen(text))&&tls_write_bytes(tls,"\n",1);
}
static bool tls_recv_line_blocking(esp_tls_t *tls,char *out,size_t cap)
{
    size_t used=0;int64_t deadline=esp_timer_get_time()+3000000;
    while(used+1<cap&&esp_timer_get_time()<deadline){char c;ssize_t n=esp_tls_conn_read(tls,&c,1);if(n==1){if(c=='\n'){out[used]=0;return true;}if(c!='\r')out[used++]=c;continue;}if(n==ESP_TLS_ERR_SSL_WANT_READ||n==ESP_TLS_ERR_SSL_WANT_WRITE){vTaskDelay(pdMS_TO_TICKS(1));continue;}return false;}return false;
}
static esp_tls_t *open_telemetry_tls(const char *ip,uint16_t port,int *sockfd)
{
    size_t cert_n=0;const unsigned char *cert=opsdeck_link_auth_tls_cert(&cert_n);if(!cert||cert_n<1)return NULL;
    esp_tls_cfg_t cfg={0};cfg.cacert_buf=cert;cfg.cacert_bytes=(unsigned int)cert_n;cfg.skip_common_name=true;cfg.timeout_ms=6000;cfg.non_block=true;
    uint32_t free_before=(uint32_t)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),largest_before=(uint32_t)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL);int64_t started=esp_timer_get_time();
    esp_tls_t *tls=esp_tls_init();if(!tls)return NULL;int rc=esp_tls_conn_new_sync(ip,(int)strlen(ip),port,&cfg,tls);if(rc!=1){esp_tls_conn_destroy(tls);return NULL;}
    int fd=-1;if(esp_tls_get_conn_sockfd(tls,&fd)!=ESP_OK||fd<0){esp_tls_conn_destroy(tls);return NULL;}if(sockfd)*sockfd=fd;
    uint32_t free_after=(uint32_t)heap_caps_get_free_size(MALLOC_CAP_INTERNAL),largest_after=(uint32_t)heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL),min_after=(uint32_t)heap_caps_get_minimum_free_size(MALLOC_CAP_INTERNAL);
    ESP_LOGI(TAG,"TLS_PERF ms=%"PRId64" internal_before=%"PRIu32" internal_after=%"PRIu32" internal_min=%"PRIu32" largest_before=%"PRIu32" largest_after=%"PRIu32" psram_free=%u",(esp_timer_get_time()-started)/1000,free_before,free_after,min_after,largest_before,largest_after,(unsigned)heap_caps_get_free_size(MALLOC_CAP_SPIRAM));
    ESP_LOGI(TAG,"TLS connected host=%s:%u pin=%.12s",ip,(unsigned)port,opsdeck_link_auth_tls_fingerprint());return tls;
}
static bool authenticate_telemetry(esp_tls_t *tls,char nonce_out[33])
{
    char challenge[96],response[160],reply[128];if(!tls_recv_line_blocking(tls,challenge,sizeof(challenge)))return false;
    if(!opsdeck_link_auth_build_response(challenge,response,sizeof(response))||!tls_send_line(tls,response)||!tls_recv_line_blocking(tls,reply,sizeof(reply)))return false;
    return opsdeck_link_auth_verify_server(challenge,reply,nonce_out,33);
}
typedef struct {char nonce[33];uint32_t last_seq,pending_seq;char pending_mac[65];bool expect_frame;} telemetry_rx_t;
static void telemetry_status(bool authenticated,bool active,bool frame)
{
    portENTER_CRITICAL(&mux);status.telemetry_authenticated=authenticated;status.telemetry_active=active;if(frame)status.telemetry_frames++;if(authenticated)status.telemetry_seen_us=esp_timer_get_time();bump();portEXIT_CRITICAL(&mux);
}
static void close_telemetry(esp_tls_t **tls,int *sockfd)
{
    if(*tls){esp_tls_conn_destroy(*tls);*tls=NULL;}if(sockfd)*sockfd=-1;telemetry_status(false,false,false);
}
static bool pump_telemetry(esp_tls_t *tls,char *buf,size_t *used,telemetry_rx_t *rx)
{
    for(int reads=0;reads<8;reads++){
        char chunk[768];ssize_t n=esp_tls_conn_read(tls,chunk,sizeof(chunk));
        if(n==0)return false;
        if(n<0){if(n==ESP_TLS_ERR_SSL_WANT_READ||n==ESP_TLS_ERR_SSL_WANT_WRITE)return true;return false;}
        if(*used+(size_t)n>=4095)return false;
        memcpy(buf+*used,chunk,(size_t)n);*used+=(size_t)n;buf[*used]=0;
        char *start=buf,*nl;
        while((nl=strchr(start,'\n'))){
            *nl=0;if(nl>start&&nl[-1]=='\r')nl[-1]=0;
            if(!strncmp(start,"OPSDECK_STANDBY_V2|",19)){
                char *seq_text=start+19,*sep=strchr(seq_text,'|');if(!sep)return false;*sep=0;char *end=NULL;unsigned long seq=strtoul(seq_text,&end,10);const char *mac=sep+1;
                if(!end||*end||seq==0||seq>UINT32_MAX||seq<=rx->last_seq||strlen(mac)!=64||!opsdeck_link_auth_verify_standby(rx->nonce,(uint32_t)seq,mac))return false;
                rx->last_seq=(uint32_t)seq;rx->expect_frame=false;telemetry_status(true,false,false);
            }else if(!strncmp(start,"OPSDECK_FRAME_V1|",17)){
                char *seq_text=start+17,*sep=strchr(seq_text,'|');if(!sep)return false;*sep=0;char *end=NULL;unsigned long seq=strtoul(seq_text,&end,10);const char *mac=sep+1;
                if(!end||*end||seq==0||seq>UINT32_MAX||seq<=rx->last_seq||strlen(mac)!=64)return false;
                rx->pending_seq=(uint32_t)seq;strlcpy(rx->pending_mac,mac,sizeof(rx->pending_mac));rx->expect_frame=true;
            }else if(start[0]=='{'){
                if(!rx->expect_frame||!opsdeck_link_auth_verify_frame(rx->nonce,rx->pending_seq,start,rx->pending_mac))return false;
                bool accepted=frame_handler?frame_handler(start):false;rx->last_seq=rx->pending_seq;rx->expect_frame=false;telemetry_status(true,accepted,accepted);
            }else return false;
            start=nl+1;
        }
        size_t remain=*used-(size_t)(start-buf);memmove(buf,start,remain);*used=remain;buf[remain]=0;
    }
    return true;
}
static void wifi_runtime_task(void *unused)
{
    (void)unused;int discovery_sock=-1,tele_fd=-1;esp_tls_t *tele_tls=NULL;int64_t last_rssi=0,last_discovery_try=0,last_telemetry_try=0;telemetry_rx_t tele_rx={0};telemetry_rx_used=0;memset(telemetry_rx_buffer,0,sizeof(telemetry_rx_buffer));
    for(;;){
        UBaseType_t hwm=uxTaskGetStackHighWaterMark(NULL);if(hwm<runtime_stack_min)runtime_stack_min=hwm;
        int64_t now=esp_timer_get_time();opsdeck_wifi_status_t snap;opsdeck_wifi_copy(&snap);
        if(discovery_sock<0&&now-last_discovery_try>5000000){last_discovery_try=now;discovery_sock=open_discovery_socket();if(discovery_sock<0)ESP_LOGW(TAG,"DISCOVERY socket unavailable");}
        if(snap.configured&&!snap.connected&&snap.state!=OPSDECK_WIFI_ERROR&&now>=next_retry_us){
            esp_err_t err=apply_config();if(err==ESP_OK)err=esp_wifi_connect();
            if(err==ESP_OK||err==ESP_ERR_WIFI_CONN)next_retry_us=now+10000000;else{ESP_LOGW(TAG,"CONNECT start failed: %s",esp_err_to_name(err));next_retry_us=now+5000000;}
        }
        if(scan_done_pending){scan_done_pending=false;process_scan_done();opsdeck_wifi_copy(&snap);}
        if(snap.connected&&now-last_rssi>5000000){update_rssi();last_rssi=now;}
        receive_beacon(discovery_sock);opsdeck_wifi_copy(&snap);now=esp_timer_get_time();
        bool host_fresh=snap.host_seen_us>0&&now-snap.host_seen_us<8000000;
        if(snap.connected&&host_fresh&&opsdeck_link_auth_tls_ready()&&tele_tls==NULL&&now-last_telemetry_try>5000000){
            last_telemetry_try=now;telemetry_rx_used=0;memset(telemetry_rx_buffer,0,sizeof(telemetry_rx_buffer));memset(&tele_rx,0,sizeof(tele_rx));
            tele_tls=open_telemetry_tls(snap.host_ip,snap.host_port,&tele_fd);if(tele_tls){if(authenticate_telemetry(tele_tls,tele_rx.nonce))telemetry_status(true,false,false);else close_telemetry(&tele_tls,&tele_fd);}
        }
        if(tele_tls&&!pump_telemetry(tele_tls,telemetry_rx_buffer,&telemetry_rx_used,&tele_rx)){close_telemetry(&tele_tls,&tele_fd);memset(&tele_rx,0,sizeof(tele_rx));telemetry_rx_used=0;}
        if(!snap.connected&&tele_tls)close_telemetry(&tele_tls,&tele_fd);
        if(snap.host_seen_us>0&&!host_fresh){if(tele_tls)close_telemetry(&tele_tls,&tele_fd);portENTER_CRITICAL(&mux);status.host_name[0]=0;status.host_ip[0]=0;status.host_port=0;status.host_seen_us=0;bump();portEXIT_CRITICAL(&mux);}
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}
static void publish_config_state(void)
{
    portENTER_CRITICAL(&mux);status.configured=configured_ssid[0]!=0;status.radio_active=radio_initialized;strlcpy(status.ssid,configured_ssid,sizeof(status.ssid));status.state=status.configured?OPSDECK_WIFI_CONNECTING:OPSDECK_WIFI_NO_CONFIG;bump();portEXIT_CRITICAL(&mux);
}
static esp_err_t ensure_radio(void)
{
    if(radio_initialized)return ESP_OK;
    esp_err_t err=esp_netif_init();if(err!=ESP_OK&&err!=ESP_ERR_INVALID_STATE)return err;
    err=esp_event_loop_create_default();if(err!=ESP_OK&&err!=ESP_ERR_INVALID_STATE)return err;
    sta_netif=esp_netif_create_default_wifi_sta();if(!sta_netif)return ESP_FAIL;
    err=esp_netif_set_hostname(sta_netif,"alaz-opsdeck");if(err!=ESP_OK)return err;
    wifi_init_config_t cfg=WIFI_INIT_CONFIG_DEFAULT();err=esp_wifi_init(&cfg);if(err!=ESP_OK)return err;
    err=esp_wifi_set_storage(WIFI_STORAGE_RAM);if(err!=ESP_OK)return err;
    err=esp_event_handler_register(WIFI_EVENT,ESP_EVENT_ANY_ID,&wifi_event,NULL);if(err!=ESP_OK)return err;
    err=esp_event_handler_register(IP_EVENT,IP_EVENT_STA_GOT_IP,&wifi_event,NULL);if(err!=ESP_OK)return err;
    err=esp_wifi_set_mode(WIFI_MODE_STA);if(err!=ESP_OK)return err;
    err=esp_wifi_start();if(err!=ESP_OK)return err;
    radio_initialized=true;portENTER_CRITICAL(&mux);status.radio_active=true;bump();portEXIT_CRITICAL(&mux);
    if(xTaskCreate(wifi_runtime_task,"opsdeck_wifi",6144,NULL,2,NULL)!=pdPASS){set_state(OPSDECK_WIFI_ERROR);return ESP_ERR_NO_MEM;}
    return ESP_OK;
}
esp_err_t opsdeck_wifi_init(void)
{
    if(foundation_initialized)return ESP_OK;
    memset(&status,0,sizeof(status));status.state=OPSDECK_WIFI_DISABLED;
    esp_err_t err=nvs_flash_init();if(err!=ESP_OK){ESP_LOGE(TAG,"NVS init failed without erase: %s",esp_err_to_name(err));set_state(OPSDECK_WIFI_ERROR);return err;}
    esp_err_t auth_err=opsdeck_link_auth_init();if(auth_err!=ESP_OK)ESP_LOGW(TAG,"Link auth init failed: %s",esp_err_to_name(auth_err));
    err=load_credentials();if(err!=ESP_OK){ESP_LOGE(TAG,"Credential read failed: %s",esp_err_to_name(err));set_state(OPSDECK_WIFI_ERROR);return err;}
    foundation_initialized=true;publish_config_state();
    if(configured_ssid[0]){err=ensure_radio();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}err=apply_config();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}next_retry_us=esp_timer_get_time();}
    return ESP_OK;
}
esp_err_t opsdeck_wifi_scan(void)
{
    if(!foundation_initialized)return ESP_ERR_INVALID_STATE;
    esp_err_t err=ensure_radio();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}
    wifi_scan_config_t cfg={0};cfg.show_hidden=false;cfg.scan_type=WIFI_SCAN_TYPE_ACTIVE;
    portENTER_CRITICAL(&mux);status.scanning=true;status.scan_count=0;memset(status.scan,0,sizeof(status.scan));bump();portEXIT_CRITICAL(&mux);
    err=esp_wifi_scan_start(&cfg,false);if(err!=ESP_OK){portENTER_CRITICAL(&mux);status.scanning=false;bump();portEXIT_CRITICAL(&mux);}return err;
}
esp_err_t opsdeck_wifi_set_credentials(const char *ssid,const char *password)
{
    if(!foundation_initialized||!valid_ssid(ssid)||!valid_password(password))return ESP_ERR_INVALID_ARG;
    esp_err_t err=save_credentials(ssid,password);if(err!=ESP_OK)return err;
    strlcpy(configured_ssid,ssid,sizeof(configured_ssid));strlcpy(configured_pass,password,sizeof(configured_pass));publish_config_state();
    next_retry_us=esp_timer_get_time()+10000000;err=ensure_radio();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}
    esp_wifi_disconnect();err=apply_config();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}return ESP_OK;
}
esp_err_t opsdeck_wifi_connect(void)
{
    if(!foundation_initialized||!configured_ssid[0])return ESP_ERR_INVALID_STATE;
    esp_err_t err=ensure_radio();if(err!=ESP_OK){set_state(OPSDECK_WIFI_ERROR);return err;}
    err=apply_config();if(err!=ESP_OK)return err;
    portENTER_CRITICAL(&mux);status.state=OPSDECK_WIFI_CONNECTING;status.retry_count=0;bump();portEXIT_CRITICAL(&mux);
    err=esp_wifi_connect();next_retry_us=esp_timer_get_time()+10000000;return err;
}
esp_err_t opsdeck_wifi_forget(void)
{
    if(!foundation_initialized)return ESP_ERR_INVALID_STATE;
    esp_err_t err=erase_credentials();if(err!=ESP_OK)return err;if(radio_initialized)esp_wifi_disconnect();
    memset(configured_ssid,0,sizeof(configured_ssid));memset(configured_pass,0,sizeof(configured_pass));
    portENTER_CRITICAL(&mux);bool scanning=status.scanning;memset(&status,0,sizeof(status));status.scanning=scanning;status.radio_active=radio_initialized;status.state=OPSDECK_WIFI_NO_CONFIG;bump();portEXIT_CRITICAL(&mux);next_retry_us=0;return ESP_OK;
}
