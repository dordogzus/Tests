#include "config.h"

#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void config_defaults(dc_config *c) {
	memset(c, 0, sizeof *c);
	strcpy(c->name, "Prisoner");
	c->mode = 3;
	c->port = 47770;
	c->tick_rate = 30;
	c->pvp_mode = PVP_FFA;
	c->pvp_damage = 0.35f;
	c->friendly_fire_key = 0x78; /* F9 */
	c->revive = 1;
	c->shared_cells = 1;
	balance_default_cfg(&c->balance);
	strcpy(c->f_cx, "cx"); strcpy(c->f_cy, "cy");
	strcpy(c->f_xr, "xr"); strcpy(c->f_yr, "yr");
	strcpy(c->f_dx, "dx"); strcpy(c->f_dy, "dy");
	strcpy(c->f_dir, "dir");
	strcpy(c->f_life, "life"); strcpy(c->f_maxlife, "maxLife");
}

static char *trim(char *s) {
	while (isspace((unsigned char)*s)) s++;
	char *e = s + strlen(s);
	while (e > s && isspace((unsigned char)e[-1])) *--e = 0;
	return s;
}

static void set_str(char *dst, size_t n, const char *v) {
	strncpy(dst, v, n - 1);
	dst[n - 1] = 0;
}

#define STR(key, field) if (!strcmp(k, key)) { set_str(c->field, sizeof c->field, v); continue; }
#define INT(key, field) if (!strcmp(k, key)) { c->field = atoi(v); continue; }
#define FLT(key, field) if (!strcmp(k, key)) { c->field = (float)atof(v); continue; }

void config_parse(dc_config *c, const char *text) {
	char line[256];
	const char *p = text;
	while (*p) {
		size_t n = strcspn(p, "\r\n");
		size_t m = n < sizeof line - 1 ? n : sizeof line - 1;
		memcpy(line, p, m);
		line[m] = 0;
		p += n;
		while (*p == '\r' || *p == '\n') p++;
		char *cm = strpbrk(line, "#;");
		if (cm) *cm = 0;
		char *eq = strchr(line, '=');
		if (!eq) continue;
		*eq = 0;
		char *k = trim(line), *v = trim(eq + 1);
		if (!strcmp(k, "mode")) {
			c->mode = !strcmp(v, "host") ? 1 : !strcmp(v, "join") ? 2 : !strcmp(v, "auto") ? 3 : 0;
			continue;
		}
		if (!strcmp(k, "pvp")) {
			c->pvp_mode = !strcmp(v, "teams") ? PVP_TEAMS : !strcmp(v, "off") ? PVP_OFF : PVP_FFA;
			continue;
		}
		STR("name", name) STR("join", join_addr)
		INT("port", port) INT("tick_rate", tick_rate) INT("pvp_key", friendly_fire_key)
		INT("revive", revive) INT("shared_cells", shared_cells) INT("debug_log", debug_log)
		FLT("pvp_damage", pvp_damage)
		FLT("density_per_player", balance.density_per_player) FLT("density_cap", balance.density_cap)
		FLT("pressure_per_player", balance.pressure_per_player) FLT("boss_falloff", balance.boss_falloff)
		FLT("enemy_damage_per_player", balance.dmg_per_player) FLT("enemy_damage_cap", balance.dmg_cap)
		FLT("elite_per_player", balance.elite_per_player)
		STR("field_cx", f_cx) STR("field_cy", f_cy) STR("field_xr", f_xr) STR("field_yr", f_yr)
		STR("field_dx", f_dx) STR("field_dy", f_dy) STR("field_dir", f_dir)
		STR("field_life", f_life) STR("field_maxlife", f_maxlife)
	}
	if (c->tick_rate < 10) c->tick_rate = 10;
	if (c->tick_rate > 60) c->tick_rate = 60;
	if (c->port <= 0 || c->port > 65535) c->port = 47770;
}

int config_load(dc_config *c, const char *path) {
	FILE *f = fopen(path, "rb");
	if (!f) return -1;
	char buf[16384];
	size_t n = fread(buf, 1, sizeof buf - 1, f);
	fclose(f);
	buf[n] = 0;
	config_parse(c, buf);
	return 0;
}
