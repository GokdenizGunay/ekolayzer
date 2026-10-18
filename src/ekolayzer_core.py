"""Ekolayzer ortak çekirdek: ayarlar, hazır ayarlar, PipeWire yapılandırması ve canlı kontrol."""

import json
import math
import os
import subprocess

VERSION = "1.3.0"
APP_ID = "io.github.ekolayzer"
NODE_NAME = "ekolayzer"
CAPTURE_NODE = "effect_input." + NODE_NAME
PLAYBACK_NODE = "effect_output." + NODE_NAME
SERVICE = "ekolayzer.service"
PLUGIN_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ekolayzer_dyn.so")

SAMPLE_RATE = 48000
BAND_FREQS = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]
BAND_Q = 1.41
GAIN_MIN = -12.0
GAIN_MAX = 12.0
CHANNELS = ("L", "R")

PRESETS = {
    "Düz": [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    "Bas Güçlendirme": [7, 6, 5, 3, 1, 0, 0, 0, 0, 0],
    "Bas Azaltma": [-6, -5, -4, -2, -1, 0, 0, 0, 0, 0],
    "Tiz Güçlendirme": [0, 0, 0, 0, 0, 1, 3, 5, 6, 7],
    "Vokal": [-3, -2, -1, 1, 4, 5, 4, 2, 0, -1],
    "Rock": [5, 4, 2, -1, -2, -1, 2, 4, 5, 5],
    "Pop": [-1, 1, 3, 4, 3, 0, -1, -1, 1, 2],
    "Caz": [3, 2, 1, 2, -1, -1, 0, 1, 3, 4],
    "Klasik": [4, 3, 2, 1, 0, 0, 0, 2, 3, 4],
    "Elektronik": [6, 5, 1, 0, -2, 1, 0, 2, 5, 6],
    "Akustik": [4, 3, 2, 1, 2, 2, 3, 3, 3, 2],
    "Konuşma / Podcast": [-6, -4, -1, 2, 4, 4, 3, 1, -1, -3],
    "Gece Modu": [-4, -3, -2, 0, 1, 2, 2, 1, -1, -2],
}

# Limiter parametreleri: ayar anahtarı -> (eklenti portu, en küçük, en büyük, varsayılan)
LIMITER_PARAMS = {
    "threshold": ("Threshold", -30.0, 0.0, -1.0),
    "ratio": ("Ratio", 1.0, 20.0, 20.0),
    "knee": ("Knee", 0.0, 12.0, 2.0),
    "attack": ("Attack", 0.5, 10.0, 2.0),
    "release": ("Release", 10.0, 2000.0, 120.0),
    "ceiling": ("Ceiling", -12.0, 0.0, -0.3),
}

DEFAULT_SETTINGS = {
    "enabled": True,
    "preamp": 0.0,
    "gains": [0.0] * len(BAND_FREQS),
    "preset": "Düz",
    "custom_presets": {},
    "limiter": dict({"enabled": True}, **{k: v[3] for k, v in LIMITER_PARAMS.items()}),
    "limiter_panel": False,
}


def config_dir():
    base = os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config")
    return os.path.join(base, "ekolayzer")


def settings_path():
    return os.path.join(config_dir(), "settings.json")


def runtime_dir():
    base = os.environ.get("XDG_RUNTIME_DIR") or "/tmp"
    return os.path.join(base, "ekolayzer")


def levels_path():
    return os.path.join(runtime_dir(), "levels")


def clamp(v, lo, hi):
    return max(lo, min(hi, v))


def load_settings():
    s = json.loads(json.dumps(DEFAULT_SETTINGS))
    data = {}
    try:
        with open(settings_path(), encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            s.update({k: v for k, v in data.items() if k in s})
    except (OSError, ValueError):
        data = {}
    gains = [float(g) for g in s.get("gains", [])][: len(BAND_FREQS)]
    gains += [0.0] * (len(BAND_FREQS) - len(gains))
    s["gains"] = [clamp(g, GAIN_MIN, GAIN_MAX) for g in gains]
    s["preamp"] = clamp(float(s.get("preamp", 0.0)), GAIN_MIN, GAIN_MAX)
    s["enabled"] = bool(s.get("enabled", True))
    s["limiter_panel"] = bool(s.get("limiter_panel", False))
    if not isinstance(s.get("custom_presets"), dict):
        s["custom_presets"] = {}

    lim = dict(DEFAULT_SETTINGS["limiter"])
    if isinstance(s.get("limiter"), dict):
        lim.update(s["limiter"])
    elif isinstance(data, dict) and "auto_headroom" in data:  # 1.1.0 ayarlarından geçiş
        lim["enabled"] = bool(data["auto_headroom"])
    lim["enabled"] = bool(lim.get("enabled", True))
    for k, (_port, lo, hi, dflt) in LIMITER_PARAMS.items():
        try:
            lim[k] = clamp(float(lim.get(k, dflt)), lo, hi)
        except (TypeError, ValueError):
            lim[k] = dflt
    s["limiter"] = lim
    return s


def save_settings(s):
    os.makedirs(config_dir(), exist_ok=True)
    tmp = settings_path() + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=2)
    os.replace(tmp, settings_path())


# ---------------------------------------------------------------- PipeWire

def _dyn_controls(s):
    lim = s["limiter"]
    ctl = {"Gain": s["preamp"], "Enable": 1.0 if lim["enabled"] else 0.0}
    for k, (port, _lo, _hi, _d) in LIMITER_PARAMS.items():
        ctl[port] = lim[k]
    return ctl


def build_filter_chain_conf(s):
    """Ayarlardan bağımsız bir pipewire süreci için filter-chain yapılandırması üretir.

    Grafik: her kanal için 10 tepe (peaking) süzgeci -> stereo bağlı limiter (ön kazanç dahil).
    """
    nodes, links = [], []
    for ch in CHANNELS:
        for i, (freq, gain) in enumerate(zip(BAND_FREQS, s["gains"]), start=1):
            nodes.append('{ type = builtin name = b%d%s label = bq_peaking '
                         'control = { "Freq" = %.1f "Q" = %.2f "Gain" = %.2f } }'
                         % (i, ch, freq, BAND_Q, gain))
            if i > 1:
                links.append('{ output = "b%d%s:Out" input = "b%d%s:In" }' % (i - 1, ch, i, ch))
        links.append('{ output = "b%d%s:Out" input = "dyn:In %s" }' % (len(BAND_FREQS), ch, ch))
    ctl = " ".join('"%s" = %.3f' % kv for kv in _dyn_controls(s).items())
    nodes.append('{ type = ladspa name = dyn plugin = "%s" label = ekolayzer_dyn '
                 'control = { %s } }' % (PLUGIN_PATH, ctl))
    ind = "\n" + " " * 20
    return """# Ekolayzer tarafından otomatik üretildi - elle düzenlemeyin.
context.properties = {
    log.level = 0
}
context.spa-libs = {
    audio.convert.* = audioconvert/libspa-audioconvert
    support.*       = support/libspa-support
}
context.modules = [
    { name = libpipewire-module-rt args = { } flags = [ ifexists nofail ] }
    { name = libpipewire-module-protocol-native }
    { name = libpipewire-module-client-node }
    { name = libpipewire-module-adapter }
    { name = libpipewire-module-filter-chain
        args = {
            node.description = "Ekolayzer"
            media.name       = "Ekolayzer"
            filter.graph = {
                nodes = [%(nodes)s
                ]
                links = [%(links)s
                ]
                inputs  = [ "b1L:In" "b1R:In" ]
                outputs = [ "dyn:Out L" "dyn:Out R" ]
            }
            audio.channels = 2
            audio.position = [ FL FR ]
            capture.props = {
                node.name         = "%(cap)s"
                media.class       = Audio/Sink
                filter.smart      = true
                filter.smart.name = "%(name)s"
            }
            playback.props = {
                node.name         = "%(play)s"
                node.passive      = true
                stream.dont-remix = true
                filter.smart      = true
                filter.smart.name = "%(name)s"
            }
        }
    }
]
""" % {
        "nodes": "".join(ind + n for n in nodes),
        "links": "".join(ind + l for l in links),
        "cap": CAPTURE_NODE,
        "play": PLAYBACK_NODE,
        "name": NODE_NAME,
    }


def props_pod(s):
    parts = []
    for i, g in enumerate(s["gains"], start=1):
        for ch in CHANNELS:
            parts.append('"b%d%s:Gain" %.2f' % (i, ch, g))
    parts += ['"dyn:%s" %.3f' % kv for kv in _dyn_controls(s).items()]
    return "{ params = [ %s ] }" % " ".join(parts)


def apply_live(s):
    """Çalışan filtrenin parametrelerini sesi kesmeden günceller (düğüm adıyla, kimlik araması yok)."""
    try:
        r = subprocess.run(["pw-cli", "set-param", CAPTURE_NODE, "Props", props_pod(s)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True, timeout=5)
        return r.returncode == 0 and not r.stderr.strip()
    except (OSError, subprocess.SubprocessError):
        return False


def systemctl(*args):
    try:
        return subprocess.run(["systemctl", "--user", *args],
                              capture_output=True, text=True, timeout=15).returncode
    except (OSError, subprocess.SubprocessError):
        return -1


def service_active():
    return systemctl("is-active", "--quiet", SERVICE) == 0


# ---------------------------------------------------------------- Frekans yanıtı

def _peaking_coeffs(f0, q, gain_db, fs=SAMPLE_RATE):
    a = 10 ** (gain_db / 40.0)
    w0 = 2 * math.pi * f0 / fs
    alpha = math.sin(w0) / (2 * q)
    cw = math.cos(w0)
    b0, b1, b2 = 1 + alpha * a, -2 * cw, 1 - alpha * a
    a0, a1, a2 = 1 + alpha / a, -2 * cw, 1 - alpha / a
    return b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0


def response_db(freq, s, fs=SAMPLE_RATE):
    """Verilen frekansta toplam kazanç (dB)."""
    w = 2 * math.pi * freq / fs
    z1 = complex(math.cos(-w), math.sin(-w))
    z2 = z1 * z1
    total = s["preamp"]
    for f0, g in zip(BAND_FREQS, s["gains"]):
        if abs(g) < 1e-6:
            continue
        b0, b1, b2, a1, a2 = _peaking_coeffs(f0, BAND_Q, g, fs)
        h = (b0 + b1 * z1 + b2 * z2) / (1 + a1 * z1 + a2 * z2)
        total += 20 * math.log10(max(abs(h), 1e-9))
    return total


_GRID = sorted(set([20 * (1000 ** (i / 120)) for i in range(121)] + BAND_FREQS))


def peak_gain(s):
    """Ön kazanç dahil, herhangi bir frekanstaki en yüksek toplam kazanç (dB)."""
    return max(response_db(f, s) for f in _GRID)
