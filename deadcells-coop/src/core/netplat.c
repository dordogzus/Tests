#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
typedef int socklen_t;
#define CLOSESOCK closesocket
#else
#define _POSIX_C_SOURCE 200809L
#include <arpa/inet.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>
#define CLOSESOCK close
#endif

#include "netplat.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int net_init(void) {
#if defined(_WIN32)
	WSADATA d;
	return WSAStartup(MAKEWORD(2, 2), &d) == 0 ? 0 : -1;
#else
	return 0;
#endif
}

void net_shutdown(void) {
#if defined(_WIN32)
	WSACleanup();
#endif
}

int udp_open(udpsock *s, uint16_t port, int broadcast) {
	s->fd = -1;
	intptr_t fd = (intptr_t)socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
	if (fd < 0) return -1;
	int one = 1;
	if (broadcast) setsockopt((int)fd, SOL_SOCKET, SO_BROADCAST, (const char *)&one, sizeof one);
	/* discovery listeners share the port with other local instances */
	if (broadcast) setsockopt((int)fd, SOL_SOCKET, SO_REUSEADDR, (const char *)&one, sizeof one);
	int bufsz = 1 << 20;
	setsockopt((int)fd, SOL_SOCKET, SO_RCVBUF, (const char *)&bufsz, sizeof bufsz);
	setsockopt((int)fd, SOL_SOCKET, SO_SNDBUF, (const char *)&bufsz, sizeof bufsz);
	struct sockaddr_in a;
	memset(&a, 0, sizeof a);
	a.sin_family = AF_INET;
	a.sin_addr.s_addr = htonl(INADDR_ANY);
	a.sin_port = htons(port);
	if (bind((int)fd, (struct sockaddr *)&a, sizeof a) != 0) { CLOSESOCK((int)fd); return -1; }
#if defined(_WIN32)
	u_long nb = 1;
	ioctlsocket((SOCKET)fd, FIONBIO, &nb);
	/* ignore ICMP port-unreachable resets so one dead peer can't break recv */
	BOOL off = FALSE;
	DWORD ret = 0;
	WSAIoctl((SOCKET)fd, _WSAIOW(IOC_VENDOR, 12), &off, sizeof off, NULL, 0, &ret, NULL, NULL);
#else
	fcntl((int)fd, F_SETFL, fcntl((int)fd, F_GETFL, 0) | O_NONBLOCK);
#endif
	s->fd = fd;
	return 0;
}

void udp_close(udpsock *s) {
	if (s->fd >= 0) CLOSESOCK((int)s->fd);
	s->fd = -1;
}

int udp_send(udpsock *s, const netaddr *to, const void *data, int len) {
	struct sockaddr_in a;
	memset(&a, 0, sizeof a);
	a.sin_family = AF_INET;
	a.sin_addr.s_addr = htonl(to->ip);
	a.sin_port = htons(to->port);
	int r = (int)sendto((int)s->fd, (const char *)data, len, 0, (struct sockaddr *)&a, sizeof a);
	return r == len ? 0 : -1;
}

int udp_recv(udpsock *s, netaddr *from, void *buf, int cap) {
	struct sockaddr_in a;
	socklen_t alen = sizeof a;
	int r = (int)recvfrom((int)s->fd, (char *)buf, cap, 0, (struct sockaddr *)&a, &alen);
	if (r < 0) {
#if defined(_WIN32)
		int e = WSAGetLastError();
		if (e == WSAEWOULDBLOCK || e == WSAECONNRESET || e == WSAEMSGSIZE) return 0;
#else
		if (errno == EAGAIN || errno == EWOULDBLOCK || errno == ECONNREFUSED) return 0;
#endif
		return -1;
	}
	from->ip = ntohl(a.sin_addr.s_addr);
	from->port = ntohs(a.sin_port);
	return r;
}

uint16_t udp_local_port(udpsock *s) {
	struct sockaddr_in a;
	socklen_t alen = sizeof a;
	if (getsockname((int)s->fd, (struct sockaddr *)&a, &alen) != 0) return 0;
	return ntohs(a.sin_port);
}

int netaddr_eq(const netaddr *a, const netaddr *b) { return a->ip == b->ip && a->port == b->port; }

int netaddr_parse(const char *s, uint16_t default_port, netaddr *out) {
	unsigned a, b, c, d, p = default_port;
	int n = sscanf(s, "%u.%u.%u.%u:%u", &a, &b, &c, &d, &p);
	if (n < 4 || a > 255 || b > 255 || c > 255 || d > 255 || p > 65535) return -1;
	out->ip = (a << 24) | (b << 16) | (c << 8) | d;
	out->port = (uint16_t)p;
	return 0;
}

void netaddr_str(const netaddr *a, char *buf, int n) {
	snprintf(buf, (size_t)n, "%u.%u.%u.%u:%u", a->ip >> 24, (a->ip >> 16) & 255, (a->ip >> 8) & 255, a->ip & 255,
		a->port);
}

double net_time(void) {
#if defined(_WIN32)
	static LARGE_INTEGER freq;
	LARGE_INTEGER now;
	if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
	QueryPerformanceCounter(&now);
	return (double)now.QuadPart / (double)freq.QuadPart;
#else
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
#endif
}
