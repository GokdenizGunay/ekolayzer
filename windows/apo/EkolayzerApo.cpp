/*
 * EkolayzerApo - Ekolayzer'in Windows ses motoru.
 *
 * Windows ses motoruna (audiodg.exe) "mod efekti" (MFX, karıştırma sonrası) olarak yüklenen bir
 * Audio Processing Object. Aygıtın kendi MFX'i (ör. Windows'un ses geliştirmeleri) varsa onu içinden
 * çalıştırır (zincirleme), sonra kendi işlemini yapar. Tüm uygulamaların karışmış sesi buradan geçer:
 *   her kanal için 10 tepe (peaking) süzgeci -> ön kazanç -> kanallar arası bağlı, ileri bakışlı
 *   limiter/kompresör (Linux sürümündeki ekolayzer_dyn ile aynı algoritma) -> çıkış.
 *
 * Ayarlar ve seviye ölçümleri ortak bellek dosyasıyla taşınır (bkz. ekz_shared.h). Gerçek zamanlı
 * iş parçacığında (APOProcess) bellek ayırma, kilit, dosya veya sistem çağrısı yoktur.
 *
 * SPDX-License-Identifier: GPL-3.0-or-later
 */
#include <windows.h>
#include <stddef.h>
#include <stdio.h>
#include <math.h>
#include <new>
#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <mmreg.h>
#include <ks.h>
#include <ksmedia.h>
#include <propsys.h>

#include "ekz_shared.h"

// {249FC38E-317D-4074-9AC1-A639059256EC}
static const CLSID CLSID_EkolayzerApo = {0x249fc38e, 0x317d, 0x4074, {0x9a, 0xc1, 0xa6, 0x39, 0x05, 0x92, 0x56, 0xec}};

// Kurulumun aygıtın FxProperties'ine yazdığı, yerine geçtiğimiz özgün efektin CLSID'i
static const PROPERTYKEY PKEY_Ekz_Child = {{0x249fc38e, 0x317d, 0x4074, {0x9a, 0xc1, 0xa6, 0x39, 0x05, 0x92, 0x56, 0xec}}, 1};
// PKEY_AudioEndpoint_GUID
static const PROPERTYKEY PKEY_Endpoint_Guid = {{0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}}, 4};

static HMODULE g_module;
static volatile LONG g_objects, g_locks;

static const float BAND_FREQS[EKZ_BANDS] = {31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000};
static const double BAND_Q = 1.41;

#define RING 8192 /* 2^n, > en büyük ileri bakış (10 ms @ 384 kHz) */
#define MASK (RING - 1)
#define LV_SUB 256
#define MAX_CH 32

static inline float db2lin(float db) { return powf(10.0f, db * 0.05f); }
static inline float clampf(float v, float lo, float hi)
{
	if (!(v == v)) /* NaN */
		return lo;
	return v < lo ? lo : (v > hi ? hi : v);
}

static void default_params(EkzParams *p)
{
	memset(p, 0, sizeof(*p));
	p->enabled = 1;
	p->threshold = -1.0f;
	p->ratio = 20.0f;
	p->knee = 2.0f;
	p->attack = 2.0f;
	p->release = 120.0f;
	p->ceiling = -0.3f;
}

static void sanitize(EkzParams *p)
{
	p->enabled = p->enabled ? 1 : 0;
	p->preamp = clampf(p->preamp, -12.0f, 12.0f);
	for (int i = 0; i < EKZ_BANDS; i++)
		p->gains[i] = clampf(p->gains[i], -12.0f, 12.0f);
	p->threshold = clampf(p->threshold, -30.0f, 0.0f);
	p->ratio = clampf(p->ratio, 1.0f, 20.0f);
	p->knee = clampf(p->knee, 0.0f, 12.0f);
	p->attack = clampf(p->attack, 0.5f, 10.0f);
	p->release = clampf(p->release, 10.0f, 2000.0f);
	p->ceiling = clampf(p->ceiling, -12.0f, 0.0f);
}

/* ------------------------------------------------------------------ ortak bellek */

struct SharedFile {
	HANDLE file = INVALID_HANDLE_VALUE, map = NULL;
	EkzShared *sh = NULL;

