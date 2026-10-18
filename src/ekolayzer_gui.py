"""Ekolayzer grafik arayüzü (GTK4 + libadwaita)."""

import json
import math
import os
import sys
import threading

import gi

# Basit, çizim ağırlıklı bu arayüz için cairo çizicisi GL'den çok daha az CPU ve bellek kullanır
# (ölçüm: ~130 MB daha az bellek). Kullanıcı GSK_RENDERER belirlediyse ona dokunulmaz.
os.environ.setdefault("GSK_RENDERER", "cairo")

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio, GLib, Gtk  # noqa: E402

import ekolayzer_core as core  # noqa: E402
import ekolayzer_update as upd  # noqa: E402
import ekolayzer_widgets as wdg  # noqa: E402

VERSION = core.VERSION
CUSTOM = "Özel"
METER_FPS = int(os.environ.get("EKOLAYZER_METER_FPS", "30"))  # gösterge yenileme hızı


def freq_label(f):
    return "%dk" % (f // 1000) if f >= 1000 else str(f)


class ResponseView(Gtk.DrawingArea):
    """Toplam frekans yanıtı eğrisi (bant yanıtları önbelleğe alınır, yalnızca değişen bant hesaplanır)."""

    F_MIN, F_MAX, DB_RANGE = 20.0, 20000.0, 15.0

    def __init__(self, get_settings):
        super().__init__()
        self.get_settings = get_settings
        self.set_content_height(150)
        self.set_hexpand(True)
        self.set_draw_func(self._draw)
        self._freqs = []
        self._band_cache = {}

    def _x(self, f, w):
        return w * math.log10(f / self.F_MIN) / math.log10(self.F_MAX / self.F_MIN)

    def _y(self, db, h):
        return h / 2 - (db / self.DB_RANGE) * (h / 2 - 6)

    def _band_curve(self, i, gain):
        key = (i, gain, len(self._freqs))
        c = self._band_cache.get(key)
        if c is None:
            one = {"preamp": 0.0, "gains": [gain if k == i else 0.0 for k in range(len(core.BAND_FREQS))]}
            c = [core.response_db(f, one) for f in self._freqs]
            if len(self._band_cache) > 400:
                self._band_cache.clear()
            self._band_cache[key] = c
        return c

    def _draw(self, _area, cr, w, h):
        fg = self.get_color()
        accent = Adw.StyleManager.get_default().get_accent_color_rgba()
        s = self.get_settings()

        cr.set_line_width(1)
        cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.12)
        for f in core.BAND_FREQS:
            x = round(self._x(f, w)) + 0.5
            cr.move_to(x, 0)
            cr.line_to(x, h)
        for db in (-12, -6, 6, 12):
            y = round(self._y(db, h)) + 0.5
            cr.move_to(0, y)
            cr.line_to(w, y)
        cr.stroke()
        cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.35)
        y0 = round(self._y(0, h)) + 0.5
        cr.move_to(0, y0)
        cr.line_to(w, y0)
        cr.stroke()

        steps = max(int(w / 3), 50)
        if len(self._freqs) != steps + 1:
            self._freqs = [self.F_MIN * (self.F_MAX / self.F_MIN) ** (i / steps) for i in range(steps + 1)]
            self._band_cache.clear()
        total = [s["preamp"]] * len(self._freqs)
        for i, g in enumerate(s["gains"]):
            if abs(g) > 1e-6:
                total = [a + b for a, b in zip(total, self._band_curve(i, g))]
        pts = [(self._x(f, w), self._y(max(-self.DB_RANGE, min(self.DB_RANGE, db)), h))
               for f, db in zip(self._freqs, total)]

        alpha = 1.0 if s["enabled"] else 0.35
        cr.move_to(pts[0][0], y0)
        for x, y in pts:
            cr.line_to(x, y)
        cr.line_to(pts[-1][0], y0)
        cr.close_path()
        cr.set_source_rgba(accent.red, accent.green, accent.blue, 0.18 * alpha)
        cr.fill()
        cr.move_to(*pts[0])
        for x, y in pts[1:]:
            cr.line_to(x, y)
        cr.set_line_width(2.5)
        cr.set_source_rgba(accent.red, accent.green, accent.blue, alpha)
        cr.stroke()


