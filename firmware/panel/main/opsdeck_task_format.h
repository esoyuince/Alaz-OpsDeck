#pragma once
#include <stddef.h>
#include <stdio.h>
#include <string.h>

static inline void opsdeck_task_count_text(char *b,size_t n,int value)
{
    if(value<0){snprintf(b,n,"--");return;}
    if(value>=1000000){snprintf(b,n,"%.1fM",value/1000000.0);return;}
    if(value>=1000){snprintf(b,n,"%.1fK",value/1000.0);return;}
    snprintf(b,n,"%d",value);
}

static inline void opsdeck_task_age_text(char *b,size_t n,int age_s)
{
    if(age_s<0){snprintf(b,n,"--");return;}
    if(age_s<60){snprintf(b,n,"%ds",age_s);return;}
    if(age_s<3600){snprintf(b,n,"%dm",age_s/60);return;}
    if(age_s<86400){snprintf(b,n,"%dh",age_s/3600);return;}
    snprintf(b,n,"%dd",age_s/86400);
}

static inline const char *opsdeck_task_event_label(const char *event_type)
{
    if(!event_type||!event_type[0])return "--";
    if(!strcmp(event_type,"created"))return "CREATED";
    if(!strcmp(event_type,"context"))return "CONTEXT";
    if(!strcmp(event_type,"claimed"))return "CLAIMED";
    if(!strcmp(event_type,"runner_claimed"))return "RUNNER CLAIMED";
    if(!strcmp(event_type,"status"))return "STATUS";
    if(!strcmp(event_type,"result"))return "RESULT";
    if(!strcmp(event_type,"runner_approved"))return "APPROVED";
    if(!strcmp(event_type,"e2e_requeued"))return "REQUEUED";
    return "--";
}
