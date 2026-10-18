"""Ekolayzer arayüz bileşenleri: paylaşılan bellek seviye okuyucu, seviye göstergeleri, döner düğme."""

import math
import mmap
import struct
import time

from gi.repository import Adw, Gdk, Gtk

import ekolayzer_core as core

LV_MAGIC = 0x4C564B45
LV_SLOTS = 64
_HDR = struct.Struct("<IIII")
_SLOT = struct.Struct("<4f")


def to_db(v):
    return 20.0 * math.log10(v) if v > 1e-6 else -120.0


class LevelSource:
    """Limiter eklentisinin paylaşılan bellekteki halka arabelleğini okur (ek süreç/akış yok)."""

    def __init__(self):
        self.mm = None
        self.last = None
        self.last_new = 0.0
        self.next_open = 0.0

    def _open(self):
        try:
            with open(core.levels_path(), "rb") as f:
                mm = mmap.mmap(f.fileno(), _HDR.size + LV_SLOTS * _SLOT.size, access=mmap.ACCESS_READ)
            if _HDR.unpack_from(mm, 0)[0] != LV_MAGIC:
                mm.close()
                return
            self.mm, self.last = mm, None
        except (OSError, ValueError):
            self.mm = None

    def close(self):
        if self.mm:
            self.mm.close()
        self.mm = None

    def poll(self):
        """Son okumadan bu yana gelen dilimlerin en büyükleri: (sol, sağ, kazanç azaltma dB) ya da None."""
        now = time.monotonic()
        if self.mm is None:
            if now >= self.next_open:
                self.next_open = now + 1.0
                self._open()
            if self.mm is None:
                return None
        count = _HDR.unpack_from(self.mm, 0)[2]
        if self.last is None:
            self.last, self.last_new = count, now
            return None
        n = (count - self.last) & 0xFFFFFFFF
        if n == 0:
            if now - self.last_new > 2.0:  # filtre yeniden başlamış olabilir: dosyayı yeniden aç
                self.close()
                self.last_new = now
            return None
        n = min(n, LV_SLOTS)
        l = r = gr = 0.0
        base = _HDR.size
        for k in range(count - n, count):
            sl, sr, _sin, sg = _SLOT.unpack_from(self.mm, base + (k % LV_SLOTS) * _SLOT.size)
            l, r, gr = max(l, sl), max(r, sr), max(gr, sg)
        self.last, self.last_new = count, now
        return l, r, gr