	void open()
	{
		if (sh)
			return;
		wchar_t path[MAX_PATH];
		DWORD n = GetEnvironmentVariableW(L"ProgramData", path, MAX_PATH);
		if (n == 0 || n > MAX_PATH - 40)
			wcscpy_s(path, L"C:\\ProgramData");
		wcscat_s(path, L"\\Ekolayzer\\engine.bin");
		file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL,
		                   OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
		if (file == INVALID_HANDLE_VALUE)
			return;
		LARGE_INTEGER sz;
		if (!GetFileSizeEx(file, &sz) || sz.QuadPart < EKZ_FILE_SIZE) {
			close();
			return;
		}
		map = CreateFileMappingW(file, NULL, PAGE_READWRITE, 0, EKZ_FILE_SIZE, NULL);
		if (map)
			sh = (EkzShared *)MapViewOfFile(map, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, EKZ_FILE_SIZE);
		if (!sh || sh->magic != EKZ_MAGIC || sh->layout != EKZ_LAYOUT) {
			close();
			return;
		}
	}

	void close()
	{
		if (sh)
			UnmapViewOfFile(sh);
		if (map)
			CloseHandle(map);
		if (file != INVALID_HANDLE_VALUE)
			CloseHandle(file);
		sh = NULL;
		map = NULL;
		file = INVALID_HANDLE_VALUE;
	}
};

/* ------------------------------------------------------------------ APO */

/* Ses motoru APO'ları COM toplama (aggregation) ile oluşturur: dış nesnenin IUnknown'ı ayrı tutulur. */
struct INonDelegatingUnknown {
	virtual HRESULT STDMETHODCALLTYPE NDQueryInterface(REFIID riid, void **ppv) = 0;
	virtual ULONG STDMETHODCALLTYPE NDAddRef() = 0;
	virtual ULONG STDMETHODCALLTYPE NDRelease() = 0;
};

