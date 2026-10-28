// Ekolayzer ölçüm aracı: bir çıkış aygıtına sinüs çalar, aynı aygıtı geri döngü (loopback) ile kaydeder
// ve ses motorundan geçen sinyalin seviyesini ölçer.
//   wasapi_test list
//   wasapi_test <aygıt-guid> <frekans-Hz> <seviye-dBFS> [süre-s]
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <math.h>
#include <stdio.h>
#include <string>
#include <vector>

#pragma comment(lib, "ole32.lib")

#define CHECK(x)                                                                                   \
	do {                                                                                           \
		HRESULT hr_ = (x);                                                                         \
		if (FAILED(hr_)) {                                                                         \
			fprintf(stderr, "%s satir %d: 0x%08lx\n", #x, __LINE__, (unsigned long)hr_);          \
			return 1;                                                                              \
		}                                                                                          \
	} while (0)

int wmain(int argc, wchar_t **argv)
{
	SetConsoleOutputCP(CP_UTF8);
	CoInitializeEx(NULL, COINIT_MULTITHREADED);
	IMMDeviceEnumerator *en = NULL;
	CHECK(CoCreateInstance(__uuidof(MMDeviceEnumerator), NULL, CLSCTX_ALL, IID_PPV_ARGS(&en)));
	if (argc < 2 || !wcscmp(argv[1], L"list")) {
		IMMDeviceCollection *col;
		CHECK(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col));
		UINT n;
		col->GetCount(&n);
		for (UINT i = 0; i < n; i++) {
			IMMDevice *d;
			col->Item(i, &d);
			LPWSTR id;
			d->GetId(&id);
			IPropertyStore *ps;
			d->OpenPropertyStore(STGM_READ, &ps);
			PROPVARIANT v;
			PropVariantInit(&v);
			ps->GetValue(PKEY_Device_FriendlyName, &v);
			wprintf(L"%s  %s\n", id, v.pwszVal);
		}
		return 0;
	}
	std::wstring id = std::wstring(L"{0.0.0.00000000}.") + argv[1];
	double freq = _wtof(argv[2]), level = _wtof(argv[3]);
	double secs = argc > 4 ? _wtof(argv[4]) : 2.0;
	IMMDevice *dev;
	CHECK(en->GetDevice(id.c_str(), &dev));

	IAudioClient *rc, *cc;
	CHECK(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, NULL, (void **)&rc));
	CHECK(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, NULL, (void **)&cc));
	WAVEFORMATEX *fmt;
	CHECK(rc->GetMixFormat(&fmt));
	const int ch = fmt->nChannels;
	const double rate = fmt->nSamplesPerSec;
	CHECK(rc->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 2000000, 0, fmt, NULL));
	CHECK(cc->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, fmt, NULL));
	IAudioRenderClient *rend;
	IAudioCaptureClient *cap;
	CHECK(rc->GetService(IID_PPV_ARGS(&rend)));
	CHECK(cc->GetService(IID_PPV_ARGS(&cap)));
	UINT32 bufFrames;
	rc->GetBufferSize(&bufFrames);

	const double amp = pow(10.0, level / 20.0);
	double phase = 0, dph = 2 * 3.14159265358979 * freq / rate;
	CHECK(rc->Start());
	CHECK(cc->Start());
	ULONGLONG t0 = GetTickCount64();
	double peak = 0, sumsq = 0;
	UINT64 counted = 0;
	const double settle = 0.6;
	while (GetTickCount64() - t0 < (ULONGLONG)((secs + settle) * 1000)) {
		UINT32 pad;
		rc->GetCurrentPadding(&pad);
		UINT32 avail = bufFrames - pad;
		if (avail > 0) {
			BYTE *buf;
			if (SUCCEEDED(rend->GetBuffer(avail, &buf))) {
				float *f = (float *)buf;
				for (UINT32 i = 0; i < avail; i++) {
					float s = (float)(amp * sin(phase));
					phase += dph;
					if (phase > 6.283185307179586)
						phase -= 6.283185307179586;
					for (int c = 0; c < ch; c++)
						f[i * ch + c] = s;
				}
				rend->ReleaseBuffer(avail, 0);
			}
		}
		UINT32 pk;
		while (SUCCEEDED(cap->GetNextPacketSize(&pk)) && pk) {
			BYTE *data;
			UINT32 n;
			DWORD flags;
			if (FAILED(cap->GetBuffer(&data, &n, &flags, NULL, NULL)))
				break;
			bool measure = GetTickCount64() - t0 > settle * 1000;
			if (measure && !(flags & AUDCLNT_BUFFERFLAGS_SILENT)) {
				float *f = (float *)data;
				for (UINT32 i = 0; i < n; i++) {
					double v = f[i * ch];
					if (fabs(v) > peak)
						peak = fabs(v);
					sumsq += v * v;
				}
				counted += n;
			} else if (measure) {
				counted += n;
			}
			cap->ReleaseBuffer(n);
		}
		Sleep(5);
	}
	rc->Stop();
	cc->Stop();
	double rms = counted ? sqrt(sumsq / counted) : 0;
	printf("kanal=%d hiz=%.0f giris=%.2f dBFS (tepe)  olculen tepe=%.2f dBFS  rms=%.2f dBFS  (rms+3.01=%.2f) kare=%llu\n", ch,
	       rate, level, peak > 0 ? 20 * log10(peak) : -999.0, rms > 0 ? 20 * log10(rms) : -999.0,
	       rms > 0 ? 20 * log10(rms) + 3.0103 : -999.0, (unsigned long long)counted);
	return 0;
}
