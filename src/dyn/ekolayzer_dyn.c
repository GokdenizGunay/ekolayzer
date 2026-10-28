/*
 * ekolayzer_dyn - Ekolayzer için stereo bağlı, ileri bakışlı (lookahead) limiter/kompresör
 * ve seviye ölçer. PipeWire filter-chain içinde LADSPA eklentisi olarak çalışır.
 *
 * Kazanç zinciri (örnek başına):
 *   x = giriş * kazanç (ön kazanç)
 *   r = statik eğri (eşik/oran/diz) ve tavan ile gereken kazanç
 *   m = son L örnekteki en küçük r (kayan minimum, O(1))
 *   e = m'ye bırakma (release) süresiyle yükselen zarf (e <= m)
 *   g = e'nin L uzunluklu kayan ortalaması (atak rampası)
 *   çıkış = L-1 örnek geciktirilmiş x * g
 * Kayan ortalamadaki her e değeri tepe örneğinin gerektirdiği kazançtan küçük
 * olduğundan çıkış hiçbir zaman tavanı aşmaz.
 *
 * Ölçümler: her 256 örnekte (~5 ms) tepe ve kazanç azaltma değerleri
 * $XDG_RUNTIME_DIR/ekolayzer/levels dosyasındaki halka arabelleğe yazılır
 * (kilitsiz; arayüz mmap ile okur, ek süreç veya akış gerekmez).
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <fcntl.h>
#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

/* ---- LADSPA 1.1 ABI (ladspa.h ile birebir uyumlu, bağımlılık olmasın diye burada) ---- */
typedef float LADSPA_Data;
typedef int LADSPA_Properties;
typedef int LADSPA_PortDescriptor;
typedef int LADSPA_PortRangeHintDescriptor;
typedef void *LADSPA_Handle;
typedef struct {
	LADSPA_PortRangeHintDescriptor HintDescriptor;
	LADSPA_Data LowerBound;
	LADSPA_Data UpperBound;
} LADSPA_PortRangeHint;
typedef struct _LADSPA_Descriptor {
	unsigned long UniqueID;
	const char *Label;
	LADSPA_Properties Properties;
	const char *Name;
	const char *Maker;
	const char *Copyright;
	unsigned long PortCount;
	const LADSPA_PortDescriptor *PortDescriptors;
	const char *const *PortNames;
	const LADSPA_PortRangeHint *PortRangeHints;
	void *ImplementationData;
	LADSPA_Handle (*instantiate)(const struct _LADSPA_Descriptor *, unsigned long);
	void (*connect_port)(LADSPA_Handle, unsigned long, LADSPA_Data *);
	void (*activate)(LADSPA_Handle);
	void (*run)(LADSPA_Handle, unsigned long);
	void (*run_adding)(LADSPA_Handle, unsigned long);
	void (*set_run_adding_gain)(LADSPA_Handle, LADSPA_Data);
	void (*deactivate)(LADSPA_Handle);
	void (*cleanup)(LADSPA_Handle);
} LADSPA_Descriptor;

#define PROP_HARD_RT 0x4
#define P_IN 0x1
#define P_OUT 0x2
#define P_CONTROL 0x4
#define P_AUDIO 0x8
#define H_BELOW 0x1
#define H_ABOVE 0x2
#define H_TOGGLED 0x4
#define H_LOG 0x10
#define H_DEF_MIN 0x40
#define H_DEF_MID 0xC0
#define H_DEF_MAX 0x140
#define H_DEF_0 0x200
#define H_DEF_1 0x240

/* ---- Portlar ---- */
enum {
	IN_L, IN_R, OUT_L, OUT_R,
	C_GAIN, C_ENABLE, C_THRESH, C_RATIO, C_KNEE, C_ATTACK, C_RELEASE, C_CEIL,
	C_GR,
	N_PORTS
};

static const LADSPA_PortDescriptor port_desc[N_PORTS] = {
	P_IN | P_AUDIO, P_IN | P_AUDIO, P_OUT | P_AUDIO, P_OUT | P_AUDIO,
	P_IN | P_CONTROL, P_IN | P_CONTROL, P_IN | P_CONTROL, P_IN | P_CONTROL,
	P_IN | P_CONTROL, P_IN | P_CONTROL, P_IN | P_CONTROL, P_IN | P_CONTROL,
	P_OUT | P_CONTROL,
};

