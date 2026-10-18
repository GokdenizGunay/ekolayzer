#!/bin/sh
# Kullanım: ./build-deb.sh  ->  ekolayzer_<sürüm>_<mimari>.deb üretir
# Gerekenler: gcc, libc6-dev, dpkg-deb (yalnızca derleme için; kurulumda gerekmez)
set -e
cd "$(dirname "$0")"
VERSION=$(sed -n 's/^VERSION = "\(.*\)"/\1/p' src/ekolayzer_gui.py)
ARCH=$(dpkg --print-architecture)
PKG=build/ekolayzer
rm -rf build && mkdir -p "$PKG/DEBIAN" && chmod 755 "$PKG"

# Limiter/ölçer eklentisi (LADSPA)
${CC:-gcc} -O2 -fPIC -shared -fvisibility=hidden -fno-math-errno -Wall -Wextra \
    -Wl,-z,relro,-z,now -Wl,--as-needed -s \
    -o build/ekolayzer_dyn.so src/dyn/ekolayzer_dyn.c -lm

install -Dm755 src/ekolayzer               "$PKG/usr/bin/ekolayzer"
install -Dm644 src/ekolayzer_core.py       "$PKG/usr/lib/ekolayzer/ekolayzer_core.py"
install -Dm644 src/ekolayzer_gui.py        "$PKG/usr/lib/ekolayzer/ekolayzer_gui.py"
install -Dm644 src/ekolayzer_widgets.py    "$PKG/usr/lib/ekolayzer/ekolayzer_widgets.py"
install -Dm644 build/ekolayzer_dyn.so      "$PKG/usr/lib/ekolayzer/ekolayzer_dyn.so"
install -Dm755 src/ekolayzer-daemon        "$PKG/usr/lib/ekolayzer/ekolayzer-daemon"
install -Dm755 src/ekolayzer-shortcut      "$PKG/usr/lib/ekolayzer/ekolayzer-shortcut"
install -Dm644 data/ekolayzer-shortcut.desktop "$PKG/etc/xdg/autostart/ekolayzer-shortcut.desktop"
install -Dm644 data/ekolayzer.service      "$PKG/usr/lib/systemd/user/ekolayzer.service"
install -Dm644 data/io.github.ekolayzer.desktop "$PKG/usr/share/applications/io.github.ekolayzer.desktop"
install -Dm644 data/ekolayzer.svg          "$PKG/usr/share/icons/hicolor/scalable/apps/ekolayzer.svg"
install -Dm644 debian/copyright            "$PKG/usr/share/doc/ekolayzer/copyright"

for f in postinst prerm postrm; do install -m755 "debian/$f" "$PKG/DEBIAN/$f"; done
SIZE=$(du -sk --exclude=DEBIAN "$PKG" | cut -f1)
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE@/$SIZE/" -e "s/@ARCH@/$ARCH/" debian/control > "$PKG/DEBIAN/control"
(cd "$PKG" && find etc -type f | sed "s,^,/," > DEBIAN/conffiles && find usr -type f -exec md5sum {} + > DEBIAN/md5sums)

dpkg-deb --root-owner-group -Zxz --build "$PKG" "ekolayzer_${VERSION}_${ARCH}.deb"
echo "Oluşturuldu: ekolayzer_${VERSION}_${ARCH}.deb"
