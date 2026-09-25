/* dccoop.ini parser: "key = value" lines, '#' or ';' comments, [sections] ignored. */
#ifndef DCCOOP_CONFIG_H
#define DCCOOP_CONFIG_H

#include "rules.h"

typedef struct {
	char name[24];
	int mode;               /* 0 = off, 1 = host, 2 = join, 3 = auto (join LAN game or host) */
	char join_addr[64];
	int port;
	int tick_rate;          /* network sends per second */
	int pvp_mode;
	float pvp_damage;
	int friendly_fire_key;  /* virtual key code for the PvP toggle */
	int revive;             /* downed/revive system on/off */
	int shared_cells;       /* cells/gold picked by anyone go to everyone */
	int debug_log;
	balance_cfg balance;
	/* game field names (configurable so a game update does not need a rebuild) */
	char f_cx[24], f_cy[24], f_xr[24], f_yr[24], f_dx[24], f_dy[24], f_dir[24], f_life[24], f_maxlife[24];
} dc_config;

void config_defaults(dc_config *c);
/* Applies settings from text; unknown keys are ignored. */
void config_parse(dc_config *c, const char *text);
int config_load(dc_config *c, const char *path);

#endif