class EkolayzerApo : public INonDelegatingUnknown,
                     public IAudioProcessingObject,
                     public IAudioProcessingObjectRT,
                     public IAudioProcessingObjectConfiguration,
                     public IAudioSystemEffects2 {
public:
	explicit EkolayzerApo(IUnknown *outer)
	{
		outer_ = outer ? outer : reinterpret_cast<IUnknown *>(static_cast<INonDelegatingUnknown *>(this));
		InterlockedIncrement(&g_objects);
		default_params(&prm_);
		id_ = (LONG)(GetTickCount() ^ (DWORD)(ULONG_PTR)this) | 1;
	}
	virtual ~EkolayzerApo()
	{
		releaseChild();
		freeBuffers();
		shm_.close();
		InterlockedDecrement(&g_objects);
	}

	/* toplamayı yapan nesneye yönlendirilen IUnknown */
	STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override { return outer_->QueryInterface(riid, ppv); }
	STDMETHODIMP_(ULONG) AddRef() override { return outer_->AddRef(); }
	STDMETHODIMP_(ULONG) Release() override { return outer_->Release(); }

	/* asıl (yönlendirilmeyen) IUnknown */
	STDMETHODIMP NDQueryInterface(REFIID riid, void **ppv) override
	{
		if (!ppv)
			return E_POINTER;
		if (riid == __uuidof(IUnknown)) {
			*ppv = static_cast<INonDelegatingUnknown *>(this);
			NDAddRef();
			return S_OK;
		}
		if (riid == __uuidof(IAudioProcessingObject))
			*ppv = static_cast<IAudioProcessingObject *>(this);
		else if (riid == __uuidof(IAudioProcessingObjectRT))
			*ppv = static_cast<IAudioProcessingObjectRT *>(this);
		else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
			*ppv = static_cast<IAudioProcessingObjectConfiguration *>(this);
		else if (riid == __uuidof(IAudioSystemEffects) || riid == __uuidof(IAudioSystemEffects2))
			*ppv = static_cast<IAudioSystemEffects2 *>(this);
		else {
			*ppv = NULL;
			return E_NOINTERFACE;
		}
		reinterpret_cast<IUnknown *>(*ppv)->AddRef();
		return S_OK;
	}
	STDMETHODIMP_(ULONG) NDAddRef() override { return InterlockedIncrement(&ref_); }
	STDMETHODIMP_(ULONG) NDRelease() override
	{
		ULONG r = InterlockedDecrement(&ref_);
		if (r == 0)
			delete this;
		return r;
	}

	/* IAudioProcessingObject */
	STDMETHODIMP Reset() override
	{
		if (child_)
			child_->Reset();
		resetState();
		return S_OK;
	}

	STDMETHODIMP GetLatency(HNSTIME *t) override
	{
		if (!t)
			return E_POINTER;
		*t = rate_ > 0 ? (HNSTIME)((double)(L_ > 0 ? L_ - 1 : 0) / rate_ * 1e7) : 0;
		HNSTIME ct = 0;
		if (child_ && SUCCEEDED(child_->GetLatency(&ct)))
			*t += ct;
		return S_OK;
	}

	STDMETHODIMP GetRegistrationProperties(APO_REG_PROPERTIES **pp) override
	{
		if (!pp)
			return E_POINTER;
		APO_REG_PROPERTIES *r = (APO_REG_PROPERTIES *)CoTaskMemAlloc(sizeof(APO_REG_PROPERTIES));
		if (!r)
			return E_OUTOFMEMORY;
		memset(r, 0, sizeof(*r));
		r->clsid = CLSID_EkolayzerApo;
		r->Flags = (APO_FLAG)(APO_FLAG_INPLACE | APO_FLAG_DEFAULT);
		wcscpy_s(r->szFriendlyName, L"Ekolayzer");
		wcscpy_s(r->szCopyrightInfo, L"GPL-3.0-or-later");
		r->u32MajorVersion = 1;
		r->u32MinorVersion = 4;
		r->u32MinInputConnections = 1;
		r->u32MaxInputConnections = 1;
		r->u32MinOutputConnections = 1;
		r->u32MaxOutputConnections = 1;
		r->u32MaxInstances = 0xffffffff;
		r->u32NumAPOInterfaces = 1;
		r->iidAPOInterfaceList[0] = __uuidof(IAudioProcessingObject);
		*pp = r;
		return S_OK;
	}

	STDMETHODIMP Initialize(UINT32 cb, BYTE *data) override
	{
		if (cb && !data)
			return E_POINTER;
		if (cb >= sizeof(APOInitBaseStruct)) {
			APOInitBaseStruct *b = (APOInitBaseStruct *)data;
			if (b->clsid != CLSID_EkolayzerApo)
				return APOERR_INVALID_APO_CLSID;
		}
		if (cb >= sizeof(APOInitSystemEffects) && !child_)
			createChild((APOInitSystemEffects *)data, cb);
		return S_OK;
	}

	STDMETHODIMP IsInputFormatSupported(IAudioMediaType *opposite, IAudioMediaType *req,
	                                    IAudioMediaType **supported) override
	{
		if (child_) {
			HRESULT h = child_->IsInputFormatSupported(opposite, req, supported);
			if (h != S_OK)
				return h; /* özgün efektin önerisi/reddi geçerli */
			if (supported && *supported) {
				(*supported)->Release();
				*supported = NULL;
			}
		}
		return checkFormat(opposite, req, supported);
	}

	STDMETHODIMP IsOutputFormatSupported(IAudioMediaType *opposite, IAudioMediaType *req,
	                                     IAudioMediaType **supported) override
	{
		if (child_) {
			HRESULT h = child_->IsOutputFormatSupported(opposite, req, supported);
			if (h != S_OK)
				return h; /* özgün efektin önerisi/reddi geçerli */
			if (supported && *supported) {
				(*supported)->Release();
				*supported = NULL;
			}
		}
		return checkFormat(opposite, req, supported);
	}

	STDMETHODIMP GetInputChannelCount(UINT32 *n) override
	{
		if (!n)
			return E_POINTER;
		*n = ch_;
		return S_OK;
	}

	/* IAudioProcessingObjectConfiguration */
	STDMETHODIMP LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR **in, UINT32 nOut,
	                            APO_CONNECTION_DESCRIPTOR **out) override
	{
		if (locked_)
			return APOERR_APO_LOCKED;
		if (nIn != 1 || nOut != 1 || !in || !out || !in[0] || !out[0] || !in[0]->pFormat)
			return E_INVALIDARG;
		UNCOMPRESSEDAUDIOFORMAT f;
		HRESULT hr = in[0]->pFormat->GetUncompressedAudioFormat(&f);
		if (FAILED(hr))
			return hr;
		if (!isFloat32(f) || f.dwSamplesPerFrame == 0 || f.dwSamplesPerFrame > MAX_CH || f.fFramesPerSecond < 8000)
			return APOERR_FORMAT_NOT_SUPPORTED;
		if (childCfg_ && FAILED(childCfg_->LockForProcess(nIn, in, nOut, out)))
			releaseChild(); /* özgün efekt çalışamıyorsa yalnızca ekolayzer */
		ch_ = f.dwSamplesPerFrame;
		rate_ = f.fFramesPerSecond;
		if (!allocBuffers())
			return E_OUTOFMEMORY;
		shm_.open();
		if (shm_.sh) {
			shm_.sh->rate = (uint32_t)rate_;
			shm_.sh->channels = ch_;
			shm_.sh->lock_tick = (int64_t)GetTickCount64();
		}
		last_seq_ = -1;
		pollParams();
		resetState();
		locked_ = true;
		return S_OK;
	}

	STDMETHODIMP UnlockForProcess() override
	{
		if (!locked_)
			return APOERR_ALREADY_UNLOCKED;
		if (childCfg_)
			childCfg_->UnlockForProcess();
		locked_ = false;
		return S_OK;
	}

	/* IAudioProcessingObjectRT */
	STDMETHODIMP_(void) APOProcess(UINT32 nIn, APO_CONNECTION_PROPERTY **in, UINT32 nOut,
	                               APO_CONNECTION_PROPERTY **out) override
	{
		if (nIn < 1 || nOut < 1)
			return;
		APO_CONNECTION_PROPERTY *ip = in[0], *op = out[0];
		if (childRT_) {
			/* önce özgün efekt (girişten çıkışa), sonra ekolayzer çıkış üzerinde yerinde */
			childRT_->APOProcess(nIn, in, nOut, out);
			ip = op;
		}
		const UINT32 frames = ip->u32ValidFrameCount;
		float *src = (float *)ip->pBuffer, *dst = (float *)op->pBuffer;
		op->u32ValidFrameCount = frames;

		pollParams();
		const int64_t now = (int64_t)GetTickCount64();
		if (shm_.sh)
			shm_.sh->alive_tick = now;
		/* seviye ölçümü yalnızca arayüz izlerken (pencere açık ve odakta) */
		bool meter = false;
		if (shm_.sh) {
			const int64_t rt = shm_.sh->reader_tick;
			meter = rt <= now && now - rt < 1000;
		}
		if (meter != meter_) {
			meter_ = meter;
			sub_n_ = 0;
			acc_l_ = acc_r_ = acc_in_ = 0.0f;
			acc_g_ = 1.0f;
		}

		const bool silent_in = ip->u32BufferFlags == BUFFER_SILENT;
		if (!prm_.enabled) {
			/* kapalı: sesi değiştirmeden geçir */
			if (dst != src && !silent_in)
				memcpy(dst, src, (size_t)frames * ch_ * sizeof(float));
			op->u32BufferFlags = ip->u32BufferFlags;
			bypassed_ = true;
			return;
		}
		if (bypassed_) {
			resetState();
			bypassed_ = false;
		}
		if (silent_in) {
			/* sessizlik: süzgeç kuyrukları ve ileri bakış tamponu boşaldıysa hiçbir şey hesaplama */
			if (quiet_frames_ > (UINT64)L_ + 4096) {
				op->u32BufferFlags = BUFFER_SILENT;
				return;
			}
			memset(dst, 0, (size_t)frames * ch_ * sizeof(float));
			src = dst;
		}
		process(src, dst, frames, now);
		quiet_frames_ = silent_in ? quiet_frames_ + frames : 0;
		op->u32BufferFlags = BUFFER_VALID;
	}

	STDMETHODIMP_(UINT32) CalcInputFrames(UINT32 n) override { return n; }
	STDMETHODIMP_(UINT32) CalcOutputFrames(UINT32 n) override { return n; }

	/* IAudioSystemEffects2 */
	STDMETHODIMP GetEffectsList(LPGUID *ids, UINT *count, HANDLE ev) override
	{
		if (!ids || !count)
			return E_POINTER;
		if (childFx_)
			return childFx_->GetEffectsList(ids, count, ev);
		*ids = NULL;
		*count = 0;
		return S_OK;
	}

