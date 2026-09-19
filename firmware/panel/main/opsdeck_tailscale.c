#include "opsdeck_tailscale.h"
#include <string.h>
#include <stdio.h>
#include <stdlib.h>

#ifndef OPSDECK_TAILSCALE
#define OPSDECK_TAILSCALE 0
#endif

#if !OPSDECK_TAILSCALE
static opsdeck_tailscale_status_t status={.compiled=false,.state=OPSDECK_TS_DISABLED};
void opsdeck_tailscale_set_frame_handler(opsdeck_tailscale_frame_handler_t handler){(void)handler;}
esp_err_t opsdeck_tailscale_init(void){return ESP_OK;}
void opsdeck_tailscale_copy(opsdeck_tailscale_status_t *out){if(out)*out=status;}
bool opsdeck_tailscale_accept_provision(const char *line,char *ack,size_t ack_n){(void)line;(void)ack;(void)ack_n;return false;}
#else
#include "opsdeck_wifi.h"
#include "opsdeck_link_auth.h"
#include "microlink.h"
#include "nvs.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mbedtls/ssl.h"
#include "mbedtls/x509_crt.h"
#include "mbedtls/ctr_drbg.h"
#include "mbedtls/entropy.h"
#include "mbedtls/net_sockets.h"

#define TS_NAMESPACE "opsts"
#define TS_ENABLED_KEY "enabled"
#define TS_HOST_KEY "host"
#define TS_PORT_KEY "port"
#define TS_HOST_PORT 47231u
#define TS_AUTH_MAX 160u

static const char *TAG="opsdeck.ts";
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_tailscale_status_t status={.compiled=true,.state=OPSDECK_TS_IDLE};
static opsdeck_tailscale_frame_handler_t frame_handler;
static char configured_host[16];
static uint16_t configured_port=TS_HOST_PORT;
static char enrollment_key[TS_AUTH_MAX+1];
static volatile uint32_t config_epoch;
static bool initialized;

static void bump(void){status.sequence++;}
static void copy_status_locked(opsdeck_tailscale_status_t *out){*out=status;}
void opsdeck_tailscale_copy(opsdeck_tailscale_status_t *out)
{
    if(!out)return;
    portENTER_CRITICAL(&mux);copy_status_locked(out);portEXIT_CRITICAL(&mux);
}
void opsdeck_tailscale_set_frame_handler(opsdeck_tailscale_frame_handler_t handler){frame_handler=handler;}

static uint32_t parse_tail_ip(const char *s)
{
    unsigned a,b,c,d;char extra;
    if(!s||sscanf(s,"%u.%u.%u.%u%c",&a,&b,&c,&d,&extra)!=4)return 0;
    if(a!=100||b<64||b>127||c>255||d>255)return 0;
    return ((uint32_t)a<<24)|((uint32_t)b<<16)|((uint32_t)c<<8)|(uint32_t)d;
}
static bool valid_auth_key(const char *s)
{
    static const char prefix[]={'t','s','k','e','y','-','a','u','t','h','-',0};
    if(!s)return false;
    size_t n=strlen(s),p=strlen(prefix);if(n<20||n>TS_AUTH_MAX||strncmp(s,prefix,p))return false;
    for(size_t i=0;i<n;i++){unsigned char c=(unsigned char)s[i];if(c<33||c>126||c=='|')return false;}return true;
}
static void secure_zero(char *p,size_t n){volatile char *v=(volatile char*)p;while(n--)*v++=0;}

