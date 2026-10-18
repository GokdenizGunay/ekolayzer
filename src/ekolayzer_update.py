"""Ekolayzer güncelleme denetimi: GitHub'daki son sürümü bulur, paketi indirip doğrular ve kurar.

Bu modül GTK yüklemez; oturum açılışındaki arka plan denetimi güncelleme yoksa
arayüz kitaplıklarını hiç yüklemeden biter.
"""

import hashlib
import json
import os
import re
import subprocess
import urllib.error
import urllib.request

import ekolayzer_core as core

REPO = "GokdenizGunay/ekolayzer"
# Test için EKOLAYZER_UPDATE_URL ile başka bir adres verilebilir.
API_URL = os.environ.get("EKOLAYZER_UPDATE_URL") or \
    "https://api.github.com/repos/%s/releases/latest" % REPO
HELPER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ekolayzer-update-install")
USER_AGENT = "ekolayzer/%s" % core.VERSION


class NetworkError(Exception):
    """Ağa ulaşılamadı (oturum açılışında ağ henüz hazır olmayabilir)."""


def parse_version(text):
    m = re.match(r"\s*v?(\d+(?:\.\d+)*)", text or "")
    return tuple(int(x) for x in m.group(1).split(".")) if m else None


def _arch():
    try:
        return subprocess.run(["dpkg", "--print-architecture"], capture_output=True,
                              text=True, timeout=5).stdout.strip() or "amd64"
    except (OSError, subprocess.SubprocessError):
        return "amd64"


def _summary(body, limit=6):
    """Sürüm notlarından kısa bir madde listesi."""
    bullets = [l.strip()[2:].strip() for l in (body or "").splitlines() if l.strip().startswith("- ")]
    bullets = [re.sub(r"\*\*|`", "", b) for b in bullets][:limit]
    return bullets


def check(timeout=10):
    """Yüklü sürümden yeni bir sürüm varsa bilgi sözlüğü, yoksa None döndürür."""
    req = urllib.request.Request(API_URL, headers={
        "Accept": "application/vnd.github+json", "User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            rel = json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 404:  # henüz sürüm yok
            return None
        raise NetworkError(str(e)) from e
    except (urllib.error.URLError, OSError, ValueError) as e:
        raise NetworkError(str(e)) from e

    if rel.get("draft") or rel.get("prerelease"):
        return None
    tag = rel.get("tag_name", "")
    new, cur = parse_version(tag), parse_version(core.VERSION)
    if not new or not cur or new <= cur:
        return None

    arch = _arch()
    asset = next((a for a in rel.get("assets", [])
                  if re.fullmatch(r"ekolayzer_[\w.~+-]+_%s\.deb" % re.escape(arch), a.get("name", ""))), None)
    sha = None
    if asset:
        digest = asset.get("digest") or ""
        if digest.startswith("sha256:"):
            sha = digest.split(":", 1)[1].lower()
        else:  # sürüm notlarındaki "SHA-256  <özet>  <dosya>" satırı
            m = re.search(r"([0-9a-fA-F]{64})\s+%s" % re.escape(asset["name"]), rel.get("body") or "")
            sha = m.group(1).lower() if m else None
    return {
        "version": ".".join(map(str, new)),
        "current": core.VERSION,
        "name": rel.get("name") or tag,
        "page": rel.get("html_url") or "https://github.com/%s/releases" % REPO,
        "summary": _summary(rel.get("body")),
        "asset_url": asset.get("browser_download_url") if asset and sha else None,
        "asset_name": asset.get("name") if asset else None,
        "size": asset.get("size", 0) if asset else 0,
        "sha256": sha,
    }


def cache_dir():
    base = os.environ.get("XDG_CACHE_HOME") or os.path.expanduser("~/.cache")
    return os.path.join(base, "ekolayzer", "updates")


def download(info, progress=None, timeout=30):
    """Paketi indirir, SHA-256 özetini doğrular ve dosya yolunu döndürür."""
    os.makedirs(cache_dir(), exist_ok=True)
    path = os.path.join(cache_dir(), os.path.basename(info["asset_name"]))
    tmp = path + ".part"
    h = hashlib.sha256()
    req = urllib.request.Request(info["asset_url"], headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r, open(tmp, "wb") as f:
            total = int(r.headers.get("Content-Length") or info.get("size") or 0)
            done = 0
            while True:
                chunk = r.read(65536)
                if not chunk:
                    break
                f.write(chunk)
                h.update(chunk)
                done += len(chunk)
                if progress and total:
                    progress(done / total)
    except (urllib.error.URLError, OSError) as e:
        raise NetworkError(str(e)) from e
    if h.hexdigest() != info["sha256"]:
        os.unlink(tmp)
        raise ValueError("İndirilen dosyanın SHA-256 özeti eşleşmiyor; güncelleme iptal edildi.")
    os.replace(tmp, path)
    return path


def install(path):
    """Sistem parola penceresiyle (polkit) paketi kurar. (başarılı mı, ileti) döndürür."""
    try:
        r = subprocess.run(["pkexec", HELPER, path], capture_output=True, text=True)
    except OSError as e:
        return False, str(e)
    if r.returncode == 0:
        try:
            os.unlink(path)
        except OSError:
            pass
        return True, ""
    if r.returncode in (126, 127):
        return False, "Kimlik doğrulama iptal edildi."
    msg = (r.stderr or r.stdout).strip().splitlines()
    return False, msg[-1] if msg else "Kurulum başarısız oldu (çıkış kodu %d)." % r.returncode
