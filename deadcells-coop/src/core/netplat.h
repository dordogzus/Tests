/* Minimal non-blocking UDP over Winsock2 / BSD sockets. IPv4 only (LAN). */
#ifndef DCCOOP_NETPLAT_H
#define DCCOOP_NETPLAT_H

#include <stdint.h>

typedef struct { uint32_t ip; uint16_t port; } netaddr; /* host byte order */

typedef struct { intptr_t fd; } udpsock;

int net_init(void);
void net_shutdown(void);
/* port 0 = ephemeral. Returns 0 on success. */
int udp_open(udpsock *s, uint16_t port, int broadcast);
void udp_close(udpsock *s);
int udp_send(udpsock *s, const netaddr *to, const void *data, int len);
/* Returns bytes received, 0 if nothing pending, -1 on error. */
int udp_recv(udpsock *s, netaddr *from, void *buf, int cap);
uint16_t udp_local_port(udpsock *s);

int netaddr_eq(const netaddr *a, const netaddr *b);
/* "a.b.c.d[:port]" -> addr; returns 0 on success. */
int netaddr_parse(const char *s, uint16_t default_port, netaddr *out);
void netaddr_str(const netaddr *a, char *buf, int n);

/* Monotonic seconds. */
double net_time(void);

#endif
