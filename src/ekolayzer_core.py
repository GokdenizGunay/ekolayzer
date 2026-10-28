"""Ekolayzer ortak çekirdek: ayarlar, hazır ayarlar, PipeWire yapılandırması ve canlı kontrol."""

import json
import math
import os
import re
import subprocess

VERSION = "1.4.1"
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
    "update_skip": "",  # "Tekrar gösterme" ile atlanan sürüm
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
    s["update_skip"] = s["update_skip"] if isinstance(s.get("update_skip"), str) else ""
    if not isinstance(s.get("custom_presets"), dict):
        s["custom_presets"] = {}

    lim = dict(DEFAULT_SETTINGS["limiter"])
    if isinstance(s.get("limiter"), dict):
        lim.update(s["limiter"])
    lim["enabled"] = True  # limiter her zaman açık (eski ayar dosyalarındaki "kapalı" yok sayılır)
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
    ctl = {"Gain": s["preamp"], "Enable": 1.0}  # limiter her zaman açık
    for k, (port, _lo, _hi, _d) in LIMITER_PARAMS.items():
        ctl[port] = lim[k]
    return ctl


def wireplumber_version():
    """Kurulu WirePlumber sürümü (ör. (0, 5, 8)); bulunamazsa None."""
    try:
        out = subprocess.run(["wireplumber", "--version"], capture_output=True, text=True,
                             timeout=5).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r"Linked with libwireplumber (\d+)\.(\d+)\.(\d+)", out) or \
        re.search(r"(\d+)\.(\d+)\.(\d+)", out)
    return tuple(int(x) for x in m.groups()) if m else None


def smart_filters_supported():
    """WirePlumber 0.5+ "smart filter" desteği (Pardus 25 / ETAP 25). Yoksa (ETAP 23) klasik yönlendirme."""
    v = wireplumber_version()
    return v is None or v >= (0, 5, 0)


def build_filter_chain_conf(s, legacy=False, target=None):
    """Ayarlardan bağımsız bir pipewire süreci için filter-chain yapılandırması üretir.

    Grafik: her kanal için 10 tepe (peaking) süzgeci -> stereo bağlı limiter (ön kazanç dahil).
    legacy=False: WirePlumber 0.5 smart filter; filtre varsayılan çıkışın önüne kendiliğinden girer.
    legacy=True : WirePlumber 0.4; filtre çıkışı `target` aygıtına bağlanır, filtre girişi
                  denetleyici tarafından varsayılan çıkış yapılır.
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
                node.description  = "Ekolayzer"
                media.class       = Audio/Sink%(cap_extra)s
            }
            playback.props = {
                node.name         = "%(play)s"
                node.passive      = true
                stream.dont-remix = true%(play_extra)s
            }
        }
    }
]
""" % {
        "nodes": "".join(ind + n for n in nodes),
        "links": "".join(ind + l for l in links),
        "cap": CAPTURE_NODE,
        "play": PLAYBACK_NODE,
        "cap_extra": _props_extra(legacy, None),
        "play_extra": _props_extra(legacy, target),
    }


def _props_extra(legacy, target):
    ind = "\n" + " " * 16
    if not legacy:
        return ind + "filter.smart      = true" + ind + 'filter.smart.name = "%s"' % NODE_NAME
    if target:
        t = target.replace('"', "")
        return ind + 'target.object     = "%s"' % t + ind + 'node.target       = "%s"' % t
    return ""


# ---------------------------------------------------------------- Klasik (WirePlumber 0.4) yönlendirme

def _pw_dump_nodes():
    try:
        out = subprocess.run(["pw-dump"], capture_output=True, text=True, timeout=10).stdout
        return [o for o in json.loads(out or "[]") if o.get("type") == "PipeWire:Interface:Node"]
    except (OSError, ValueError, subprocess.SubprocessError):
        return []


def node_id(name):
    for o in _pw_dump_nodes():
        if o.get("info", {}).get("props", {}).get("node.name") == name:
            return o["id"]
    return None