class Meter(Gtk.DrawingArea):
    """Dikey tepe göstergesi.

    mode="level": stereo çıkış seviyesi (0 .. -60 dBFS), yeşil→sarı→kırmızı, kırpılma ışığı.
    mode="gr":    kazanç azaltma (0 .. 24 dB), yukarıdan aşağı dolar.
    """

    CLIP_H = 8
    BAR_W = 9
    GAP = 3
    FALL_DB_S = 48.0   # hızlı düşüş
    HOLD_S = 1.0

    def __init__(self, mode="level"):
        super().__init__()
        self.mode = mode
        self.bars = 2 if mode == "level" else 1
        self.span = 60.0 if mode == "level" else 24.0
        self.marks = (0, 6, 12, 24, 36, 48, 60) if mode == "level" else (0, 3, 6, 9, 12, 18, 24)
        self.set_content_width(self.bars * self.BAR_W + (self.bars - 1) * self.GAP + 24)
        self.set_vexpand(True)
        self.vals = [0.0] * self.bars    # gösterilen değer (span içinde, 0 = boş)
        self.holds = [0.0] * self.bars
        self.hold_t = [0.0] * self.bars
        self.clipped = False
        self.last_t = time.monotonic()
        self.set_draw_func(self._draw)
        if mode == "level":
            self.set_tooltip_text("Çıkış seviyesi (dBFS). Kırmızı ışık kırpılma/0 dBFS'ye ulaşıldığını "
                                  "gösterir; sıfırlamak için tıklayın.")
            click = Gtk.GestureClick()
            click.connect("pressed", lambda *_: self.reset_clip())
            self.add_controller(click)
        else:
            self.set_tooltip_text("Limiterin uyguladığı kazanç azaltma (dB)")

    def reset_clip(self):
        self.clipped = False
        self.queue_draw()

    def push(self, values):
        """values: level modunda doğrusal tepe (sol, sağ); gr modunda (dB,). None = veri yok."""
        now = time.monotonic()
        dt = min(now - self.last_t, 0.1)
        self.last_t = now
        dirty = False
        was_clipped = self.clipped
        for i in range(self.bars):
            if values is None:
                v = 0.0
            elif self.mode == "level":
                p = values[i]
                if p >= 0.9999:
                    self.clipped = True
                v = max(0.0, self.span + max(to_db(p), -self.span))
            else:
                v = min(max(values[0], 0.0), self.span)
            cur = self.vals[i]
            new = v if v >= cur else max(v, cur - self.FALL_DB_S * dt)
            hold = self.holds[i]
            if v >= hold or now - self.hold_t[i] > self.HOLD_S:
                self.holds[i], self.hold_t[i] = v, now
            # yalnızca görünür bir değişiklik varsa yeniden çiz (sessizlikte hiç çizim yok)
            if abs(new - cur) > 0.05 or abs(self.holds[i] - hold) > 0.05:
                dirty = True
            self.vals[i] = new
        if dirty or self.clipped != was_clipped:
            self.queue_draw()

    def _draw(self, _area, cr, w, h):
        fg = self.get_color()
        top = self.CLIP_H + 4 if self.mode == "level" else 7
        bh = h - top - 6
        bars_w = self.bars * self.BAR_W + (self.bars - 1) * self.GAP

        def ypos(depth):  # depth: üstten dB uzaklığı
            return top + min(max(depth, 0.0), self.span) / self.span * bh

        cr.set_font_size(9)
        for mk in self.marks:
            y = ypos(mk)
            txt = str(mk)
            ext = cr.text_extents(txt)
            cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.6)
            cr.move_to(w - ext.width - 1, y + ext.height / 2)
            cr.show_text(txt)
            cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.25)
            cr.rectangle(bars_w + 1, round(y), 4, 1)
            cr.fill()

        for i in range(self.bars):
            x = i * (self.BAR_W + self.GAP)
            cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.10)
            cr.rectangle(x, top, self.BAR_W, bh)
            cr.fill()
            v = self.vals[i]
            if self.mode == "level":
                # v: tabandan yükseklik (dB); renk bölgeleri üstten derinliğe göre
                for d_lo, d_hi, rgb in ((12.0, 60.0, (0.20, 0.82, 0.32)),
                                        (3.0, 12.0, (0.96, 0.83, 0.18)),
                                        (0.0, 3.0, (0.88, 0.11, 0.14))):
                    depth = self.span - v
                    if depth >= d_hi:
                        continue
                    y1 = ypos(max(depth, d_lo))
                    y2 = ypos(d_hi)
                    cr.set_source_rgb(*rgb)
                    cr.rectangle(x, y1, self.BAR_W, y2 - y1)
                    cr.fill()
                hold_depth = self.span - self.holds[i]
            else:
                # kazanç azaltma: üstten aşağı turuncu→kırmızı
                for d_lo, d_hi, rgb in ((0.0, 6.0, (0.97, 0.60, 0.15)),
                                        (6.0, 24.0, (0.88, 0.25, 0.14))):
                    if v <= d_lo:
                        continue
                    y1, y2 = ypos(d_lo), ypos(min(v, d_hi))
                    cr.set_source_rgb(*rgb)
                    cr.rectangle(x, y1, self.BAR_W, y2 - y1)
                    cr.fill()
                hold_depth = self.holds[i]
            if (self.mode == "level" and self.holds[i] > 0.5) or (self.mode == "gr" and self.holds[i] > 0.1):
                yh = ypos(hold_depth)
                cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.85)
                cr.rectangle(x, yh - 1, self.BAR_W, 2)
                cr.fill()

        if self.mode == "level":
            if self.clipped:
                cr.set_source_rgb(0.88, 0.11, 0.14)
            else:
                cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.15)
            cr.rectangle(0, 0, bars_w, self.CLIP_H)
            cr.fill()


