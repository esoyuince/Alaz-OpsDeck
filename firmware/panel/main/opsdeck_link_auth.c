#include "opsdeck_link_auth.h"
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include "nvs.h"
#include "esp_mac.h"
#include "esp_heap_caps.h"
#include "mbedtls/md.h"
#include "mbedtls/base64.h"

#define LINK_NAMESPACE "opslink"
#define LINK_KEY "pair_key"
#define LINK_ID "pair_id"
#define LINK_CERT "tls_cert"
#define TLS_CERT_MAX 1536

static unsigned char pair_key[32];
static char pair_id[13];
static char device_id[13];
static unsigned char *tls_cert;
static size_t tls_cert_len;
static char tls_fingerprint[65];
static bool initialized,paired,tls_ready;

static int hexv(char c){if(c>='0'&&c<='9')return c-'0';if(c>='A'&&c<='F')return c-'A'+10;return -1;}
static bool hex_to_bytes(const char *hex,unsigned char *out,size_t n)
{
    if(strlen(hex)!=n*2)return false;
    for(size_t i=0;i<n;i++){int a=hexv(hex[i*2]),b=hexv(hex[i*2+1]);if(a<0||b<0)return false;out[i]=(unsigned char)((a<<4)|b);}return true;
}
static void bytes_to_hex(const unsigned char *in,size_t n,char *out)
{
    static const char h[]="0123456789ABCDEF";for(size_t i=0;i<n;i++){out[i*2]=h[in[i]>>4];out[i*2+1]=h[in[i]&15];}out[n*2]=0;
}
static bool cert_fingerprint(const unsigned char *cert,size_t cert_n,char out[65])
{
    const mbedtls_md_info_t *sha=mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);if(!sha||!cert||cert_n<1)return false;
    unsigned char digest[32];if(mbedtls_md(sha,cert,cert_n,digest)!=0)return false;bytes_to_hex(digest,sizeof(digest),out);memset(digest,0,sizeof(digest));return true;
}
static unsigned char *cert_alloc(size_t n)
{
    unsigned char *p=heap_caps_malloc(n,MALLOC_CAP_SPIRAM|MALLOC_CAP_8BIT);if(!p)p=malloc(n);return p;
}
static void replace_tls_cert(unsigned char *next,size_t next_n,const char *fp)
{
    if(tls_cert){memset(tls_cert,0,tls_cert_len);free(tls_cert);}tls_cert=next;tls_cert_len=next_n;strlcpy(tls_fingerprint,fp,sizeof(tls_fingerprint));tls_ready=next&&next_n>0;
}
static bool hmac_two(const char *a,const char *b,unsigned char out[32])
{
    const mbedtls_md_info_t *md=mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);if(!md)return false;mbedtls_md_context_t ctx;mbedtls_md_init(&ctx);
    if(mbedtls_md_setup(&ctx,md,1)!=0){mbedtls_md_free(&ctx);return false;}bool ok=mbedtls_md_hmac_starts(&ctx,pair_key,sizeof(pair_key))==0;
    if(ok)ok=mbedtls_md_hmac_update(&ctx,(const unsigned char*)a,strlen(a))==0;
    if(ok&&b)ok=mbedtls_md_hmac_update(&ctx,(const unsigned char*)b,strlen(b))==0;
    if(ok)ok=mbedtls_md_hmac_finish(&ctx,out)==0;
    mbedtls_md_free(&ctx);
    return ok;
}
static bool fixed_mac_equal(const unsigned char expected[32],const char *hex)
{
    unsigned char got[32];if(!hex||!hex_to_bytes(hex,got,sizeof(got)))return false;unsigned char diff=0;for(size_t i=0;i<sizeof(got);i++)diff|=(unsigned char)(expected[i]^got[i]);memset(got,0,sizeof(got));return diff==0;
}
static void make_device_id(void)
{
    unsigned char mac[6]={0};if(esp_read_mac(mac,ESP_MAC_WIFI_STA)==ESP_OK)bytes_to_hex(mac,6,device_id);else strlcpy(device_id,"000000000000",sizeof(device_id));
}
static bool key_id_matches(const unsigned char key[32],const char *id)
{
    if(!id||strlen(id)!=12)return false;
    const mbedtls_md_info_t *sha=mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);if(!sha)return false;
    unsigned char digest[32];char expected[13];if(mbedtls_md(sha,key,32,digest)!=0)return false;bytes_to_hex(digest,6,expected);memset(digest,0,sizeof(digest));
    unsigned char diff=0;for(size_t i=0;i<12;i++)diff|=(unsigned char)(expected[i]^id[i]);memset(expected,0,sizeof(expected));return diff==0;
}
static void clear_pairing(void)
{
    memset(pair_key,0,sizeof(pair_key));pair_id[0]=0;paired=false;tls_ready=false;if(tls_cert){memset(tls_cert,0,tls_cert_len);free(tls_cert);tls_cert=NULL;}tls_cert_len=0;tls_fingerprint[0]=0;
}
esp_err_t opsdeck_link_auth_init(void)
{
    if(initialized)return ESP_OK;
    make_device_id();
    nvs_handle_t h;esp_err_t err=nvs_open(LINK_NAMESPACE,NVS_READONLY,&h);
    if(err==ESP_ERR_NVS_NOT_FOUND){initialized=true;return ESP_OK;}if(err!=ESP_OK)return err;
    size_t key_n=sizeof(pair_key),id_n=sizeof(pair_id);err=nvs_get_blob(h,LINK_KEY,pair_key,&key_n);
    if(err==ESP_OK&&key_n==sizeof(pair_key)){err=nvs_get_str(h,LINK_ID,pair_id,&id_n);if(err==ESP_OK&&key_id_matches(pair_key,pair_id))paired=true;}
    if(!paired){nvs_close(h);clear_pairing();initialized=true;return ESP_OK;}
    size_t cert_n=0;esp_err_t cert_err=nvs_get_blob(h,LINK_CERT,NULL,&cert_n);
    if(cert_err==ESP_OK&&cert_n>0&&cert_n<=TLS_CERT_MAX)
    {
        unsigned char *loaded=cert_alloc(cert_n);if(!loaded){nvs_close(h);return ESP_ERR_NO_MEM;}
        size_t read_n=cert_n;cert_err=nvs_get_blob(h,LINK_CERT,loaded,&read_n);
        char fp[65]={0};if(cert_err==ESP_OK&&read_n==cert_n&&cert_fingerprint(loaded,cert_n,fp))replace_tls_cert(loaded,cert_n,fp);else{memset(loaded,0,cert_n);free(loaded);}
    }
    else if(cert_err!=ESP_ERR_NVS_NOT_FOUND&&cert_err!=ESP_OK){nvs_close(h);return cert_err;}
    nvs_close(h);initialized=true;return ESP_OK;
}
bool opsdeck_link_auth_paired(void){return paired;}
bool opsdeck_link_auth_tls_ready(void){return paired&&tls_ready;}
const char *opsdeck_link_auth_pair_id(void){return paired?pair_id:"";}
const char *opsdeck_link_auth_device_id(void){return device_id;}
const char *opsdeck_link_auth_tls_fingerprint(void){return tls_ready?tls_fingerprint:"";}
const unsigned char *opsdeck_link_auth_tls_cert(size_t *out_n){if(out_n)*out_n=tls_ready?tls_cert_len:0;return tls_ready?tls_cert:NULL;}