private:
	LONG ref_ = 1;
	IUnknown *outer_;
	IAudioProcessingObject *child_ = NULL;
	IAudioProcessingObjectRT *childRT_ = NULL;
	IAudioProcessingObjectConfiguration *childCfg_ = NULL;
	IAudioSystemEffects2 *childFx_ = NULL;

	void releaseChild()
	{
		if (childFx_)
			childFx_->Release();
		if (childCfg_)
			childCfg_->Release();
		if (childRT_)
			childRT_->Release();
		if (child_)
			child_->Release();
		child_ = NULL;
		childRT_ = NULL;
		childCfg_ = NULL;
		childFx_ = NULL;
	}

	static bool readClsid(IPropertyStore *ps, const PROPERTYKEY &key, CLSID *out)
	{
		if (!ps)
			return false;
		PROPVARIANT v;
		PropVariantInit(&v);
		bool ok = SUCCEEDED(ps->GetValue(key, &v)) && v.vt == VT_LPWSTR && v.pwszVal &&
		          SUCCEEDED(CLSIDFromString(v.pwszVal, out));
		PropVariantClear(&v);
		return ok;
	}

	/* Yerine geçtiğimiz özgün efekti bul: önce FxProperties'teki kayıt, yoksa aygıt GUID'iyle
	 * HKLM\SOFTWARE\Ekolayzer\Endpoints\{guid} altındaki "child" değeri. */
	static bool findChild(APOInitSystemEffects *se, CLSID *out)
	{
		if (readClsid(se->pAPOSystemEffectsProperties, PKEY_Ekz_Child, out))
			return true;
		IPropertyStore *ep = se->pAPOEndpointProperties;
		if (!ep)
			return false;
		PROPVARIANT v;
		PropVariantInit(&v);
		bool ok = false;
		if (SUCCEEDED(ep->GetValue(PKEY_Endpoint_Guid, &v)) && v.vt == VT_LPWSTR && v.pwszVal) {
			wchar_t key[128], val[64];
			swprintf_s(key, L"SOFTWARE\\Ekolayzer\\Endpoints\\%s", v.pwszVal);
			DWORD sz = sizeof(val);
			ok = RegGetValueW(HKEY_LOCAL_MACHINE, key, L"child", RRF_RT_REG_SZ, NULL, val, &sz) == ERROR_SUCCESS &&
			     SUCCEEDED(CLSIDFromString(val, out));
		}
		PropVariantClear(&v);
		return ok;
	}

	void createChild(APOInitSystemEffects *se, UINT32 cb)
	{
		CLSID clsid;
		if (!findChild(se, &clsid) || clsid == CLSID_EkolayzerApo)
			return;
		IAudioProcessingObject *c = NULL;
		if (FAILED(CoCreateInstance(clsid, NULL, CLSCTX_INPROC_SERVER, __uuidof(IAudioProcessingObject), (void **)&c)))
			return;
		/* özgün efekt kendi CLSID'ini bekler */
		BYTE *copy = (BYTE *)malloc(cb);
		if (!copy) {
			c->Release();
			return;
		}
		memcpy(copy, se, cb);
		((APOInitBaseStruct *)copy)->clsid = clsid;
		HRESULT h = c->Initialize(cb, copy);
		free(copy);
		if (FAILED(h)) {
			c->Release();
			return;
		}
		child_ = c;
		if (FAILED(c->QueryInterface(__uuidof(IAudioProcessingObjectRT), (void **)&childRT_)) ||
		    FAILED(c->QueryInterface(__uuidof(IAudioProcessingObjectConfiguration), (void **)&childCfg_))) {
			releaseChild();
			return;
		}
		c->QueryInterface(__uuidof(IAudioSystemEffects2), (void **)&childFx_);
	}

	LONG id_;
	bool locked_ = false, bypassed_ = false, meter_ = false;
	UINT32 ch_ = 2;
	float rate_ = 48000.0f;
	SharedFile shm_;
	long last_seq_ = -1;
	EkzParams prm_;

	/* EQ */
	struct Coef {
		double b0, b1, b2, a1, a2;
	} coef_[EKZ_BANDS];
	int active_[EKZ_BANDS];
	int n_active_ = 0;
	double *z_ = NULL; /* [band][kanal][2] */

	/* limiter (ekolayzer_dyn.c ile aynı) */
	float gain_lin_ = 1.0f, gain_target_ = 1.0f;
	float env_ = 1.0f;
	double box_sum_ = 0;
	unsigned L_ = 1;
	UINT64 n_ = 0;
	unsigned w_ = 0;
	float *dl_ = NULL; /* [kanal][RING] */
	float *box_ = NULL;
	UINT64 *dq_idx_ = NULL;
	float *dq_val_ = NULL;
	unsigned dq_head_ = 0, dq_tail_ = 0;
	float thresh_, slope_, knee_, ceil_db_, ceil_lin_, lim_lin_, rel_a_;
	UINT64 quiet_frames_ = 0;

	/* ölçüm */
	unsigned sub_n_ = 0;
	float acc_l_ = 0, acc_r_ = 0, acc_in_ = 0, acc_g_ = 1.0f;

	static bool isFloat32(const UNCOMPRESSEDAUDIOFORMAT &f)
	{
		return f.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && f.dwBytesPerSampleContainer == 4 &&
		       f.dwValidBitsPerSample == 32;
	}

	HRESULT checkFormat(IAudioMediaType *opposite, IAudioMediaType *req, IAudioMediaType **supported)
	{
		if (!req || !supported)
			return E_POINTER;
		*supported = NULL;
		UNCOMPRESSEDAUDIOFORMAT r, o;
		if (FAILED(req->GetUncompressedAudioFormat(&r)))
			return APOERR_FORMAT_NOT_SUPPORTED;
		bool ok = isFloat32(r) && r.dwSamplesPerFrame >= 1 && r.dwSamplesPerFrame <= MAX_CH;
		if (ok && opposite && SUCCEEDED(opposite->GetUncompressedAudioFormat(&o)))
			ok = o.dwSamplesPerFrame == r.dwSamplesPerFrame && o.fFramesPerSecond == r.fFramesPerSecond;
		if (ok) {
			req->AddRef();
			*supported = req;
			return S_OK;
		}
		/* karşı taraftaki biçim uygunsa onu öner */
		if (opposite && SUCCEEDED(opposite->GetUncompressedAudioFormat(&o)) && isFloat32(o) &&
		    o.dwSamplesPerFrame <= MAX_CH) {
			opposite->AddRef();
			*supported = opposite;
			return S_FALSE;
		}
		return APOERR_FORMAT_NOT_SUPPORTED;
	}

	bool allocBuffers()
	{
		freeBuffers();
		z_ = (double *)calloc((size_t)EKZ_BANDS * ch_ * 2, sizeof(double));
		dl_ = (float *)calloc((size_t)ch_ * RING, sizeof(float));
		box_ = (float *)calloc(RING, sizeof(float));
		dq_idx_ = (UINT64 *)calloc(RING, sizeof(UINT64));
		dq_val_ = (float *)calloc(RING, sizeof(float));
		return z_ && dl_ && box_ && dq_idx_ && dq_val_;
	}

	void freeBuffers()
	{
		free(z_);
		free(dl_);
		free(box_);
		free(dq_idx_);
		free(dq_val_);
		z_ = NULL;
		dl_ = box_ = dq_val_ = NULL;
		dq_idx_ = NULL;
	}

	void resetLookahead(unsigned L)
	{
		L_ = L;
		dq_head_ = dq_tail_ = 0;
		if (box_)
			for (unsigned i = 0; i < RING; i++)
				box_[i] = env_;
		box_sum_ = (double)env_ * L;
	}

	void resetState()
	{
		if (z_)
			memset(z_, 0, (size_t)EKZ_BANDS * ch_ * 2 * sizeof(double));
		if (dl_)
			memset(dl_, 0, (size_t)ch_ * RING * sizeof(float));
		env_ = 1.0f;
		n_ = 0;
		w_ = 0;
		gain_lin_ = gain_target_;
		quiet_frames_ = 0;
		resetLookahead(L_);
	}

	/* Arayüzün yazdığı ayarları al (seqlock, kilitsiz). */
	void pollParams()
	{
		EkzShared *sh = shm_.sh;
		if (sh) {
			long s1 = sh->seq;
			if (s1 != last_seq_ && !(s1 & 1)) {
				MemoryBarrier();
				EkzParams p = sh->p;
				MemoryBarrier();
				if (sh->seq == s1) {
					sanitize(&p);
					applyParams(p);
					last_seq_ = s1;
				}
			}
		} else if (last_seq_ == -1) {
			EkzParams p;
			default_params(&p);
			applyParams(p);
			last_seq_ = -2;
		}
	}

	void applyParams(const EkzParams &p)
	{
		/* EQ katsayıları (RBJ tepe süzgeci; PipeWire bq_peaking ile aynı) */
		int n = 0;
		for (int b = 0; b < EKZ_BANDS; b++) {
			const double g = p.gains[b];
			if (fabs(g) < 1e-3 || BAND_FREQS[b] >= rate_ * 0.49f) {
				/* kapanan bandın durumu sıfırlansın; yeniden açıldığında temiz başlasın */
				if (z_)
					memset(z_ + (size_t)b * ch_ * 2, 0, ch_ * 2 * sizeof(double));
				continue;
			}
			const double A = pow(10.0, g / 40.0);
			const double w0 = 2.0 * 3.14159265358979323846 * BAND_FREQS[b] / rate_;
			const double alpha = sin(w0) / (2.0 * BAND_Q);
			const double cw = cos(w0);
			const double a0 = 1.0 + alpha / A;
			coef_[b].b0 = (1.0 + alpha * A) / a0;
			coef_[b].b1 = -2.0 * cw / a0;
			coef_[b].b2 = (1.0 - alpha * A) / a0;
			coef_[b].a1 = -2.0 * cw / a0;
			coef_[b].a2 = (1.0 - alpha / A) / a0;
			active_[n++] = b;
		}
		n_active_ = n;

		/* limiter */
		thresh_ = p.threshold;
		knee_ = p.knee > 0 ? p.knee : 0.0f;
		ceil_db_ = p.ceiling < 0 ? p.ceiling : 0.0f;
		slope_ = p.ratio >= 19.99f ? 1.0f : 1.0f - 1.0f / (p.ratio > 1.0f ? p.ratio : 1.0f);
		ceil_lin_ = db2lin(ceil_db_);
		const float lim_db = fminf(thresh_ - knee_ * 0.5f, ceil_db_);
		lim_lin_ = db2lin(lim_db);
		unsigned L = (unsigned)(p.attack * 0.001f * rate_ + 0.5f);
		if (L < 1)
			L = 1;
		if (L > RING / 2)
			L = RING / 2;
		if (L != L_)
			resetLookahead(L);
		rel_a_ = 1.0f - expf(-1.0f / (p.release * 0.001f * rate_));
		gain_target_ = db2lin(p.preamp);
		if (p.enabled != prm_.enabled && p.enabled)
			bypassed_ = true;
		prm_ = p;
	}

	void flushMeter(int64_t now)
	{
		EkzShared *s = shm_.sh;
		if (s) {
			/* birden çok aygıtta çalışırken ölçümü son 300 ms içinde yazan örnek sürdürür.
			 * Dosya diskte kaldığı için önceki açılıştan kalan (şimdiden büyük) zaman damgası geçersizdir. */
			const long owner = s->owner;
			const int64_t ot = s->owner_tick;
			if (owner == id_ || ot > now || now - ot > 300) {
				if (owner != id_)
					InterlockedExchange(&s->owner, id_);
				s->owner_tick = now;
				const long c = s->count;
				EkzSlot *sl = &s->slot[(unsigned long)c % EKZ_SLOTS];
				sl->out_l = acc_l_;
				sl->out_r = acc_r_;
				sl->in_peak = acc_in_;
				sl->gr_db = acc_g_ < 1.0f ? -20.0f * log10f(acc_g_) : 0.0f;
				MemoryBarrier();
				s->count = c + 1;
			}
		}
		sub_n_ = 0;
		acc_l_ = acc_r_ = acc_in_ = 0.0f;
		acc_g_ = 1.0f;
	}

	void process(const float *src, float *dst, UINT32 frames, int64_t now)
	{
		const unsigned ch = ch_;
		const unsigned L = L_;
		const double invL = 1.0 / (double)L;
		const float g_target = gain_target_;
		const float g_step = (g_target - gain_lin_) / (float)(frames ? frames : 1);
		float g = gain_lin_;
		float x[MAX_CH];

		for (UINT32 i = 0; i < frames; i++) {
			g += g_step;
			const float *fi = src + (size_t)i * ch;
			float p = 0.0f;
			for (unsigned c = 0; c < ch; c++) {
				double v = fi[c];
				for (int k = 0; k < n_active_; k++) {
					const int b = active_[k];
					const Coef &q = coef_[b];
					double *z = z_ + ((size_t)b * ch + c) * 2;
					const double y = q.b0 * v + z[0];
					z[0] = q.b1 * v - q.a1 * y + z[1];
					z[1] = q.b2 * v - q.a2 * y;
					v = y;
				}
				const float xv = (float)v * g;
				x[c] = xv;
				const float a = fabsf(xv);
				if (a > p)
					p = a;
			}
			if (meter_ && p > acc_in_)
				acc_in_ = p;

			/* gereken kazanç */
			float r = 1.0f;
			if (p > lim_lin_) {
				const float lvl = 20.0f * log10f(p);
				const float over = lvl - thresh_;
				float gr = 0.0f;
				if (knee_ > 0.0f && 2.0f * fabsf(over) <= knee_)
					gr = slope_ * (over + knee_ * 0.5f) * (over + knee_ * 0.5f) / (2.0f * knee_);
				else if (over > 0.0f)
					gr = slope_ * over;
				if (lvl - gr > ceil_db_)
					gr = lvl - ceil_db_;
				if (gr > 0.0f)
					r = db2lin(-gr);
			}

			/* kayan minimum (monoton kuyruk) */
			const UINT64 n = n_++;
			while (dq_tail_ != dq_head_ && dq_val_[(dq_tail_ - 1) & MASK] >= r)
				dq_tail_--;
			dq_idx_[dq_tail_ & MASK] = n;
			dq_val_[dq_tail_ & MASK] = r;
			dq_tail_++;
			while (dq_idx_[dq_head_ & MASK] + L <= n)
				dq_head_++;
			const float m = dq_val_[dq_head_ & MASK];

			/* bırakma zarfı (her zaman e <= m) */
			float e = env_;
			e = m < e ? m : e + (m - e) * rel_a_;
			env_ = e;

			/* atak: L uzunluklu kayan ortalama */
			const unsigned w = w_;
			box_sum_ += (double)e - (double)box_[(w - L) & MASK];
			box_[w] = e;
			float gg = (float)(box_sum_ * invL);
			if (gg > 1.0f)
				gg = 1.0f;
			if (meter_ && gg < acc_g_)
				acc_g_ = gg;

			/* gecikme hattı + emniyet tavanı */
			const unsigned rd = (w - (L - 1)) & MASK;
			float *fo = dst + (size_t)i * ch;
			for (unsigned c = 0; c < ch; c++) {
				float *d = dl_ + (size_t)c * RING;
				d[w] = x[c];
				float y = d[rd] * gg;
				if (y > ceil_lin_)
					y = ceil_lin_;
				else if (y < -ceil_lin_)
					y = -ceil_lin_;
				fo[c] = y;
			}
			w_ = (w + 1) & MASK;

			if (meter_) {
				const float al = fabsf(fo[0]), ar = fabsf(fo[ch > 1 ? 1 : 0]);
				if (al > acc_l_)
					acc_l_ = al;
				if (ar > acc_r_)
					acc_r_ = ar;
				if (++sub_n_ >= LV_SUB)
					flushMeter(now);
			}
		}
		gain_lin_ = g_target;
		/* kayan ortalama toplamı uzun sürede kayabilir: ara sıra yeniden eşitle */
		if ((n_ & 0xFFFFF) < frames) {
			double s = 0;
			for (unsigned k = 1; k <= L; k++)
				s += box_[(w_ - k) & MASK];
			box_sum_ = s;
		}
	}
};