static esp_err_t persist_target(bool enabled,const char *host,uint16_t port)
{
    nvs_handle_t h;esp_err_t err=nvs_open(TS_NAMESPACE,NVS_READWRITE,&h);if(err!=ESP_OK)return err;
    if(!enabled){err=nvs_erase_all(h);if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);return err;}
    err=nvs_set_u8(h,TS_ENABLED_KEY,1);if(err==ESP_OK)err=nvs_set_str(h,TS_HOST_KEY,host);if(err==ESP_OK)err=nvs_set_u16(h,TS_PORT_KEY,port);if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);return err;
}
static esp_err_t load_target(void)
{
    nvs_handle_t h;esp_err_t err=nvs_open(TS_NAMESPACE,NVS_READONLY,&h);
    if(err==ESP_ERR_NVS_NOT_FOUND)return ESP_OK;
    if(err!=ESP_OK)return err;
    uint8_t enabled=0;size_t n=sizeof(configured_host);uint16_t port=TS_HOST_PORT;
    err=nvs_get_u8(h,TS_ENABLED_KEY,&enabled);
    if(err==ESP_ERR_NVS_NOT_FOUND){nvs_close(h);return ESP_OK;}
    if(err==ESP_OK&&enabled)err=nvs_get_str(h,TS_HOST_KEY,configured_host,&n);
    if(err==ESP_OK&&enabled){esp_err_t pe=nvs_get_u16(h,TS_PORT_KEY,&port);if(pe==ESP_ERR_NVS_NOT_FOUND)port=TS_HOST_PORT;else if(pe!=ESP_OK)err=pe;}
    nvs_close(h);if(err!=ESP_OK)return err;
    if(enabled&&(parse_tail_ip(configured_host)==0||port!=TS_HOST_PORT))return ESP_ERR_INVALID_STATE;
    configured_port=port;
    portENTER_CRITICAL(&mux);status.configured=enabled;status.host_port=enabled?port:0;if(enabled)strlcpy(status.host_ip,configured_host,sizeof(status.host_ip));bump();portEXIT_CRITICAL(&mux);
    return ESP_OK;
}
static void publish_ml_state(microlink_state_t s,int err)
{
    opsdeck_tailscale_state_t mapped=s==ML_STATE_CONNECTED?OPSDECK_TS_CONNECTED:(s==ML_STATE_ERROR?OPSDECK_TS_ERROR:(s==ML_STATE_IDLE?OPSDECK_TS_IDLE:OPSDECK_TS_CONNECTING));
    portENTER_CRITICAL(&mux);status.ml_state=(int)s;status.state=mapped;status.connected=s==ML_STATE_CONNECTED;status.last_error=err;if(!status.connected){status.vpn_ip[0]=0;status.direct_path=false;status.telemetry_authenticated=false;status.telemetry_active=false;}bump();portEXIT_CRITICAL(&mux);
}
static void state_cb(microlink_t *ml,microlink_state_t state,void *user_data)
{
    (void)user_data;publish_ml_state(state,0);
    if(state==ML_STATE_CONNECTED){uint32_t ip=microlink_get_vpn_ip(ml);char text[16]={0};if(ip)microlink_ip_to_str(ip,text);portENTER_CRITICAL(&mux);strlcpy(status.vpn_ip,text,sizeof(status.vpn_ip));bump();portEXIT_CRITICAL(&mux);}
}
static void update_peer_path(microlink_t *ml,uint32_t host_ip)
{
    bool direct=false;int n=microlink_get_peer_count(ml);for(int i=0;i<n;i++){microlink_peer_info_t p={0};if(microlink_get_peer_info(ml,i,&p)==ESP_OK&&p.vpn_ip==host_ip&&p.online){direct=p.direct_path;break;}}
    portENTER_CRITICAL(&mux);status.direct_path=direct;bump();portEXIT_CRITICAL(&mux);
}
static void clear_enrollment_key(void)
{
    portENTER_CRITICAL(&mux);secure_zero(enrollment_key,sizeof(enrollment_key));status.enrollment_key_in_ram=false;bump();portEXIT_CRITICAL(&mux);
}
typedef struct {
    microlink_tcp_socket_t *tcp;
    mbedtls_ssl_context ssl;
    mbedtls_ssl_config conf;
    mbedtls_x509_crt pinned;
    mbedtls_ctr_drbg_context drbg;
    mbedtls_entropy_context entropy;
    bool contexts_ready;
} ts_tls_t;