bool opsdeck_link_auth_accept_pairing(const char *line,char *ack,size_t ack_n)
{
    const char *prefix="OPSDECK_PAIR_V2|";size_t pfx=strlen(prefix);if(!line||strncmp(line,prefix,pfx))return false;
    const char *id=line+pfx,*sep1=strchr(id,'|');if(!sep1||(size_t)(sep1-id)!=12)return false;for(size_t i=0;i<12;i++)if(hexv(id[i])<0)return false;
    const char *hex=sep1+1,*sep2=strchr(hex,'|');if(!sep2||(size_t)(sep2-hex)!=64)return false;
    const char *b64=sep2+1;size_t b64_n=strlen(b64);if(b64_n<32||b64_n>2048)return false;
    char next_id[13],key_hex[65];memcpy(next_id,id,12);next_id[12]=0;memcpy(key_hex,hex,64);key_hex[64]=0;
    unsigned char next_key[32];if(!hex_to_bytes(key_hex,next_key,sizeof(next_key))||!key_id_matches(next_key,next_id)){memset(next_key,0,sizeof(next_key));return false;}
    unsigned char *next_cert=cert_alloc(TLS_CERT_MAX);if(!next_cert){memset(next_key,0,sizeof(next_key));return false;}
    size_t next_cert_n=0;int dec=mbedtls_base64_decode(next_cert,TLS_CERT_MAX,&next_cert_n,(const unsigned char*)b64,b64_n);
    if(dec!=0||next_cert_n<200||next_cert_n>TLS_CERT_MAX){memset(next_key,0,sizeof(next_key));memset(next_cert,0,TLS_CERT_MAX);free(next_cert);return false;}
    char next_fp[65]={0};if(!cert_fingerprint(next_cert,next_cert_n,next_fp)){memset(next_key,0,sizeof(next_key));memset(next_cert,0,next_cert_n);free(next_cert);return false;}
    bool same=paired&&tls_ready&&!strcmp(pair_id,next_id)&&!strcmp(tls_fingerprint,next_fp);if(same){unsigned char diff=0;for(size_t i=0;i<sizeof(pair_key);i++)diff|=(unsigned char)(pair_key[i]^next_key[i]);same=diff==0;}
    if(same){memset(next_key,0,sizeof(next_key));memset(next_cert,0,next_cert_n);free(next_cert);if(ack&&ack_n>0)snprintf(ack,ack_n,"OPSDECK_PAIR_ACK_V2|%s|%s|%s",pair_id,device_id,tls_fingerprint);
    return true;}
    nvs_handle_t h;if(nvs_open(LINK_NAMESPACE,NVS_READWRITE,&h)!=ESP_OK){memset(next_key,0,sizeof(next_key));memset(next_cert,0,next_cert_n);free(next_cert);return false;}
    esp_err_t err=nvs_set_blob(h,LINK_KEY,next_key,sizeof(next_key));if(err==ESP_OK)err=nvs_set_str(h,LINK_ID,next_id);if(err==ESP_OK)err=nvs_set_blob(h,LINK_CERT,next_cert,next_cert_n);if(err==ESP_OK)err=nvs_commit(h);nvs_close(h);
    if(err!=ESP_OK){memset(next_key,0,sizeof(next_key));memset(next_cert,0,next_cert_n);free(next_cert);return false;}
    memcpy(pair_key,next_key,sizeof(pair_key));memset(next_key,0,sizeof(next_key));strlcpy(pair_id,next_id,sizeof(pair_id));paired=true;replace_tls_cert(next_cert,next_cert_n,next_fp);
    if(ack&&ack_n>0)snprintf(ack,ack_n,"OPSDECK_PAIR_ACK_V2|%s|%s|%s",pair_id,device_id,tls_fingerprint);
    return true;
}