class Knob(Gtk.Box):
    """Döner düğme: dikey sürükleme (Shift ile ince ayar), tekerlek, çift tıkla varsayılana dön."""

    SIZE = 46

    def __init__(self, title, lo, hi, value, default, fmt, log=False, step=None, on_change=None):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=2, halign=Gtk.Align.CENTER)
        self.lo, self.hi, self.log, self.default = lo, hi, log, default
        self.fmt, self.step, self.on_change = fmt, step, on_change
        self.value = value
        self.append(Gtk.Label(label=title, css_classes=["caption-heading"]))
        self.area = Gtk.DrawingArea(content_width=self.SIZE, content_height=self.SIZE,
                                    halign=Gtk.Align.CENTER, focusable=True)
        self.area.set_draw_func(self._draw)
        self.area.set_cursor(Gdk.Cursor.new_from_name("ns-resize"))
        self.area.set_tooltip_text("%s — sürükleyin veya tekerleği kullanın (Shift: ince), "
                                   "çift tıklama: varsayılan" % title)
        self.append(self.area)
        self.lbl = Gtk.Label(css_classes=["numeric", "caption"])
        self.append(self.lbl)

        drag = Gtk.GestureDrag()
        drag.connect("drag-begin", self._drag_begin)
        drag.connect("drag-update", self._drag_update)
        self.area.add_controller(drag)
        scroll = Gtk.EventControllerScroll.new(Gtk.EventControllerScrollFlags.VERTICAL)
        scroll.connect("scroll", self._scroll)
        self.area.add_controller(scroll)
        click = Gtk.GestureClick()
        click.connect("pressed", self._click)
        self.area.add_controller(click)
        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", self._key)
        self.area.add_controller(keys)
        self._start = 0.0
        self._update_label()

    # değer <-> 0..1
    def _to_norm(self, v):
        if self.log:
            return math.log(v / self.lo) / math.log(self.hi / self.lo)
        return (v - self.lo) / (self.hi - self.lo)

    def _from_norm(self, n):
        n = min(max(n, 0.0), 1.0)
        v = self.lo * (self.hi / self.lo) ** n if self.log else self.lo + n * (self.hi - self.lo)
        if self.step:
            v = round(v / self.step) * self.step
        return min(max(v, self.lo), self.hi)

    def set_value(self, v, notify=True):
        v = min(max(v, self.lo), self.hi)
        if abs(v - self.value) < 1e-9:
            return
        self.value = v
        self._update_label()
        self.area.queue_draw()
        if notify and self.on_change:
            self.on_change(v)

    def _update_label(self):
        self.lbl.set_label(self.fmt(self.value))

    def _drag_begin(self, _g, _x, _y):
        self._start = self._to_norm(self.value)
        self.area.grab_focus()

    def _drag_update(self, g, _dx, dy):
        fine = g.get_current_event_state() & Gdk.ModifierType.SHIFT_MASK
        self.set_value(self._from_norm(self._start - dy / (800.0 if fine else 160.0)))

    def _nudge(self, steps, fine=False):
        v = self._from_norm(self._to_norm(self.value) + steps * (0.005 if fine else 0.025))
        if self.step and abs(v - self.value) < 1e-9 and steps:
            v = self.value + (self.step if steps > 0 else -self.step)  # yuvarlamada takılma
        self.set_value(round(v / self.step) * self.step if self.step else v)

    def _scroll(self, ctl, _dx, dy):
        fine = ctl.get_current_event_state() & Gdk.ModifierType.SHIFT_MASK
        self._nudge(-dy, fine)
        return True

    def _click(self, _g, n_press, _x, _y):
        if n_press == 2:
            self.set_value(self.default)

    def _key(self, _c, keyval, _code, state):
        fine = bool(state & Gdk.ModifierType.SHIFT_MASK)
        if keyval in (Gdk.KEY_Up, Gdk.KEY_Right):
            self._nudge(1, fine)
        elif keyval in (Gdk.KEY_Down, Gdk.KEY_Left):
            self._nudge(-1, fine)
        elif keyval in (Gdk.KEY_Home, Gdk.KEY_BackSpace, Gdk.KEY_Delete):
            self.set_value(self.default)
        else:
            return False
        return True

    def _draw(self, area, cr, w, h):
        fg = self.get_color()
        accent = Adw.StyleManager.get_default().get_accent_color_rgba()
        if not self.get_sensitive():
            accent = fg
        cx, cy = w / 2, h / 2
        rad = min(w, h) / 2 - 4
        a0, a1 = math.radians(135), math.radians(405)
        a = a0 + (a1 - a0) * self._to_norm(self.value)
        cr.set_line_width(4)
        cr.set_line_cap(1)  # yuvarlak uç
        cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.15)
        cr.arc(cx, cy, rad, a0, a1)
        cr.stroke()
        cr.set_source_rgba(accent.red, accent.green, accent.blue, 1.0 if self.get_sensitive() else 0.4)
        cr.arc(cx, cy, rad, a0, a)
        cr.stroke()
        cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.08)
        cr.arc(cx, cy, rad - 6, 0, 2 * math.pi)
        cr.fill()
        cr.set_line_width(2.5)
        cr.set_source_rgba(fg.red, fg.green, fg.blue, 0.85)
        cr.move_to(cx + math.cos(a) * (rad - 14), cy + math.sin(a) * (rad - 14))
        cr.line_to(cx + math.cos(a) * (rad - 6), cy + math.sin(a) * (rad - 6))
        cr.stroke()
        if area.has_focus():
            cr.set_line_width(1)
            cr.set_source_rgba(accent.red, accent.green, accent.blue, 0.6)
            cr.arc(cx, cy, rad + 2.5, 0, 2 * math.pi)
            cr.stroke()