static const char *const port_names[N_PORTS] = {
	"In L", "In R", "Out L", "Out R",
	"Gain", "Enable", "Threshold", "Ratio", "Knee", "Attack", "Release", "Ceiling",
	"GR",
};

static const LADSPA_PortRangeHint port_hints[N_PORTS] = {
	{0, 0, 0}, {0, 0, 0}, {0, 0, 0}, {0, 0, 0},
	{H_BELOW | H_ABOVE | H_DEF_0, -36.0f, 24.0f},          /* Gain dB */
	{H_TOGGLED | H_DEF_1, 0, 1},                             /* Enable */
	{H_BELOW | H_ABOVE | H_DEF_MAX, -30.0f, 0.0f},          /* Threshold dB */
	{H_BELOW | H_ABOVE | H_DEF_MAX, 1.0f, 20.0f},           /* Ratio (20 = sonsuz) */
	{H_BELOW | H_ABOVE | H_DEF_MIN, 0.0f, 12.0f},           /* Knee dB */
	{H_BELOW | H_ABOVE | H_LOG | H_DEF_MID, 0.5f, 10.0f},   /* Attack ms */
	{H_BELOW | H_ABOVE | H_LOG | H_DEF_MID, 10.0f, 2000.0f},/* Release ms */
	{H_BELOW | H_ABOVE | H_DEF_MAX, -12.0f, 0.0f},          /* Ceiling dB */
	{H_BELOW | H_ABOVE, 0.0f, 60.0f},                       /* GR dB (çıkış) */
};

/* ---- Paylaşılan seviye arabelleği ---- */
#define LV_MAGIC 0x4c564b45u /* "EKVL" */
#define LV_SLOTS 64
#define LV_SUB 256 /* ölçüm dilimi (örnek) */
struct lv_slot {
	float out_l, out_r, in_peak, gr_db;
};
struct lv_shm {
	uint32_t magic;
	uint32_t rate;
	uint32_t count; /* yazılan toplam blok sayısı (atomik) */
	uint32_t pad;
	struct lv_slot slot[LV_SLOTS];
};

#define RING 4096 /* 2^n, > en büyük ileri bakış (10 ms @ 192 kHz) */
#define MASK (RING - 1)

typedef struct {
	LADSPA_Data *port[N_PORTS];
	float rate;
	/* durum */
	float gain_lin;      /* yumuşatılmış ön kazanç */
	float env;           /* bırakma zarfı */
	double box_sum;      /* kayan ortalama toplamı */
	unsigned L;          /* ileri bakış (örnek) */
	uint64_t n;          /* örnek sayacı */
	unsigned w;          /* halka yazma konumu */
	float dl[2][RING];   /* gecikme hattı */
	float box[RING];     /* zarf geçmişi */
	uint64_t dq_idx[RING];
	float dq_val[RING];
	unsigned dq_head, dq_tail;
	/* ölçüm */
	struct lv_shm *shm;
	unsigned sub_n;
	float acc_l, acc_r, acc_in, acc_g;
} Dyn;

static float db2lin(float db) { return powf(10.0f, db * 0.05f); }

static void reset_lookahead(Dyn *d, unsigned L)
{
	d->L = L;
	d->dq_head = d->dq_tail = 0;
	for (unsigned i = 0; i < RING; i++)
		d->box[i] = d->env;
	d->box_sum = (double)d->env * L;
}

static void open_shm(Dyn *d)
{
	const char *rt = getenv("XDG_RUNTIME_DIR");
	char path[512];
	if (!rt || strlen(rt) > 400)
		return;
	strcpy(path, rt);
	strcat(path, "/ekolayzer");
	mkdir(path, 0700);
	strcat(path, "/levels");
	int fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC, 0600);
	if (fd < 0)
		return;
	if (ftruncate(fd, sizeof(struct lv_shm)) == 0) {
		void *p = mmap(NULL, sizeof(struct lv_shm), PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0);
		if (p != MAP_FAILED) {
			d->shm = p;
			d->shm->rate = (uint32_t)d->rate;
			d->shm->magic = LV_MAGIC;
		}
	}
	close(fd);
}