bool opsdeck_link_auth_build_response(const char *challenge,char *out,size_t out_n)
{
    if(!paired||!challenge||!out)return false;
    const char *prefix="OPSDECK_CHALLENGE_V1|";size_t pfx=strlen(prefix);if(strncmp(challenge,prefix,pfx))return false;
    const char *nonce=challenge+pfx;if(strlen(nonce)!=32)return false;for(size_t i=0;i<32;i++)if(hexv(nonce[i])<0)return false;
    char msg[96];int n=snprintf(msg,sizeof(msg),"OPSDECK_AUTH_V1|%s|%s",nonce,device_id);if(n<=0||n>=(int)sizeof(msg))return false;
    const mbedtls_md_info_t *md=mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);if(!md)return false;unsigned char mac[32];
    if(mbedtls_md_hmac(md,pair_key,sizeof(pair_key),(const unsigned char*)msg,(size_t)n,mac)!=0)return false;
    char hex[65];bytes_to_hex(mac,sizeof(mac),hex);memset(mac,0,sizeof(mac));n=snprintf(out,out_n,"OPSDECK_AUTH_V1|%s|%s",device_id,hex);return n>0&&(size_t)n<out_n;
}
bool opsdeck_link_auth_verify_server(const char *challenge,const char *reply,char *nonce_out,size_t nonce_n)
{
    if(!paired||!challenge||!reply||!nonce_out||nonce_n<33)return false;
    const char *cp="OPSDECK_CHALLENGE_V1|",*rp="OPSDECK_AUTH_OK_V1|";size_t cpl=strlen(cp),rpl=strlen(rp);
    if(strncmp(challenge,cp,cpl)||strncmp(reply,rp,rpl))return false;
    const char *nonce=challenge+cpl,*mac_hex=reply+rpl;if(strlen(nonce)!=32||strlen(mac_hex)!=64)return false;
    char prefix[96];int n=snprintf(prefix,sizeof(prefix),"OPSDECK_SERVER_V1|%s|%s",nonce,device_id);if(n<=0||n>=(int)sizeof(prefix))return false;unsigned char mac[32];if(!hmac_two(prefix,NULL,mac))return false;
    bool ok=fixed_mac_equal(mac,mac_hex);memset(mac,0,sizeof(mac));if(!ok)return false;strlcpy(nonce_out,nonce,nonce_n);return true;
}
bool opsdeck_link_auth_verify_frame(const char *nonce,uint32_t sequence,const char *frame,const char *mac_hex)
{
    if(!paired||!nonce||strlen(nonce)!=32||!frame||!mac_hex)return false;
    char prefix[96];int n=snprintf(prefix,sizeof(prefix),"OPSDECK_FRAME_V1|%s|%lu|",nonce,(unsigned long)sequence);if(n<=0||n>=(int)sizeof(prefix))return false;
    unsigned char mac[32];if(!hmac_two(prefix,frame,mac))return false;bool ok=fixed_mac_equal(mac,mac_hex);memset(mac,0,sizeof(mac));return ok;
}
bool opsdeck_link_auth_verify_standby(const char *nonce,uint32_t sequence,const char *mac_hex)
{
    if(!paired||!nonce||strlen(nonce)!=32||!mac_hex)return false;
    char msg[128];int n=snprintf(msg,sizeof(msg),"OPSDECK_STANDBY_V2|%s|%lu|%s",nonce,(unsigned long)sequence,device_id);if(n<=0||n>=(int)sizeof(msg))return false;
    unsigned char mac[32];if(!hmac_two(msg,NULL,mac))return false;bool ok=fixed_mac_equal(mac,mac_hex);memset(mac,0,sizeof(mac));return ok;
}