def _fmt_db(v):
    return "%+.1f dB" % v if abs(v) >= 0.05 else "0.0 dB"


def _fmt_ratio(v):
    return "∞:1" if v >= 19.95 else "%.1f:1" % v


def _fmt_ms(v):
    return "%.1f ms" % v if v < 100 else "%d ms" % round(v)


# (ayar anahtarı, başlık, biçim, logaritmik, adım)
KNOBS = (
    ("threshold", "Eşik", _fmt_db, False, 0.1),
    ("ratio", "Oran", _fmt_ratio, True, 0.1),
    ("knee", "Diz", lambda v: "%.1f dB" % v, False, 0.1),
    ("attack", "Atak", _fmt_ms, True, 0.05),
    ("release", "Bırakma", _fmt_ms, True, 1.0),
    ("ceiling", "Tavan", _fmt_db, False, 0.1),
)


class EqWindow(Adw.ApplicationWindow):
    def __init__(self, app):
        super().__init__(application=app, title="Ekolayzer")
        self.s = core.load_settings()
        self.set_default_size(1080 if self.s["limiter_panel"] else 840, 600)
        self._apply_src = 0
        self._apply_busy = False
        self._apply_dirty = False
        self._save_src = 0
        self._updating = False

        toolbar = Adw.ToolbarView()
        header = Adw.HeaderBar()
        toolbar.add_top_bar(header)

        self.power = Gtk.Switch(valign=Gtk.Align.CENTER, active=self.s["enabled"])
        self.power.set_tooltip_text("Ekolayzeri aç / kapat")
        self.power.connect("notify::active", self._on_power)
        header.pack_start(self.power)

        menu_btn = Gtk.MenuButton(icon_name="open-menu-symbolic", tooltip_text="Menü")
        menu_btn.set_popover(Gtk.PopoverMenu.new_from_model(self._build_menu()))
        header.pack_end(menu_btn)

        self.toast = Adw.ToastOverlay()
        toolbar.set_content(self.toast)
        self.set_content(toolbar)

        root = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12,
                       margin_top=12, margin_bottom=16, margin_start=16, margin_end=16)
        self.toast.set_child(root)

        # Ön ayar satırı
        row = Gtk.Box(spacing=8)
        row.append(Gtk.Label(label="Ön ayar:", css_classes=["dim-label"]))
        self.preset_model = Gtk.StringList()
        self.preset_dd = Gtk.DropDown(model=self.preset_model, hexpand=True)
        self.preset_dd.connect("notify::selected", self._on_preset_selected)
        row.append(self.preset_dd)
        save_btn = Gtk.Button(icon_name="document-save-symbolic", tooltip_text="Ön ayar olarak kaydet")
        save_btn.connect("clicked", self._on_save_preset)
        row.append(save_btn)
        self.del_btn = Gtk.Button(icon_name="user-trash-symbolic", tooltip_text="Özel ön ayarı sil")
        self.del_btn.connect("clicked", self._on_delete_preset)
        row.append(self.del_btn)
        reset_btn = Gtk.Button(icon_name="edit-undo-symbolic", tooltip_text="Düz ayara sıfırla")
        reset_btn.connect("clicked", lambda *_: self._select_preset("Düz"))
        row.append(reset_btn)
        root.append(row)

        # Frekans yanıtı
        frame = Gtk.Frame(css_classes=["view"])
        self.curve = ResponseView(lambda: self.s)
        frame.set_child(self.curve)
        root.append(frame)

        # Kaydırıcılar + çıkış göstergesi + limiter paneli
        sliders = Gtk.Box(spacing=4, vexpand=True)
        self.preamp_scale, self.preamp_lbl = self._make_slider(sliders, "Ön kazanç", self.s["preamp"], -1)
        sliders.append(Gtk.Separator(orientation=Gtk.Orientation.VERTICAL, margin_start=6, margin_end=6))
        self.band_scales, self.band_lbls = [], []
        for i, f in enumerate(core.BAND_FREQS):
            sc, lb = self._make_slider(sliders, freq_label(f) + " Hz", self.s["gains"][i], i)
            self.band_scales.append(sc)
            self.band_lbls.append(lb)
        sliders.append(Gtk.Separator(orientation=Gtk.Orientation.VERTICAL, margin_start=6, margin_end=6))

        mcol = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        self.peak_lbl = Gtk.Label(label="−∞", css_classes=["numeric", "caption"], width_chars=5)
        self.out_meter = wdg.Meter("level")
        mcol.append(self.peak_lbl)
        mcol.append(self.out_meter)
        mcol.append(Gtk.Label(label="Çıkış", css_classes=["caption-heading"]))
        sliders.append(mcol)

        self.panel_btn = Gtk.ToggleButton(active=self.s["limiter_panel"], valign=Gtk.Align.CENTER,
                                          css_classes=["flat", "circular"], margin_start=4)
        self.panel_btn.connect("toggled", self._on_panel_toggled)
        sliders.append(self.panel_btn)

        self.revealer = Gtk.Revealer(transition_type=Gtk.RevealerTransitionType.SLIDE_LEFT,
                                     transition_duration=180, reveal_child=self.s["limiter_panel"])
        self.revealer.set_child(self._build_limiter_panel())
        sliders.append(self.revealer)
        self._update_panel_btn()
        root.append(sliders)

        bottom = Gtk.Box(spacing=8)
        self.update_btn = Gtk.Button(visible=False, valign=Gtk.Align.CENTER,
                                     css_classes=["suggested-action", "pill"])
        self.update_btn.set_child(Adw.ButtonContent(icon_name="software-update-available-symbolic",
                                                    label="Güncelle"))
        self.update_btn.set_action_name("app.update")
        bottom.append(self.update_btn)
        self.status = Gtk.Label(xalign=0, css_classes=["dim-label", "caption"], wrap=True, hexpand=True)
        bottom.append(self.status)
        bottom.append(Gtk.Label(label="Limiter", css_classes=["caption-heading"]))
        self.limiter_sw = Gtk.Switch(valign=Gtk.Align.CENTER, active=self.s["limiter"]["enabled"])
        self.limiter_sw.set_tooltip_text("Limiter: tepe seviyelerini tavanın altında tutar, "
                                         "kırpılmayı (bozulmayı) önler.")
        self.limiter_sw.connect("notify::active", self._on_limiter_toggled)
        bottom.append(self.limiter_sw)
        root.append(bottom)

        # Ölçümler: ekran karesiyle senkron, yalnızca pencere görünürken çalışır
        self.levels = wdg.LevelSource()
        self._label_t = 0
        self._meter_t = 0
        self._peak_hold = (-120.0, 0)
        self._tick_id = self.out_meter.add_tick_callback(self._on_tick)
        self._quiet_since = None

        self._rebuild_presets(self.s.get("preset", CUSTOM))
        self._refresh_labels()
        self._ensure_service()

    # ------------------------------------------------------------ yapı

    def _build_menu(self):
        m = Gio.Menu()
        m.append("Güncellemeleri denetle", "app.check-update")
        m.append("Hakkında", "app.about")
        m.append("Çıkış", "app.quit")
        return m

    def _make_slider(self, box, title, value, idx):
        col = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, hexpand=True)
        val = Gtk.Label(css_classes=["numeric", "caption"])
        sc = Gtk.Scale(orientation=Gtk.Orientation.VERTICAL, inverted=True, vexpand=True,
                       draw_value=False, height_request=200)
        sc.set_range(core.GAIN_MIN, core.GAIN_MAX)
        sc.set_increments(0.5, 1.0)
        sc.set_has_origin(False)
        sc.set_value(value)
        sc.connect("value-changed", self._on_slider, idx)
        name = Gtk.Label(label=title, css_classes=["caption-heading"])
        col.append(val)
        col.append(sc)
        col.append(name)
        box.append(col)
        return sc, val

    def _build_limiter_panel(self):
        panel = Gtk.Box(spacing=10, margin_start=8, css_classes=["card"])
        inner = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6,
                        margin_top=8, margin_bottom=8, margin_start=10)
        inner.append(Gtk.Label(label="Limiter", xalign=0, css_classes=["heading"]))
        grid = Gtk.Grid(row_spacing=6, column_spacing=10, vexpand=True, valign=Gtk.Align.CENTER)
        self.knobs = {}
        for n, (key, title, fmt, log, step) in enumerate(KNOBS):
            _port, lo, hi, dflt = core.LIMITER_PARAMS[key]
            knob = wdg.Knob(title, lo, hi, self.s["limiter"][key], dflt, fmt, log=log, step=step,
                            on_change=lambda v, k=key: self._on_knob(k, v))
            grid.attach(knob, n % 2, n // 2, 1, 1)
            self.knobs[key] = knob
        inner.append(grid)
        panel.append(inner)

        gcol = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, margin_top=8, margin_bottom=8,
                       margin_end=10)
        self.gr_lbl = Gtk.Label(label="0.0", css_classes=["numeric", "caption"], width_chars=5)
        self.gr_meter = wdg.Meter("gr")
        gcol.append(self.gr_lbl)
        gcol.append(self.gr_meter)
        gcol.append(Gtk.Label(label="Azaltma", css_classes=["caption-heading"]))
        panel.append(gcol)
        return panel

    def _update_panel_btn(self):
        open_ = self.panel_btn.get_active()
        self.panel_btn.set_icon_name("pan-start-symbolic" if not open_ else "pan-end-symbolic")
        self.panel_btn.set_tooltip_text("Limiter ayarlarını gizle" if open_ else "Limiter ayarlarını göster")

    def _on_panel_toggled(self, btn):
        self.s["limiter_panel"] = btn.get_active()
        self.revealer.set_reveal_child(btn.get_active())
        self._update_panel_btn()
        if not btn.get_active():
            # paneli kapatınca pencere eski genişliğine dönsün
            GLib.timeout_add(200, lambda: (self.set_default_size(840, self.get_height()), False)[1])
        self._queue_save()

    # ------------------------------------------------------------ ön ayarlar

    def _all_presets(self):
        p = dict(core.PRESETS)
        p.update(self.s["custom_presets"])
        return p

    def _rebuild_presets(self, select):
        self._updating = True
        names = [CUSTOM] + list(core.PRESETS) + sorted(self.s["custom_presets"])
        self.preset_model.splice(0, self.preset_model.get_n_items(), names)
        self.preset_names = names
        self.preset_dd.set_selected(names.index(select) if select in names else 0)
        self._updating = False
        self._update_del_btn()

    def _update_del_btn(self):
        self.del_btn.set_sensitive(self.s.get("preset") in self.s["custom_presets"])

    def _select_preset(self, name):
        if name in self.preset_names:
            self.preset_dd.set_selected(self.preset_names.index(name))

    def _on_preset_selected(self, dd, _pspec):
        if self._updating:
            return
        name = self.preset_names[dd.get_selected()]
        self.s["preset"] = name
        self._update_del_btn()
        if name == CUSTOM:
            self._queue_save()
            return
        gains = self._all_presets()[name]
        self._updating = True
        for sc, g in zip(self.band_scales, gains):
            sc.set_value(g)
        self._updating = False
        self.s["gains"] = [float(g) for g in gains]
        self._changed()

    def _on_save_preset(self, *_):
        dlg = Adw.AlertDialog(heading="Ön ayarı kaydet", body="Geçerli ayar için bir ad girin.")
        entry = Gtk.Entry(placeholder_text="Ön ayar adı", activates_default=True)
        if self.s.get("preset") in self.s["custom_presets"]:
            entry.set_text(self.s["preset"])
        dlg.set_extra_child(entry)
        dlg.add_response("cancel", "Vazgeç")
        dlg.add_response("save", "Kaydet")
        dlg.set_response_appearance("save", Adw.ResponseAppearance.SUGGESTED)
        dlg.set_default_response("save")

        def on_resp(_d, resp):
            name = entry.get_text().strip()
            if resp != "save" or not name:
                return
            if name in core.PRESETS or name == CUSTOM:
                self.toast.add_toast(Adw.Toast(title="Bu ad hazır bir ön ayara ait, başka bir ad seçin."))
                return
            self.s["custom_presets"][name] = list(self.s["gains"])
            self.s["preset"] = name
            self._rebuild_presets(name)
            self._queue_save()
            self.toast.add_toast(Adw.Toast(title="“%s” kaydedildi" % name))

        dlg.connect("response", on_resp)
        dlg.present(self)

    def _on_delete_preset(self, *_):
        name = self.s.get("preset")
        if name in self.s["custom_presets"]:
            del self.s["custom_presets"][name]
            self.s["preset"] = CUSTOM
            self._rebuild_presets(CUSTOM)
            self._queue_save()
            self.toast.add_toast(Adw.Toast(title="“%s” silindi" % name))

    # ------------------------------------------------------------ değişiklikler

    def _on_slider(self, sc, idx):
        v = round(sc.get_value() * 2) / 2
        if idx < 0:
            if v == self.s["preamp"]:
                return
            self.s["preamp"] = v
        else:
            if v == self.s["gains"][idx]:
                return
            self.s["gains"][idx] = v
            if not self._updating and self.s.get("preset") != CUSTOM:
                preset = self._all_presets().get(self.s.get("preset"))
                if preset is None or [float(g) for g in preset] != self.s["gains"]:
                    self.s["preset"] = CUSTOM
                    self._updating = True
                    self.preset_dd.set_selected(0)
                    self._updating = False
                    self._update_del_btn()
        if not self._updating:
            self._changed()
        else:
            self._refresh_labels()

    def _on_knob(self, key, v):
        self.s["limiter"][key] = v
        self._changed(curve=False)

    def _on_limiter_toggled(self, sw, _pspec):
        self.s["limiter"]["enabled"] = sw.get_active()
        self.out_meter.reset_clip()
        self._changed(curve=False)

    def _changed(self, curve=True):
        self._refresh_labels()
        if curve:
            self.curve.queue_draw()
        if not self._apply_src:
            self._apply_src = GLib.timeout_add(30, self._do_apply)
        self._queue_save()

    def _refresh_labels(self):
        self.preamp_lbl.set_label("%+.1f" % self.s["preamp"])
        for lb, g in zip(self.band_lbls, self.s["gains"]):
            lb.set_label("%+.1f" % g)
        self._update_status()

    def _update_status(self, running=None):
        if not self.s["enabled"]:
            self.status.set_label("Ekolayzer kapalı – ses değiştirilmeden çalınıyor.")
            return False
        txt = "Etkin – tüm uygulamaların ses çıkışına uygulanıyor."
        if running is False:
            txt = "Ses motoru çalışmıyor; başlatılıyor…"
        lim = self.s["limiter"]
        if lim["enabled"]:
            txt += "  Limiter açık (tavan %s)." % _fmt_db(lim["ceiling"])
        else:
            peak = core.peak_gain(self.s)
            if peak > 0.5:
                txt += ("  Uyarı: en yüksek kazanç %+.1f dB; kırpılmayı önlemek için Limiter'i açın "
                        "veya ön kazancı düşürün." % peak)
        self.status.set_label(txt)
        return False

    # ------------------------------------------------------------ ölçüm

    def _on_tick(self, _widget, clock):
        now = clock.get_frame_time()
        if now - self._meter_t < 1_000_000 // METER_FPS - 2000:
            return GLib.SOURCE_CONTINUE
        self._meter_t = now
        # veri yoksa (ses yok / motor kapalı) göstergeler düşerek boşalır
        data = self.levels.poll() if self.s["enabled"] else None
        if data is None or max(data) < 1e-5:
            if self._quiet_since is None:
                self._quiet_since = now
            elif now - self._quiet_since > 2_000_000 and self._meters_empty():
                # sessizlikte kare döngüsünü durdur; 4 Hz yoklama ses gelince geri başlatır
                self._tick_id = 0
                GLib.timeout_add(250, self._wake_check)
                return GLib.SOURCE_REMOVE
        else:
            self._quiet_since = None
        self.out_meter.push(None if data is None else data[:2])
        if self.revealer.get_child_revealed():
            self.gr_meter.push(None if data is None else (data[2],))
        if data is not None:
            db = wdg.to_db(max(data[0], data[1]))
            hold_db, hold_t = self._peak_hold
            if db >= hold_db or now - hold_t > 1_000_000:
                self._peak_hold = (db, now)
        if now - self._label_t > 100_000:  # sayısal etiketler 10 Hz
            self._label_t = now
            hold_db, hold_t = self._peak_hold
            if now - hold_t > 1_500_000:
                hold_db = -120.0
            self.peak_lbl.set_label("%+.1f" % hold_db if hold_db > -60 else "−∞")
            if self.revealer.get_child_revealed():
                self.gr_lbl.set_label("%.1f" % self.gr_meter.holds[0])
        return GLib.SOURCE_CONTINUE

    def _meters_empty(self):
        gr_idle = (not self.revealer.get_child_revealed() or
                   max(self.gr_meter.vals + self.gr_meter.holds) < 0.05)
        return max(self.out_meter.vals + self.out_meter.holds) < 0.05 and gr_idle

    def _wake_check(self):
        if self._tick_id:
            return GLib.SOURCE_REMOVE
        data = self.levels.poll() if self.s["enabled"] else None
        if data is not None and max(data) >= 1e-5:
            self._quiet_since = None
            self._tick_id = self.out_meter.add_tick_callback(self._on_tick)
            return GLib.SOURCE_REMOVE
        return GLib.SOURCE_CONTINUE

    # ------------------------------------------------------------ uygulama / kaydetme

    def _do_apply(self):
        self._apply_src = 0
        if not self.s["enabled"]:
            return False
        if self._apply_busy:
            self._apply_dirty = True
            return False
        self._apply_busy = True
        snap = {"preamp": self.s["preamp"], "gains": list(self.s["gains"]),
                "limiter": dict(self.s["limiter"])}

        def work():
            core.apply_live(snap)
            GLib.idle_add(self._apply_done)

        threading.Thread(target=work, daemon=True).start()
        return False

    def _apply_done(self):
        self._apply_busy = False
        if self._apply_dirty:
            self._apply_dirty = False
            self._do_apply()
        return False

    def _queue_save(self):
        if self._save_src:
            GLib.source_remove(self._save_src)
        self._save_src = GLib.timeout_add(400, self._do_save)

    def _do_save(self):
        self._save_src = 0
        try:
            core.save_settings(self.s)
        except OSError as e:
            self.toast.add_toast(Adw.Toast(title="Ayarlar kaydedilemedi: %s" % e))
        return False

    def shutdown(self):
        self.levels.close()
        self.flush()

    def flush(self):
        if self._save_src:
            GLib.source_remove(self._save_src)
            self._do_save()

    # ------------------------------------------------------------ hizmet

    def _on_power(self, sw, _pspec):
        self.s["enabled"] = sw.get_active()
        self.flush()
        core.save_settings(self.s)
        self.curve.queue_draw()
        self._update_status()
        threading.Thread(target=core.systemctl, args=("restart", core.SERVICE), daemon=True).start()

    def _ensure_service(self):
        def work():
            if self.s["enabled"] and not core.service_active():
                GLib.idle_add(self._update_status, False)
                core.systemctl("start", core.SERVICE)
            GLib.idle_add(self._update_status)

        threading.Thread(target=work, daemon=True).start()