def hardware_sinks():
    """Ekolayzer dışındaki ses çıkış aygıtları: [(kimlik, ad, açıklama)]."""
    res = []
    for o in _pw_dump_nodes():
        p = o.get("info", {}).get("props", {})
        if p.get("media.class") == "Audio/Sink" and p.get("node.name") != CAPTURE_NODE:
            res.append((o["id"], p.get("node.name"), p.get("node.description", "")))
    return res


def parse_metadata_name(value):
    """'{"name":"alsa_output..."}' biçimindeki meta veri değerinden aygıt adı."""
    m = re.search(r'"name"\s*:\s*"([^"]+)"', value or "")
    return m.group(1) if m else None


def get_default_sink():
    try:
        out = subprocess.run(["pw-metadata", "0", "default.audio.sink"], capture_output=True,
                             text=True, timeout=5).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    m = re.search(r"value:'([^']*)'", out)
    return parse_metadata_name(m.group(1)) if m else None


def set_default_sink(name):
    """Varsayılan çıkışı ayarlar (WirePlumber bunu kalıcı tercih olarak da saklar)."""
    try:
        subprocess.run(["pw-metadata", "0", "default.configured.audio.sink",
                        '{ "name": "%s" }' % name, "Spa:String:JSON"],
                       capture_output=True, timeout=5)
    except (OSError, subprocess.SubprocessError):
        pass


def set_stream_target(node, target_name, target_id=None):
    """Bir akışın bağlanacağı aygıtı değiştirir (WirePlumber 0.4 meta veri anahtarları)."""
    cmds = [["pw-metadata", str(node), "target.object", target_name]]
    if target_id is not None:
        cmds.append(["pw-metadata", str(node), "target.node", str(target_id)])
    for c in cmds:
        try:
            subprocess.run(c, capture_output=True, timeout=5)
        except (OSError, subprocess.SubprocessError):
            pass


def props_pairs(s):
    """Filtreye gönderilecek (parametre adı, değer) çiftleri."""
    pairs = []
    for i, g in enumerate(s["gains"], start=1):
        for ch in CHANNELS:
            pairs.append(("b%d%s:Gain" % (i, ch), "%.2f" % g))
    pairs += [("dyn:%s" % k, "%.3f" % v) for k, v in _dyn_controls(s).items()]
    return pairs


def props_pod(pairs):
    return "{ params = [ %s ] }" % " ".join('"%s" %s' % kv for kv in pairs)


# Eski pw-cli (PipeWire 0.3.x) tek komutta ~1 KB'tan büyük parametre paketini sessizce yok sayar;
# bu yüzden yalnızca değişen değerler, en fazla PARAM_CHUNK'lık parçalar hâlinde gönderilir.
PARAM_CHUNK = 10
_sent = {}
_node_id_cache = None


def _set_param(target, pairs):
    try:
        r = subprocess.run(["pw-cli", "set-param", str(target), "Props", props_pod(pairs)],
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=5)
    except (OSError, subprocess.SubprocessError):
        return False
    return r.returncode == 0 and not r.stderr.strip() and "rror" not in r.stdout


def apply_live(s, full=False):
    """Çalışan filtrenin parametrelerini sesi kesmeden günceller.

    Yalnızca son gönderimden bu yana değişen parametreleri yollar. Önce düğüm adıyla dener;
    pw-cli adı tanımazsa düğüm kimliğini bulup onunla dener.
    """
    global _node_id_cache
    pairs = props_pairs(s)
    todo = pairs if full or not _sent else [kv for kv in pairs if _sent.get(kv[0]) != kv[1]]
    if not todo:
        return True
    for target in (CAPTURE_NODE, _node_id_cache, "lookup"):
        if target is None:
            continue
        if target == "lookup":
            _node_id_cache = node_id(CAPTURE_NODE)
            if _node_id_cache is None:
                break
            target = _node_id_cache
        if all(_set_param(target, todo[i:i + PARAM_CHUNK]) for i in range(0, len(todo), PARAM_CHUNK)):
            _sent.update(todo)
            return True
    _sent.clear()  # durum bilinmiyor: bir sonraki çağrıda her şey yeniden gönderilsin
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
