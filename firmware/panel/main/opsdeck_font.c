#include "opsdeck_font.h"
#include "esp_log.h"

extern const lv_font_t opsdeck_font_tr14_static;

void opsdeck_font_init(void)
{
 ESP_LOGI("opsdeck.font","Static Latin/Turkish font ready");
}
const lv_font_t *opsdeck_font_tr14(void){return &opsdeck_font_tr14_static;}
void opsdeck_font_deinit(void){}