class UpdateFlow:
    """Güncelleme popup'ı: bildir → indir ve doğrula → polkit ile kur → sonuç."""

    def __init__(self, app, info):
        self.app, self.info = app, info
        self._pulse = 0

    def parent(self):
        w = self.app.win
        return w if w is not None and w.get_visible() else None

    def start(self):
        info = self.info
        lines = ["Ekolayzer <b>%s</b> yayımlandı. Yüklü sürüm: %s." % (
            GLib.markup_escape_text(info["version"]), GLib.markup_escape_text(info["current"]))]
        if info.get("summary"):
            lines.append("")
            lines.append("<b>Yenilikler</b>")
            lines += ["• " + GLib.markup_escape_text(x) for x in info["summary"]]
        dlg = Adw.AlertDialog(heading="Ekolayzer güncellemesi var", body="\n".join(lines),
                              body_use_markup=True)
        self.dont_show = Gtk.CheckButton(label="Tekrar gösterme", halign=Gtk.Align.CENTER)
        self.dont_show.set_tooltip_text("Bu sürüm için açılışta popup gösterilmez; "
                                        "güncelleme düğmesi pencerede görünmeye devam eder.")
        dlg.set_extra_child(self.dont_show)
        dlg.add_response("later", "Sonra")
        if info.get("asset_url"):
            dlg.add_response("update", "Güncelle")
            dlg.set_response_appearance("update", Adw.ResponseAppearance.SUGGESTED)
            dlg.set_default_response("update")
        else:
            dlg.add_response("page", "Sürüm sayfasını aç")
            dlg.set_default_response("page")
        dlg.set_close_response("later")
        dlg.connect("response", self._on_response)
        dlg.present(self.parent())

    def _on_response(self, _dlg, resp):
        if resp == "update":
            self._run_update()
            return
        if self.dont_show.get_active():
            self.app.skip_version(self.info["version"])
        if resp == "page":
            Gtk.UriLauncher.new(self.info["page"]).launch(self.parent(), None, None, None)
        self.app.update_finished()

    def run_direct(self):
        self._run_update()

    def _run_update(self):
        self.prog = Gtk.ProgressBar()
        self.pdlg = Adw.AlertDialog(heading="Ekolayzer güncelleniyor",
                                    body="Ekolayzer %s indiriliyor…" % self.info["version"],
                                    extra_child=self.prog)
        self.pdlg.set_can_close(False)
        self.pdlg.present(self.parent())
        threading.Thread(target=self._work, daemon=True).start()

    def _work(self):
        try:
            path = upd.download(self.info, progress=lambda f: GLib.idle_add(self.prog.set_fraction, f))
        except (upd.NetworkError, ValueError, OSError) as e:
            GLib.idle_add(self._done, False, "Paket indirilemedi: %s" % e)
            return
        GLib.idle_add(self._installing)
        ok, msg = upd.install(path)
        GLib.idle_add(self._done, ok, msg)

    def _installing(self):
        self.pdlg.set_body("Kuruluyor… Açılan sistem penceresinde parolanı gir.")
        self._pulse = GLib.timeout_add(120, lambda: (self.prog.pulse(), True)[1])
        return False

    def _done(self, ok, msg):
        if self._pulse:
            GLib.source_remove(self._pulse)
            self._pulse = 0
        self.pdlg.force_close()
        gui_open = self.parent() is not None
        if ok:
            self.app.set_update_info(None)
            body = "Ekolayzer %s kuruldu; ses motoru yeni sürümle çalışıyor." % self.info["version"]
            if gui_open:
                body += "\n\nYeni arayüzü kullanmak için uygulamayı yeniden başlat."
            dlg = Adw.AlertDialog(heading="Güncelleme tamamlandı", body=body)
            if gui_open:
                dlg.add_response("later", "Sonra")
                dlg.add_response("restart", "Yeniden başlat")
                dlg.set_response_appearance("restart", Adw.ResponseAppearance.SUGGESTED)
                dlg.set_default_response("restart")
                dlg.set_close_response("later")
            else:
                dlg.add_response("ok", "Tamam")
        else:
            dlg = Adw.AlertDialog(heading="Güncelleme kurulamadı", body=msg)
            dlg.add_response("ok", "Tamam")
            dlg.add_response("page", "Sürüm sayfasını aç")
            dlg.set_close_response("ok")
        dlg.connect("response", self._on_result)
        dlg.present(self.parent())
        return False

    def _on_result(self, _dlg, resp):
        if resp == "restart":
            self.app.restart()
            return
        if resp == "page":
            Gtk.UriLauncher.new(self.info["page"]).launch(self.parent(), None, None, None)
        self.app.update_finished()


