#include "opsdeck_keyboard.h"
#include "opsdeck_font.h"

#define W 4
#define S 6
#define M 5
#define KB_SPECIAL(w) ((lv_buttonmatrix_ctrl_t)(LV_KEYBOARD_CTRL_BUTTON_FLAGS | (w)))

static const char * const tr_lower[]={
 "q","w","e","r","t","y","u","ı","o","p","ğ","ü",LV_SYMBOL_BACKSPACE,"\n",
 "a","s","d","f","g","h","j","k","l","ş","i",LV_SYMBOL_NEW_LINE,"\n",
 "ABC","z","x","c","v","b","n","m","ö","ç",".",",","?","\n",
 "1#"," ",LV_SYMBOL_OK,LV_SYMBOL_CLOSE,NULL
};
static const lv_buttonmatrix_ctrl_t tr_lower_ctrl[]={
 W,W,W,W,W,W,W,W,W,W,W,W,KB_SPECIAL(S),
 W,W,W,W,W,W,W,W,W,W,W,KB_SPECIAL(S),
 KB_SPECIAL(M),W,W,W,W,W,W,W,W,W,W,W,W,
 KB_SPECIAL(3),15,KB_SPECIAL(3),KB_SPECIAL(3)
};
static const char * const tr_upper[]={
 "Q","W","E","R","T","Y","U","I","O","P","Ğ","Ü",LV_SYMBOL_BACKSPACE,"\n",
 "A","S","D","F","G","H","J","K","L","Ş","İ",LV_SYMBOL_NEW_LINE,"\n",
 "abc","Z","X","C","V","B","N","M","Ö","Ç",".",",","?","\n",
 "1#"," ",LV_SYMBOL_OK,LV_SYMBOL_CLOSE,NULL
};
static const lv_buttonmatrix_ctrl_t tr_upper_ctrl[]={
 W,W,W,W,W,W,W,W,W,W,W,W,KB_SPECIAL(S),
 W,W,W,W,W,W,W,W,W,W,W,KB_SPECIAL(S),
 KB_SPECIAL(M),W,W,W,W,W,W,W,W,W,W,W,W,
 KB_SPECIAL(3),15,KB_SPECIAL(3),KB_SPECIAL(3)
};
void opsdeck_keyboard_attach(lv_obj_t *keyboard,lv_obj_t *textarea)
{
 const lv_font_t *font=opsdeck_font_tr14();lv_keyboard_set_map(keyboard,LV_KEYBOARD_MODE_TEXT_LOWER,tr_lower,tr_lower_ctrl);
 lv_keyboard_set_map(keyboard,LV_KEYBOARD_MODE_TEXT_UPPER,tr_upper,tr_upper_ctrl);lv_keyboard_set_mode(keyboard,LV_KEYBOARD_MODE_TEXT_LOWER);
 lv_keyboard_set_textarea(keyboard,textarea);lv_obj_set_style_text_font(keyboard,font,LV_PART_ITEMS);lv_obj_set_style_text_font(textarea,font,0);
 lv_obj_set_style_pad_all(keyboard,3,LV_PART_MAIN);lv_obj_set_style_pad_row(keyboard,3,LV_PART_MAIN);lv_obj_set_style_pad_column(keyboard,3,LV_PART_MAIN);
}
