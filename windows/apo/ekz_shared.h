/*
 * Ekolayzer: ses motoru (APO) ile arayüz arasındaki ortak bellek düzeni.
 *
 * Dosya: %ProgramData%\Ekolayzer\engine.bin (4096 bayt; kurulum oluşturur, herkes yazabilir).
 * Her iki taraf dosyayı eşler (memory-mapped); aynı dosyanın görünümleri Windows'ta tutarlıdır,
 * bu yüzden adlandırılmış nesne veya ek süreç gerekmez. Dosya diskte kaldığı için son ayarlar
 * yeniden başlatmadan sonra da motor tarafından hemen kullanılır.
 *
 * Arayüz -> motor: params (seqlock: seq tekken yazım sürüyor).
 * Motor -> arayüz: halka arabellekte her 256 örnekte bir seviye dilimi (Linux sürümüyle aynı).
 * Ölçüm yalnızca arayüz reader_tick'i son 1 saniyede yenilediyse yapılır (pencere açık ve odakta).
 * C# karşılığı: app/Engine.cs (ofsetler birebir aynı olmalı).
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#pragma once
#include <stdint.h>

#define EKZ_MAGIC 0x315A4B45u /* "EKZ1" */
#define EKZ_LAYOUT 1
#define EKZ_BANDS 10
#define EKZ_SLOTS 64
#define EKZ_FILE_SIZE 4096

#pragma pack(push, 4)
struct EkzParams {
	uint32_t enabled;  /* 0: sesi değiştirmeden geçir */
	float preamp;      /* dB */
	float gains[EKZ_BANDS];
	float threshold, ratio, knee, attack, release, ceiling;
};

struct EkzSlot {
	float out_l, out_r, in_peak, gr_db;
};

struct EkzShared {
	uint32_t magic;            /*   0 */
	uint32_t layout;           /*   4 */
	volatile long seq;         /*   8 */
	uint32_t pad0;             /*  12 */
	struct EkzParams p;        /*  16 .. 88 */
	uint8_t pad1[40];          /*  88 .. 128 */
	uint32_t rate;             /* 128 */
	volatile long count;       /* 132: yazılan dilim sayısı */
	volatile long owner;       /* 136: ölçümü yazan örnek (birden çok aygıtta çalışırken) */
	uint32_t channels;         /* 140 */
	volatile int64_t owner_tick; /* 144: GetTickCount64 */
	volatile int64_t alive_tick; /* 152: son işleme çağrısı */
	volatile int64_t lock_tick;  /* 160: son işleme başlangıcı (LockForProcess) */
	volatile int64_t reader_tick; /* 168: arayüz penceresi açık ve odaktayken yenilenir */
	uint8_t pad2[80];          /* 176 .. 256 */
	struct EkzSlot slot[EKZ_SLOTS]; /* 256 .. 1280 */
};
#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(EkzParams) == 72, "EkzParams");
static_assert(offsetof(EkzShared, rate) == 128, "rate");
static_assert(offsetof(EkzShared, slot) == 256, "slot");
static_assert(sizeof(EkzShared) <= EKZ_FILE_SIZE, "size");
#endif
