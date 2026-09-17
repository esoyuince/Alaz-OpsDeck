#include "opsdeck_sd_buffer.h"
#include <string.h>

_Static_assert(OPSDECK_SD_RECORD_V1_BYTES==80u,"record size drift");
_Static_assert(OPSDECK_SD_BUFFER_RAW_BYTES<1024u*1024u,"7d raw buffer must stay below 1 MiB");

static void put16(uint8_t *p,uint16_t v){p[0]=(uint8_t)v;p[1]=(uint8_t)(v>>8);}
static void put32(uint8_t *p,uint32_t v){for(int i=0;i<4;i++)p[i]=(uint8_t)(v>>(8*i));}
static void put64(uint8_t *p,uint64_t v){for(int i=0;i<8;i++)p[i]=(uint8_t)(v>>(8*i));}
static void putf(uint8_t *p,float v){uint32_t bits=0;memcpy(&bits,&v,sizeof(bits));put32(p,bits);}

uint32_t opsdeck_sd_buffer_crc32(const uint8_t *data,size_t len)
{
    uint32_t crc=0xffffffffu;
    for(size_t i=0;i<len;i++){
        crc^=data[i];
        for(int b=0;b<8;b++)crc=(crc>>1)^((crc&1u)?0xedb88320u:0u);
    }
    return crc^0xffffffffu;
}
size_t opsdeck_sd_buffer_encode_v1(const opsdeck_pc_t *pc,uint64_t uptime_s,
    uint8_t out[OPSDECK_SD_RECORD_V1_BYTES])
{
    if(!pc||!out||pc->sequence==0)return 0;
    const uint32_t allowed=PC_CPU|PC_INTEL|PC_GPU|PC_RAM|PC_VRAM|PC_CPU_TEMP|
        PC_CHASSIS_TEMP|PC_GPU_TEMP|PC_FAN1|PC_FAN2|PC_NETWORK;
    uint32_t valid=pc->valid&allowed;
    memset(out,0,OPSDECK_SD_RECORD_V1_BYTES);
    memcpy(out,"ODT1",4);put16(out+4,1);put16(out+6,OPSDECK_SD_RECORD_V1_BYTES);
    put32(out+8,pc->sequence);put32(out+12,valid);put64(out+16,uptime_s);
    putf(out+24,(valid&PC_CPU)?pc->cpu:0);putf(out+28,(valid&PC_INTEL)?pc->intel_gpu:0);
    putf(out+32,(valid&PC_GPU)?pc->gpu:0);putf(out+36,(valid&PC_RAM)?pc->ram_pct:0);
    putf(out+40,(valid&PC_VRAM)?pc->vram_pct:0);putf(out+44,(valid&PC_CPU_TEMP)?pc->cpu_temp:0);
    putf(out+48,(valid&PC_CHASSIS_TEMP)?pc->chassis_temp:0);putf(out+52,(valid&PC_GPU_TEMP)?pc->gpu_temp:0);
    putf(out+56,(valid&PC_FAN1)?pc->fan1_rpm:0);putf(out+60,(valid&PC_FAN2)?pc->fan2_rpm:0);
    putf(out+64,(valid&PC_NETWORK)?pc->rx_mbps:0);putf(out+68,(valid&PC_NETWORK)?pc->tx_mbps:0);
    put32(out+72,0);put32(out+76,opsdeck_sd_buffer_crc32(out,76));return OPSDECK_SD_RECORD_V1_BYTES;
}

bool opsdeck_sd_buffer_should_sample(const opsdeck_pc_t *pc,uint32_t last_sequence,
    int64_t now_us,int64_t last_sample_us)
{
    if(!pc||pc->sequence==0||pc->sequence==last_sequence||now_us<0||last_sample_us<0)return false;
    if(last_sample_us==0)return true;
    return now_us-last_sample_us>=(int64_t)OPSDECK_SD_BUFFER_INTERVAL_SECONDS*1000000;
}
