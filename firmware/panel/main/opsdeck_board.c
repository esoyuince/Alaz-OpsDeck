/* OpsDeck board bring-up. Pin/timing reference: Elecrow native Lesson03,
 * source commit 70db9eda4f0d11672508a9461de10ef9d79bd12e.
 * No external GPIO loads, buzzer, radio module, SD, or battery writes. */
#include "opsdeck_board.h"
#include <string.h>
#include "driver/i2c_master.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_rgb.h"
#include "esp_lcd_touch_gt911.h"
#include "esp_lvgl_port.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
static const char *TAG="opsdeck.board";
static i2c_master_bus_handle_t bus;

esp_err_t opsdeck_board_init(opsdeck_board_t *b)
{
    memset(b,0,sizeof(*b));
    const i2c_master_bus_config_t bc={
        .i2c_port=0, .sda_io_num=15, .scl_io_num=16,
        .clk_source=I2C_CLK_SRC_DEFAULT, .glitch_ignore_cnt=7,
        .flags.enable_internal_pullup=true,
    };
    ESP_ERROR_CHECK(i2c_new_master_bus(&bc,&bus));
    vTaskDelay(pdMS_TO_TICKS(100));
    const uint8_t known[]={0x30,0x5d,0x14,0x20};
    for(size_t i=0;i<sizeof(known);i++) {
        bool found=i2c_master_probe(bus,known[i],100)==ESP_OK;
        ESP_LOGI(TAG,"I2C address=0x%02x present=%d",known[i],found);
    }
    if(i2c_master_probe(bus,0x30,100)==ESP_OK) {
        i2c_master_dev_handle_t stc;
        const i2c_device_config_t sc={.dev_addr_length=I2C_ADDR_BIT_LEN_7,
            .device_address=0x30,.scl_speed_hz=100000};
        ESP_ERROR_CHECK(i2c_master_bus_add_device(bus,&sc,&stc));
        /* 0x10: documented backlight-on for V1.2, also bright on V1.3+.
         * Do not assume the physical PCB revision from its I2C address. */
        uint8_t on=0x10;
        b->backlight_ok=i2c_master_transmit(stc,&on,1,100)==ESP_OK;
        ESP_ERROR_CHECK(i2c_master_bus_rm_device(stc));
        ESP_LOGI(TAG,"STC common brightness command applied=%d; PCB revision unresolved",b->backlight_ok);
    } else {
        ESP_LOGW(TAG,"STC absent: no guessed expander/backlight writes");
    }
    esp_lcd_panel_handle_t panel=NULL;
    const esp_lcd_rgb_panel_config_t pc={
        .clk_src=LCD_CLK_SRC_DEFAULT,
        .timings={.pclk_hz=16000000,.h_res=800,.v_res=480,
            .hsync_pulse_width=4,.hsync_back_porch=8,.hsync_front_porch=8,
            .vsync_pulse_width=4,.vsync_back_porch=8,.vsync_front_porch=8,
            .flags.pclk_active_neg=1},
        .data_width=16,.bits_per_pixel=16,.num_fbs=2,
        .bounce_buffer_size_px=800*10,.dma_burst_size=64,
        .hsync_gpio_num=40,.vsync_gpio_num=41,.de_gpio_num=42,
        .pclk_gpio_num=39,.disp_gpio_num=-1,
        .data_gpio_nums={21,47,48,45,38,9,10,11,12,13,14,7,17,18,3,46},
        .flags.fb_in_psram=true,
    };
    ESP_ERROR_CHECK(esp_lcd_new_rgb_panel(&pc,&panel));
    ESP_ERROR_CHECK(esp_lcd_panel_reset(panel));
    ESP_ERROR_CHECK(esp_lcd_panel_init(panel));
    lvgl_port_cfg_t lc=ESP_LVGL_PORT_INIT_CONFIG();
    lc.task_affinity=1;
    lc.task_stack=16384; /* Chat/TinyTTF render path needs more than 8 KiB; M5.10-F proved stack canary overflow. */
    ESP_ERROR_CHECK(lvgl_port_init(&lc));
    const lvgl_port_display_cfg_t dc={
        .panel_handle=panel,.buffer_size=800*480,.double_buffer=true,
        .hres=800,.vres=480,.color_format=LV_COLOR_FORMAT_RGB565,
        .flags={.buff_spiram=true,.direct_mode=true},
    };
    const lvgl_port_display_rgb_cfg_t rc={.flags={.bb_mode=true,.avoid_tearing=true}};
    b->display=lvgl_port_add_disp_rgb(&dc,&rc);
    if(!b->display) return ESP_ERR_NO_MEM;
    uint8_t ta=0;
    if(i2c_master_probe(bus,0x5d,100)==ESP_OK) ta=0x5d;
    else if(i2c_master_probe(bus,0x14,100)==ESP_OK) ta=0x14;
    if(ta) {
        esp_lcd_panel_io_handle_t io;
        esp_lcd_panel_io_i2c_config_t ic=ESP_LCD_TOUCH_IO_I2C_GT911_CONFIG();
        ic.dev_addr=ta; ic.scl_speed_hz=100000;
        ESP_ERROR_CHECK(esp_lcd_new_panel_io_i2c(bus,&ic,&io));
        const esp_lcd_touch_config_t tc={.x_max=800,.y_max=480,
            .rst_gpio_num=-1,.int_gpio_num=-1};
        esp_lcd_touch_handle_t touch=NULL;
        esp_err_t tr=esp_lcd_touch_new_i2c_gt911(io,&tc,&touch);
        if(tr==ESP_OK) {
            const lvgl_port_touch_cfg_t tpc={.disp=b->display,.handle=touch};
            b->touch_ok=lvgl_port_add_touch(&tpc)!=NULL;
            b->touch_address=ta;
        } else ESP_LOGW(TAG,"GT911 initialization: %s",esp_err_to_name(tr));
    }
    ESP_LOGI(TAG,"BOARD_READY lcd=800x480 rgb565 fb=2 psram=80MHz touch=%d addr=0x%02x",b->touch_ok,b->touch_address);
    return ESP_OK;
}