static void lv_flush(Dyn *d)
{
	struct lv_shm *s = d->shm;
	if (s) {
		uint32_t c = __atomic_load_n(&s->count, __ATOMIC_RELAXED);
		struct lv_slot *sl = &s->slot[c % LV_SLOTS];
		sl->out_l = d->acc_l;
		sl->out_r = d->acc_r;
		sl->in_peak = d->acc_in;
		sl->gr_db = d->acc_g < 1.0f ? -20.0f * log10f(d->acc_g) : 0.0f;
		__atomic_store_n(&s->count, c + 1, __ATOMIC_RELEASE);
	}
	d->sub_n = 0;
	d->acc_l = d->acc_r = d->acc_in = 0.0f;
	d->acc_g = 1.0f;
}

static LADSPA_Handle instantiate(const LADSPA_Descriptor *desc, unsigned long rate)
{
	(void)desc;
	Dyn *d = calloc(1, sizeof(Dyn));
	if (!d)
		return NULL;
	d->rate = (float)rate;
	d->gain_lin = 1.0f;
	d->env = 1.0f;
	d->acc_g = 1.0f;
	reset_lookahead(d, 1);
	open_shm(d);
	return d;
}

static void connect_port(LADSPA_Handle h, unsigned long p, LADSPA_Data *data)
{
	if (p < N_PORTS)
		((Dyn *)h)->port[p] = data;
}

static void activate(LADSPA_Handle h)
{
	Dyn *d = h;
	memset(d->dl, 0, sizeof(d->dl));
	d->env = 1.0f;
	d->n = 0;
	d->w = 0;
	reset_lookahead(d, d->L);
}

static inline float ctl(Dyn *d, int p, float def)
{
	return d->port[p] ? *d->port[p] : def;
}

