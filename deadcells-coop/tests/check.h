#ifndef DCCOOP_CHECK_H
#define DCCOOP_CHECK_H
#include <math.h>
#include <stdio.h>

static int g_fail, g_pass;
#define CHECK(cond) do { if (cond) g_pass++; else { g_fail++; printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); } } while (0)
#define CHECK_NEAR(a, b, eps) do { double a_ = (a), b_ = (b); if (fabs(a_ - b_) <= (eps)) g_pass++; else { g_fail++; \
	printf("FAIL %s:%d: %s = %g, expected %g (+-%g)\n", __FILE__, __LINE__, #a, a_, b_, (double)(eps)); } } while (0)
#define CHECK_DONE(name) (printf("%s: %d passed, %d failed\n", name, g_pass, g_fail), g_fail ? 1 : 0)
#endif