/* ------------------------------------------------------------------ COM sınıf fabrikası */

class Factory : public IClassFactory {
public:
	STDMETHODIMP QueryInterface(REFIID riid, void **ppv) override
	{
		if (!ppv)
			return E_POINTER;
		if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory)) {
			*ppv = static_cast<IClassFactory *>(this);
			return S_OK;
		}
		*ppv = NULL;
		return E_NOINTERFACE;
	}
	STDMETHODIMP_(ULONG) AddRef() override { return 2; }
	STDMETHODIMP_(ULONG) Release() override { return 1; }
	STDMETHODIMP CreateInstance(IUnknown *outer, REFIID riid, void **ppv) override
	{
		if (!ppv)
			return E_POINTER;
		*ppv = NULL;
		if (outer && riid != __uuidof(IUnknown))
			return CLASS_E_NOAGGREGATION;
		EkolayzerApo *apo = new (std::nothrow) EkolayzerApo(outer);
		if (!apo)
			return E_OUTOFMEMORY;
		HRESULT hr = apo->NDQueryInterface(riid, ppv);
		apo->NDRelease();
		return hr;
	}
	STDMETHODIMP LockServer(BOOL lock) override
	{
		if (lock)
			InterlockedIncrement(&g_locks);
		else
			InterlockedDecrement(&g_locks);
		return S_OK;
	}
};

static Factory g_factory;

extern "C" BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH) {
		g_module = inst;
		DisableThreadLibraryCalls(inst);
	}
	return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID *ppv)
{
	if (clsid != CLSID_EkolayzerApo)
		return CLASS_E_CLASSNOTAVAILABLE;
	return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
	return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}