static void run(LADSPA_Handle h, unsigned long n_samples)
{
	Dyn *d = h;
	const float *il = d->port[IN_L], *ir = d->port[IN_R];
	float *ol = d->port[OUT_L], *or_ = d->port[OUT_R];
	if (!il || !ir || !ol || !or_)
		return;

	const int enable = ctl(d, C_ENABLE, 1.0f) >= 0.5f;
	const float thresh = ctl(d, C_THRESH, 0.0f);
	const float ratio = ctl(d, C_RATIO, 20.0f);
	const float knee = fmaxf(ctl(d, C_KNEE, 0.0f), 0.0f);
	const float ceil_db = fminf(ctl(d, C_CEIL, 0.0f), 0.0f);
	const float slope = ratio >= 19.99f ? 1.0f : 1.0f - 1.0f / fmaxf(ratio, 1.0f);
	const float ceil_lin = db2lin(ceil_db);
	/* statik eğrinin etkisiz kaldığı seviye: bunun altında gereken kazanç 1 */
	const float lim_db = fminf(thresh - knee * 0.5f, ceil_db);
	const float lim_lin = db2lin(lim_db);

	float att_ms = fminf(fmaxf(ctl(d, C_ATTACK, 2.0f), 0.5f), 10.0f);
	unsigned L = (unsigned)(att_ms * 0.001f * d->rate + 0.5f);
	if (L < 1)
		L = 1;
	if (L > RING / 2)
		L = RING / 2;
	if (L != d->L)
		reset_lookahead(d, L);

	float rel_ms = fminf(fmaxf(ctl(d, C_RELEASE, 150.0f), 10.0f), 2000.0f);
	const float rel_a = 1.0f - expf(-1.0f / (rel_ms * 0.001f * d->rate));

	/* ön kazanç: blok boyunca doğrusal rampa (cızırtısız) */
	const float g_target = db2lin(fminf(fmaxf(ctl(d, C_GAIN, 0.0f), -36.0f), 24.0f));
	const float g_step = (g_target - d->gain_lin) / (float)(n_samples ? n_samples : 1);
	float g = d->gain_lin;

	const double invL = 1.0 / (double)L;
	float g_min = 1.0f;

	for (unsigned long i = 0; i < n_samples; i++) {
		g += g_step;
		const float xl = il[i] * g, xr = ir[i] * g;
		const float p = fmaxf(fabsf(xl), fabsf(xr));
		if (p > d->acc_in)
			d->acc_in = p;

		/* gereken kazanç */
		float r = 1.0f;
		if (enable && p > lim_lin) {
			const float lvl = 20.0f * log10f(p);
			const float over = lvl - thresh;
			float gr = 0.0f;
			if (knee > 0.0f && 2.0f * fabsf(over) <= knee)
				gr = slope * (over + knee * 0.5f) * (over + knee * 0.5f) / (2.0f * knee);
			else if (over > 0.0f)
				gr = slope * over;
			if (lvl - gr > ceil_db)
				gr = lvl - ceil_db;
			if (gr > 0.0f)
				r = db2lin(-gr);
		}

		/* kayan minimum (monoton kuyruk) */
		const uint64_t n = d->n++;
		while (d->dq_tail != d->dq_head && d->dq_val[(d->dq_tail - 1) & MASK] >= r)
			d->dq_tail--;
		d->dq_idx[d->dq_tail & MASK] = n;
		d->dq_val[d->dq_tail & MASK] = r;
		d->dq_tail++;
		while (d->dq_idx[d->dq_head & MASK] + L <= n)
			d->dq_head++;
		const float m = d->dq_val[d->dq_head & MASK];

		/* bırakma zarfı (her zaman e <= m) */
		float e = d->env;
		e = m < e ? m : e + (m - e) * rel_a;
		d->env = e;

		/* atak: L uzunluklu kayan ortalama */
		const unsigned w = d->w;
		d->box_sum += (double)e - (double)d->box[(w - L) & MASK];
		d->box[w] = e;
		float gg = (float)(d->box_sum * invL);
		if (gg > 1.0f)
			gg = 1.0f;
		if (gg < g_min)
			g_min = gg;
		if (gg < d->acc_g)
			d->acc_g = gg;

		/* gecikme hattı */
		d->dl[0][w] = xl;
		d->dl[1][w] = xr;
		const unsigned rd = (w - (L - 1)) & MASK;
		float yl = d->dl[0][rd] * gg, yr = d->dl[1][rd] * gg;
		d->w = (w + 1) & MASK;

		/* emniyet: yuvarlama hatalarına karşı tavan */
		if (enable) {
			yl = fminf(fmaxf(yl, -ceil_lin), ceil_lin);
			yr = fminf(fmaxf(yr, -ceil_lin), ceil_lin);
		}
		ol[i] = yl;
		or_[i] = yr;
		const float al = fabsf(yl), ar = fabsf(yr);
		if (al > d->acc_l)
			d->acc_l = al;
		if (ar > d->acc_r)
			d->acc_r = ar;
		if (++d->sub_n >= LV_SUB)
			lv_flush(d);
	}
	d->gain_lin = g_target;
	/* gecikme hattı ve kutu boyunca d->box_sum çok uzun sürede kayabilir: yeniden eşitle */
	if ((d->n & 0xFFFFF) < n_samples) {
		double s = 0;
		for (unsigned k = 1; k <= L; k++)
			s += d->box[(d->w - k) & MASK];
		d->box_sum = s;
	}

	const float gr_db = g_min < 1.0f ? -20.0f * log10f(g_min) : 0.0f;
	if (d->port[C_GR])
		*d->port[C_GR] = gr_db;
}

static void cleanup(LADSPA_Handle h)
{
	Dyn *d = h;
	if (d->shm)
		munmap(d->shm, sizeof(struct lv_shm));
	free(d);
}

static const LADSPA_Descriptor descriptor = {
	.UniqueID = 0x0EC01A,
	.Label = "ekolayzer_dyn",
	.Properties = PROP_HARD_RT,
	.Name = "Ekolayzer Limiter",
	.Maker = "Ekolayzer",
	.Copyright = "GPL-3.0-or-later",
	.PortCount = N_PORTS,
	.PortDescriptors = port_desc,
	.PortNames = port_names,
	.PortRangeHints = port_hints,
	.instantiate = instantiate,
	.connect_port = connect_port,
	.activate = activate,
	.run = run,
	.cleanup = cleanup,
};

__attribute__((visibility("default")))
const LADSPA_Descriptor *ladspa_descriptor(unsigned long index)
{
	return index == 0 ? &descriptor : NULL;
}
