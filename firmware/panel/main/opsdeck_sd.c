#include "opsdeck_sd.h"
#include <string.h>
#include "driver/gpio.h"
#include "driver/spi_master.h"
#include "driver/sdspi_host.h"
#include "esp_vfs_fat.h"
#include "sdmmc_cmd.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"

#define OPSDECK_SD_HOST SPI2_HOST
#define OPSDECK_SD_MOSI GPIO_NUM_6
#define OPSDECK_SD_MISO GPIO_NUM_4
#define OPSDECK_SD_SCK  GPIO_NUM_5
#define OPSDECK_SD_CS   GPIO_NUM_0
#define OPSDECK_SD_PATH "/sdcard"

static const char *TAG="opsdeck.sd";
static portMUX_TYPE mux=portMUX_INITIALIZER_UNLOCKED;
static opsdeck_sd_info_t info;
static sdmmc_card_t *card;

void opsdeck_sd_copy(opsdeck_sd_info_t *out)
{
    if(!out)return;
    portENTER_CRITICAL(&mux);
    *out=info;
    portEXIT_CRITICAL(&mux);
}
static void publish(opsdeck_sd_state_t state,bool mounted,uint64_t total,uint64_t free_bytes,esp_err_t err)
{
    portENTER_CRITICAL(&mux);
    info=(opsdeck_sd_info_t){state,mounted,total,free_bytes,err};
    portEXIT_CRITICAL(&mux);
}

esp_err_t opsdeck_sd_init(void)
{
    publish(OPSDECK_SD_SETUP,false,0,0,ESP_OK);
    const spi_bus_config_t bus={
        .mosi_io_num=OPSDECK_SD_MOSI,
        .miso_io_num=OPSDECK_SD_MISO,
        .sclk_io_num=OPSDECK_SD_SCK,
        .quadwp_io_num=-1,
        .quadhd_io_num=-1,
        .max_transfer_sz=4096,
    };
    esp_err_t err=spi_bus_initialize(OPSDECK_SD_HOST,&bus,SPI_DMA_CH_AUTO);
    if(err!=ESP_OK){
        publish(OPSDECK_SD_ERROR,false,0,0,err);
        ESP_LOGW(TAG,"SPI init failed: %s",esp_err_to_name(err));
        return err;
    }
    sdmmc_host_t host=SDSPI_HOST_DEFAULT();
    host.slot=OPSDECK_SD_HOST;
    host.max_freq_khz=10000;
    sdspi_device_config_t dev=SDSPI_DEVICE_CONFIG_DEFAULT();
    dev.host_id=OPSDECK_SD_HOST;
    dev.gpio_cs=OPSDECK_SD_CS;
    const esp_vfs_fat_sdmmc_mount_config_t mount={
        .format_if_mount_failed=false,
        .max_files=2,
        .allocation_unit_size=16*1024,
    };
    err=esp_vfs_fat_sdspi_mount(OPSDECK_SD_PATH,&host,&dev,&mount,&card);
    if(err!=ESP_OK){
        spi_bus_free(OPSDECK_SD_HOST);
        publish(err==ESP_ERR_NOT_FOUND?OPSDECK_SD_UNAVAILABLE:OPSDECK_SD_ERROR,false,0,0,err);
        ESP_LOGW(TAG,"SD mount unavailable: %s (no format attempted)",esp_err_to_name(err));
        return err;
    }
    uint64_t total=0,free_bytes=0;
    esp_err_t info_err=esp_vfs_fat_info(OPSDECK_SD_PATH,&total,&free_bytes);
    publish(OPSDECK_SD_READY,true,info_err==ESP_OK?total:0,info_err==ESP_OK?free_bytes:0,info_err);
    ESP_LOGI(TAG,"SD_READY total=%llu free=%llu info=%s writes=disabled",
        (unsigned long long)total,(unsigned long long)free_bytes,esp_err_to_name(info_err));
    return ESP_OK;
}