typedef struct {char nonce[33];uint32_t last_seq,pending_seq;char pending_mac[65];bool expect_frame;} ts_telemetry_rx_t;

static void telemetry_status(bool authenticated,bool active,bool frame)
{
    portENTER_CRITICAL(&mux);status.telemetry_authenticated=authenticated;status.telemetry_active=active;if(frame)status.telemetry_frames++;if(authenticated)status.last_error=0;bump();portEXIT_CRITICAL(&mux);
}
static int ts_bio_send(void *ctx,const unsigned char *buf,size_t len)
{
    ts_tls_t *t=(ts_tls_t*)ctx;if(!t||!t->tcp||!microlink_tcp_is_connected(t->tcp))return MBEDTLS_ERR_NET_CONN_RESET;
    return microlink_tcp_send(t->tcp,buf,len)==ESP_OK?(int)len:MBEDTLS_ERR_NET_SEND_FAILED;
}
static int ts_bio_recv(void *ctx,unsigned char *buf,size_t len)
{
    ts_tls_t *t=(ts_tls_t*)ctx;if(!t||!t->tcp)return MBEDTLS_ERR_NET_INVALID_CONTEXT;int n=microlink_tcp_recv(t->tcp,buf,len,10);if(n>0)return n;if(n==0)return MBEDTLS_ERR_SSL_WANT_READ;return MBEDTLS_ERR_NET_CONN_RESET;
}
static int ts_bio_recv_timeout(void *ctx,unsigned char *buf,size_t len,uint32_t timeout)
{
    ts_tls_t *t=(ts_tls_t*)ctx;if(!t||!t->tcp)return MBEDTLS_ERR_NET_INVALID_CONTEXT;int n=microlink_tcp_recv(t->tcp,buf,len,timeout?timeout:100);if(n>0)return n;if(n==0)return MBEDTLS_ERR_SSL_TIMEOUT;return MBEDTLS_ERR_NET_CONN_RESET;
}
static void close_ts_tls(ts_tls_t *t)
{
    if(!t)return;
    bool had_transport=t->tcp||t->contexts_ready;
    if(t->tcp){microlink_tcp_close(t->tcp);t->tcp=NULL;}
    if(t->contexts_ready){
        mbedtls_ssl_free(&t->ssl);mbedtls_ssl_config_free(&t->conf);mbedtls_x509_crt_free(&t->pinned);
        mbedtls_ctr_drbg_free(&t->drbg);mbedtls_entropy_free(&t->entropy);
    }
    memset(t,0,sizeof(*t));
    if(had_transport)telemetry_status(false,false,false);
}
static bool open_ts_tls(ts_tls_t *t,microlink_t *ml,uint32_t host_ip,uint16_t port)
{
    memset(t,0,sizeof(*t));t->tcp=microlink_tcp_connect(ml,host_ip,port,6000);if(!t->tcp)return false;
    mbedtls_ssl_init(&t->ssl);mbedtls_ssl_config_init(&t->conf);mbedtls_x509_crt_init(&t->pinned);mbedtls_ctr_drbg_init(&t->drbg);mbedtls_entropy_init(&t->entropy);t->contexts_ready=true;
    size_t cert_n=0;const unsigned char *cert=opsdeck_link_auth_tls_cert(&cert_n);int rc=cert&&cert_n?mbedtls_x509_crt_parse_der(&t->pinned,cert,cert_n):-1;
    if(rc==0)rc=mbedtls_ssl_config_defaults(&t->conf,MBEDTLS_SSL_IS_CLIENT,MBEDTLS_SSL_TRANSPORT_STREAM,MBEDTLS_SSL_PRESET_DEFAULT);
    const unsigned char pers[]="opsdeck-ts-tls";if(rc==0)rc=mbedtls_ctr_drbg_seed(&t->drbg,mbedtls_entropy_func,&t->entropy,pers,sizeof(pers)-1);
    if(rc==0){mbedtls_ssl_conf_rng(&t->conf,mbedtls_ctr_drbg_random,&t->drbg);mbedtls_ssl_conf_authmode(&t->conf,MBEDTLS_SSL_VERIFY_REQUIRED);mbedtls_ssl_conf_ca_chain(&t->conf,&t->pinned,NULL);mbedtls_ssl_conf_min_tls_version(&t->conf,MBEDTLS_SSL_VERSION_TLS1_2);mbedtls_ssl_conf_max_tls_version(&t->conf,MBEDTLS_SSL_VERSION_TLS1_2);mbedtls_ssl_conf_read_timeout(&t->conf,250);rc=mbedtls_ssl_setup(&t->ssl,&t->conf);}
    if(rc==0)mbedtls_ssl_set_bio(&t->ssl,t,ts_bio_send,ts_bio_recv,ts_bio_recv_timeout);
    int64_t deadline=esp_timer_get_time()+8000000;while(rc==0&&esp_timer_get_time()<deadline){rc=mbedtls_ssl_handshake(&t->ssl);if(rc==0)break;if(rc==MBEDTLS_ERR_SSL_WANT_READ||rc==MBEDTLS_ERR_SSL_WANT_WRITE||rc==MBEDTLS_ERR_SSL_TIMEOUT){rc=0;vTaskDelay(pdMS_TO_TICKS(5));continue;}break;}
    if(rc==0&&mbedtls_ssl_get_verify_result(&t->ssl)!=0)rc=-2;
    const mbedtls_x509_crt *peer=rc==0?mbedtls_ssl_get_peer_cert(&t->ssl):NULL;if(rc==0&&(!peer||peer->raw.len!=cert_n||memcmp(peer->raw.p,cert,cert_n)))rc=-3;
    if(rc!=0){ESP_LOGW(TAG,"TAILSCALE TLS failed rc=%d",rc);portENTER_CRITICAL(&mux);status.last_error=rc;bump();portEXIT_CRITICAL(&mux);close_ts_tls(t);return false;}
    ESP_LOGI(TAG,"TAILSCALE TLS connected host=%s:%u pin=%.12s",configured_host,(unsigned)port,opsdeck_link_auth_tls_fingerprint());return true;
}
static bool ts_tls_write_all(ts_tls_t *t,const char *data,size_t n)
{
    size_t off=0;int64_t deadline=esp_timer_get_time()+3000000;while(off<n&&esp_timer_get_time()<deadline){int rc=mbedtls_ssl_write(&t->ssl,(const unsigned char*)data+off,n-off);if(rc>0){off+=(size_t)rc;continue;}if(rc==MBEDTLS_ERR_SSL_WANT_READ||rc==MBEDTLS_ERR_SSL_WANT_WRITE){vTaskDelay(pdMS_TO_TICKS(2));continue;}return false;}return off==n;
}
static bool ts_tls_send_line(ts_tls_t *t,const char *line){return ts_tls_write_all(t,line,strlen(line))&&ts_tls_write_all(t,"\n",1);}
static bool ts_tls_recv_line(ts_tls_t *t,char *out,size_t cap)
{
    size_t used=0;int64_t deadline=esp_timer_get_time()+3000000;while(used+1<cap&&esp_timer_get_time()<deadline){unsigned char c=0;int rc=mbedtls_ssl_read(&t->ssl,&c,1);if(rc==1){if(c=='\n'){out[used]=0;return true;}if(c!='\r')out[used++]=(char)c;continue;}if(rc==MBEDTLS_ERR_SSL_WANT_READ||rc==MBEDTLS_ERR_SSL_WANT_WRITE||rc==MBEDTLS_ERR_SSL_TIMEOUT){vTaskDelay(pdMS_TO_TICKS(2));continue;}return false;}return false;
}
static bool authenticate_ts_telemetry(ts_tls_t *t,char nonce_out[33])
{
    char challenge[96],response[160],reply[128];if(!ts_tls_recv_line(t,challenge,sizeof(challenge)))return false;if(!opsdeck_link_auth_build_response(challenge,response,sizeof(response))||!ts_tls_send_line(t,response)||!ts_tls_recv_line(t,reply,sizeof(reply)))return false;return opsdeck_link_auth_verify_server(challenge,reply,nonce_out,33);
}
static bool pump_ts_telemetry(ts_tls_t *t,char *buf,size_t *used,ts_telemetry_rx_t *rx)
{
    for(int reads=0;reads<8;reads++){unsigned char chunk[768];int n=mbedtls_ssl_read(&t->ssl,chunk,sizeof(chunk));if(n==0)return false;if(n<0){if(n==MBEDTLS_ERR_SSL_WANT_READ||n==MBEDTLS_ERR_SSL_WANT_WRITE||n==MBEDTLS_ERR_SSL_TIMEOUT)return true;return false;}if(*used+(size_t)n>=4095)return false;memcpy(buf+*used,chunk,(size_t)n);*used+=(size_t)n;buf[*used]=0;char *start=buf,*nl;
        while((nl=strchr(start,'\n'))){*nl=0;if(nl>start&&nl[-1]=='\r')nl[-1]=0;
            if(!strncmp(start,"OPSDECK_STANDBY_V2|",19)){char *seq_text=start+19,*sep=strchr(seq_text,'|');if(!sep)return false;*sep=0;char *end=NULL;unsigned long seq=strtoul(seq_text,&end,10);const char *mac=sep+1;if(!end||*end||seq==0||seq>UINT32_MAX||seq<=rx->last_seq||strlen(mac)!=64||!opsdeck_link_auth_verify_standby(rx->nonce,(uint32_t)seq,mac))return false;rx->last_seq=(uint32_t)seq;rx->expect_frame=false;telemetry_status(true,false,false);
            }else if(!strncmp(start,"OPSDECK_FRAME_V1|",17)){char *seq_text=start+17,*sep=strchr(seq_text,'|');if(!sep)return false;*sep=0;char *end=NULL;unsigned long seq=strtoul(seq_text,&end,10);const char *mac=sep+1;if(!end||*end||seq==0||seq>UINT32_MAX||seq<=rx->last_seq||strlen(mac)!=64)return false;rx->pending_seq=(uint32_t)seq;strlcpy(rx->pending_mac,mac,sizeof(rx->pending_mac));rx->expect_frame=true;
            }else if(start[0]=='{'){if(!rx->expect_frame||!opsdeck_link_auth_verify_frame(rx->nonce,rx->pending_seq,start,rx->pending_mac))return false;bool accepted=frame_handler?frame_handler(start):false;rx->last_seq=rx->pending_seq;rx->expect_frame=false;telemetry_status(true,accepted,accepted);
            }else return false;start=nl+1;}
        size_t remain=*used-(size_t)(start-buf);memmove(buf,start,remain);*used=remain;buf[remain]=0;
    }return true;
}
static bool lan_preferred(const opsdeck_wifi_status_t *wifi,int64_t now)
{
    if(wifi->telemetry_authenticated)return true;
    return wifi->host_seen_us>0&&now>=wifi->host_seen_us&&now-wifi->host_seen_us<8000000;
}
static void destroy_ml(microlink_t **ml)
{
    if(*ml){microlink_destroy(*ml);*ml=NULL;}publish_ml_state(ML_STATE_IDLE,0);
}
static void runtime_task(void *unused)
{
    (void)unused;microlink_t *ml=NULL;ts_tls_t tele={0};ts_telemetry_rx_t tele_rx={0};char tele_buf[4096]={0};size_t tele_used=0;uint32_t seen_epoch=0;int64_t last_start_try=0,last_peer_check=0,last_telemetry_try=0;bool ml_bound_wifi=false;
    for(;;){
        opsdeck_wifi_status_t wifi;opsdeck_wifi_copy(&wifi);opsdeck_tailscale_status_t snap;opsdeck_tailscale_copy(&snap);uint32_t epoch=config_epoch;int64_t now=esp_timer_get_time();
        if(epoch!=seen_epoch){seen_epoch=epoch;close_ts_tls(&tele);destroy_ml(&ml);ml_bound_wifi=false;last_start_try=0;last_telemetry_try=0;memset(&tele_rx,0,sizeof(tele_rx));tele_used=0;}
        if(!snap.configured){close_ts_tls(&tele);if(ml)destroy_ml(&ml);ml_bound_wifi=false;vTaskDelay(pdMS_TO_TICKS(250));continue;}
        if(!wifi.connected){close_ts_tls(&tele);ml_bound_wifi=false;vTaskDelay(pdMS_TO_TICKS(250));continue;}
        if(ml&&!ml_bound_wifi){esp_err_t re=microlink_rebind(ml);if(re!=ESP_OK){ESP_LOGW(TAG,"TAILSCALE rebind failed: %s",esp_err_to_name(re));close_ts_tls(&tele);destroy_ml(&ml);}else{ESP_LOGI(TAG,"TAILSCALE rebound after Wi-Fi change");ml_bound_wifi=true;last_start_try=now;}}
        if(!ml&&now-last_start_try>10000000){
            last_start_try=now;char name[40];const char *id=opsdeck_link_auth_device_id();size_t idn=id?strlen(id):0;snprintf(name,sizeof(name),"alaz-opsdeck-%s",idn>=6?id+idn-6:"panel");uint32_t host_ip=parse_tail_ip(configured_host);microlink_config_t cfg={.auth_key=enrollment_key,.device_name=name,.enable_derp=true,.enable_stun=true,.enable_disco=true,.max_peers=4,.wifi_tx_power_dbm=0,.priority_peer_ip=host_ip};
            ml=microlink_init(&cfg);if(!ml){publish_ml_state(ML_STATE_ERROR,ESP_ERR_NO_MEM);continue;}microlink_set_state_callback(ml,state_cb,NULL);esp_err_t err=microlink_start(ml);if(err!=ESP_OK){publish_ml_state(ML_STATE_ERROR,err);microlink_destroy(ml);ml=NULL;continue;}ml_bound_wifi=true;publish_ml_state(microlink_get_state(ml),0);
        }
        bool ml_live=ml&&microlink_is_connected(ml);
        if(ml_live){
            if(snap.enrollment_key_in_ram)clear_enrollment_key();
            if(now-last_peer_check>2000000){last_peer_check=now;update_peer_path(ml,parse_tail_ip(configured_host));}
            if(lan_preferred(&wifi,now)){if(tele.tcp)close_ts_tls(&tele);}
            else if(opsdeck_link_auth_tls_ready()&&!tele.tcp&&now-last_telemetry_try>5000000){last_telemetry_try=now;tele_used=0;memset(tele_buf,0,sizeof(tele_buf));memset(&tele_rx,0,sizeof(tele_rx));if(open_ts_tls(&tele,ml,parse_tail_ip(configured_host),configured_port)){if(authenticate_ts_telemetry(&tele,tele_rx.nonce)){telemetry_status(true,false,false);ESP_LOGI(TAG,"TAILSCALE telemetry authenticated");}else close_ts_tls(&tele);}}
        }else if(tele.tcp)close_ts_tls(&tele);
        if(tele.tcp&&!pump_ts_telemetry(&tele,tele_buf,&tele_used,&tele_rx)){close_ts_tls(&tele);memset(&tele_rx,0,sizeof(tele_rx));tele_used=0;}
        if(ml&&microlink_get_state(ml)==ML_STATE_ERROR&&now-last_start_try>15000000){close_ts_tls(&tele);destroy_ml(&ml);ml_bound_wifi=false;}
        vTaskDelay(pdMS_TO_TICKS(100));
    }
}
bool opsdeck_tailscale_accept_provision(const char *line,char *ack,size_t ack_n)
{
    if(!line)return false;
    const char *enroll="OPSDECK_TS_ENROLL_V1|";size_t ep=strlen(enroll);
    if(!strncmp(line,enroll,ep)){
        const char *host=line+ep,*sep1=strchr(host,'|');if(!sep1)return true;const char *port_text=sep1+1,*sep2=strchr(port_text,'|');if(!sep2)return true;const char *key=sep2+1;
        size_t hn=(size_t)(sep1-host),pn=(size_t)(sep2-port_text);if(hn<7||hn>=sizeof(configured_host)||pn<1||pn>5||!valid_auth_key(key)){if(ack&&ack_n)snprintf(ack,ack_n,"OPSDECK_TS_ACK_V1|ERROR|INVALID");return true;}
        char next_host[16],port_buf[6];memcpy(next_host,host,hn);next_host[hn]=0;memcpy(port_buf,port_text,pn);port_buf[pn]=0;char *end=NULL;long port=strtol(port_buf,&end,10);
        if(!end||*end||port!=TS_HOST_PORT||parse_tail_ip(next_host)==0){if(ack&&ack_n)snprintf(ack,ack_n,"OPSDECK_TS_ACK_V1|ERROR|TARGET");return true;}
        esp_err_t err=persist_target(true,next_host,(uint16_t)port);if(err!=ESP_OK){if(ack&&ack_n)snprintf(ack,ack_n,"OPSDECK_TS_ACK_V1|ERROR|NVS");return true;}
        portENTER_CRITICAL(&mux);strlcpy(configured_host,next_host,sizeof(configured_host));configured_port=(uint16_t)port;secure_zero(enrollment_key,sizeof(enrollment_key));strlcpy(enrollment_key,key,sizeof(enrollment_key));status.configured=true;status.host_port=(uint16_t)port;strlcpy(status.host_ip,next_host,sizeof(status.host_ip));status.enrollment_key_in_ram=true;status.last_error=0;bump();portEXIT_CRITICAL(&mux);config_epoch++;
        if(ack&&ack_n)snprintf(ack,ack_n,"OPSDECK_TS_ACK_V1|ENROLL|%s|%u",next_host,(unsigned)port);
        return true;
    }
    if(!strcmp(line,"OPSDECK_TS_DISABLE_V1")){
        esp_err_t err=persist_target(false,"",0);portENTER_CRITICAL(&mux);secure_zero(configured_host,sizeof(configured_host));configured_port=TS_HOST_PORT;secure_zero(enrollment_key,sizeof(enrollment_key));status.configured=false;status.connected=false;status.enrollment_key_in_ram=false;status.state=OPSDECK_TS_DISABLED;status.host_ip[0]=0;status.host_port=0;status.vpn_ip[0]=0;status.last_error=err;bump();portEXIT_CRITICAL(&mux);config_epoch++;if(ack&&ack_n)snprintf(ack,ack_n,err==ESP_OK?"OPSDECK_TS_ACK_V1|DISABLED":"OPSDECK_TS_ACK_V1|ERROR|NVS");return true;
    }
    return false;
}

esp_err_t opsdeck_tailscale_init(void)
{
    if(initialized)return ESP_OK;
    initialized=true;esp_err_t err=load_target();if(err!=ESP_OK){portENTER_CRITICAL(&mux);status.state=OPSDECK_TS_ERROR;status.last_error=err;bump();portEXIT_CRITICAL(&mux);return err;}
    portENTER_CRITICAL(&mux);status.compiled=true;if(!status.configured)status.state=OPSDECK_TS_DISABLED;bump();portEXIT_CRITICAL(&mux);
    if(xTaskCreate(runtime_task,"opsdeck_ts",8192,NULL,2,NULL)!=pdPASS){portENTER_CRITICAL(&mux);status.state=OPSDECK_TS_ERROR;status.last_error=ESP_ERR_NO_MEM;bump();portEXIT_CRITICAL(&mux);return ESP_ERR_NO_MEM;}
    ESP_LOGI(TAG,"TAILSCALE foundation compiled=1 configured=%d key_persisted=0",status.configured);return ESP_OK;
}
#endif
