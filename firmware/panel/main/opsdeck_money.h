#pragma once
#include <math.h>
#include <stdio.h>
/* Preserve printf cent rounding, while bounding string composition separately. */
static inline void opsdeck_money_text(char out[32],double value)
{
    if(!isfinite(value)||value<0||value>1e16){snprintf(out,32,"--");return;}
    char full[384]; /* Holds even DBL_MAX decimal formatting without truncation. */
    snprintf(full,sizeof(full),"%.2f",value);
    snprintf(out,32,"%.24s",full); /* Accepted monetary range needs <=20 chars. */
}