class EqApp(Adw.Application):
    def __init__(self):
        super().__init__(application_id=core.APP_ID,
                         flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
        self.win = None
        self._update_busy = False
        self.update_info = None  # bilinen en yeni sürüm (sol alttaki düğme için)
        for name, cb in (("about", self._about), ("quit", lambda *_: self.quit()),
                         ("check-update", lambda *_: self.check_updates(manual=True)),
                         ("update", lambda *_: self.start_update())):
            act = Gio.SimpleAction.new(name, None)
            act.connect("activate", cb)
            self.add_action(act)
        self.set_accels_for_action("app.quit", ["<Control>q"])

    def do_command_line(self, cmdline):
        info = None
        for arg in cmdline.get_arguments()[1:]:
            if arg.startswith("--update-info="):
                try:
                    info = json.loads(arg.split("=", 1)[1])
                except ValueError:
                    info = None
        if info:
            # Arka plan denetimi yeni sürüm buldu: ana pencere açılmadan popup göster
            self.set_update_info(info)
            self.show_update(info)
        else:
            self.activate()
        return 0

    def do_activate(self):
        first = self.win is None
        if first:
            self.win = EqWindow(self)
            self.set_update_info(self.update_info)
        self.win.present()
        if first:
            self.check_updates()

    # ------------------------------------------------------------ güncelleme

    def check_updates(self, manual=False):
        if self._update_busy:
            return

        def work():
            try:
                info, err = upd.check(), None
            except upd.NetworkError as e:
                info, err = None, e
            GLib.idle_add(done, info, err)

        def done(info, err):
            if info:
                self.set_update_info(info)
                # "Tekrar gösterme" denmiş sürüm için açılışta popup yok; menüden denetimde her zaman göster
                if manual or self._skipped() != info["version"]:
                    self.show_update(info)
            elif manual and self.win:
                txt = ("Güncelleme sunucusuna ulaşılamadı." if err else
                       "Ekolayzer güncel (%s)." % VERSION)
                self.win.toast.add_toast(Adw.Toast(title=txt))
            return False

        threading.Thread(target=work, daemon=True).start()

    def show_update(self, info):
        if self._update_busy:
            return
        self._update_busy = True
        self.hold()  # ana pencere yokken de popup kapanana kadar uygulama açık kalsın
        UpdateFlow(self, info).start()

    def set_update_info(self, info):
        self.update_info = info
        if self.win:
            btn = self.win.update_btn
            btn.set_visible(info is not None)
            if info:
                btn.get_child().set_label("Güncelle (%s)" % info["version"])
                btn.set_tooltip_text("Ekolayzer %s mevcut (yüklü: %s). Tıklayınca indirilip kurulur."
                                     % (info["version"], info["current"]))

    def start_update(self):
        """Sol alttaki düğme: sormadan doğrudan indir ve kur."""
        if self._update_busy or not self.update_info:
            return
        if not self.update_info.get("asset_url"):
            Gtk.UriLauncher.new(self.update_info["page"]).launch(self.win, None, None, None)
            return
        self._update_busy = True
        self.hold()
        UpdateFlow(self, self.update_info).run_direct()

    def _skipped(self):
        if self.win:
            return self.win.s.get("update_skip", "")
        return core.load_settings().get("update_skip", "")

    def skip_version(self, version):
        if self.win:
            self.win.s["update_skip"] = version
            self.win._queue_save()
        else:
            s = core.load_settings()
            s["update_skip"] = version
            core.save_settings(s)

    def update_finished(self):
        if self._update_busy:
            self._update_busy = False
            self.release()

    def restart(self):
        if self.win:
            self.win.shutdown()
        os.execv(sys.executable, [sys.executable, os.path.abspath(sys.argv[0])])

    def do_shutdown(self):
        if self.win:
            self.win.shutdown()
        Adw.Application.do_shutdown(self)

    def _about(self, *_):
        about = Adw.AboutDialog(
            application_name="Ekolayzer",
            application_icon="ekolayzer",
            version=VERSION,
            developer_name="Gökdeniz G.",
            copyright="© 2026 Gökdeniz G.",
            website="https://github.com/%s" % upd.REPO,
            issue_url="https://github.com/%s/issues" % upd.REPO,
            comments="PipeWire tabanlı, sistem geneli 10 bantlı ekolayzer ve limiter.\n"
                     "Tüm uygulamaların ses çıkışına uygulanır.",
            license_type=Gtk.License.GPL_3_0,
        )
        about.present(self.win)


def main(argv=None):
    # Ana pencere olmadan açılan popup'lar da masaüstüne doğru uygulama kimliğini (ve simgeyi) bildirsin
    GLib.set_prgname(core.APP_ID)
    Gtk.Window.set_default_icon_name("ekolayzer")
    return EqApp().run(argv if argv is not None else sys.argv)
