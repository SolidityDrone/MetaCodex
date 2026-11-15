# Debian packages beyond the official layer: the base step (in the APK's image) and `mcx-setup quest` (on request)

The 54 packages of the base step are installed in the ready Linux image by `tools/userland-image.sh`, so they ship in
the APK (see [LICENSES.md](../LICENSES.md), section 3.6). The 144 packages of the quest step are downloaded by the
user's own `apt` on the device only when `mcx-setup quest` runs.

Measured on 2026-11-12 by simulating `apt-get install --no-install-recommends` with apt on the image's own dpkg status (`/var/lib/dpkg/status` of `assets/linux/rootfs.tar`) against Debian trixie (13.7) arm64 main: **54 packages** for the base step (`ca-certificates curl git xz-utils python3 procps less nano`) and **144 more** for `mcx-setup quest` (`aapt zipalign apksigner default-jdk-headless unzip`), 198 in all, from 119 source packages. Each installed package carries its licence in `/usr/share/doc/<package>/copyright`, and its source is available from the same archive (`apt-get source`). The licences below are the `License:` fields of each source package's `debian/copyright` (read from sources.debian.org); where that file is free text rather than machine-readable, the licence families named in the text are listed (found by a keyword search, so read them as a hint and open the file).

| Source package | Version | Binary packages | Step | Licences (from `debian/copyright`) |
|---|---|---|---|---|
| 7zip | 25.01+dfsg-1~deb13u2 | 7zip | quest | LGPL-2.1+, public-domain, BSD-3-clause |
| adwaita-icon-theme | 48.1-1 | adwaita-icon-theme | quest | free text; mentions GPL-3, GPL-2, LGPL, public-domain, Artistic, CC0 |
| alsa-lib | 1.2.14-1+deb13u1 | libasound2-data libasound2t64 | quest | LGPL-2.1+ |
| android-platform-build | 1:10.0.0+r36-1.1 | zipalign | quest | Apache-2.0 |
| android-platform-frameworks-base | 1:14~beta1-3 | aapt android-libaapt android-libandroidfw | quest | Apache-2.0, BSD-2-clause, BSD-3-clause, BSD-Berkeley |
| android-platform-tools | 34.0.5-12 | android-libbacktrace android-libbase android-libcutils android-liblog android-libutils android-libziparchive | quest | Apache-2.0, Expat, BSD-3-clause, BSD-2-clause, Expat-with-optional-exception, PSF, public-domain … |
| android-platform-tools-apksig | 35.0.2-1 | apksigner libapksig-java | quest | Apache-2.0 |
| apparmor | 4.1.0-1 | libapparmor1 | quest | GPL-2+, BSD-3-clause or GPL-2+, LGPL-2.1+, GPL-2, BSD-3-clause |
| at-spi2-core | 2.56.2-1+deb13u2 | at-spi2-common libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 | quest | LGPL-2+, LGPL-2.1+, AFL-2.1 or GPL-2+, GPL-2, public-domain, AFL-2.1, GPL-2+ |
| avahi | 0.8-16 | libavahi-client3 libavahi-common-data libavahi-common3 | quest | free text; mentions GPL-2, LGPL, BSD |
| brotli | 1.1.0-2 | libbrotli1 | base | MIT |
| ca-certificates | 20250419 | ca-certificates | base | GPL-2+, MPL-2.0 |
| ca-certificates-java | 20240118 | ca-certificates-java | quest | free text, see the file |
| cairo | 1.18.4-1 | libcairo-gobject2 libcairo2 | quest | free text; mentions GPL-2, LGPL, MPL |
| colord | 1.4.7-3 | libcolord2 | quest | GPL-2+, LGPL-2.1+, GFDL-NIV, CC0-1.0, CC0-1.0 and NPES, NPES |
| cups | 2.4.10-3+deb13u2 | libcups2t64 | quest | Apache-2.0-with-GPL2-LGPL2-Exception, FSFUL, Zlib, Apache-2.0, Apache-2.0-with-GPL2-LGPL2-Exception or BSD-2-clause, BSD-2-Clause |
| curl | 8.14.1-2+deb13u5 | curl libcurl3t64-gnutls libcurl4t64 | base | curl, OLDAP-2.8, ISC, GPL-2+ with Autoconf-data exception, GPL-3+ with Autoconf-data exception, GPL-2+ with Libtool exception, BSD-3-clause … |
| cyrus-sasl2 | 2.1.28+dfsg1-9 | libsasl2-2 libsasl2-modules-db | base | BSD-3-Clause-Attribution, BSD-3-clause, BSD-2-clause, GPL-3+, GPL-3, BSD-4-clause-UC, RSA-MD … |
| dbus | 1.16.2-2 | dbus dbus-bin dbus-daemon dbus-session-bus-common dbus-system-bus-common dbus-user-session libdbus-1-3 | quest | GPL-2+ or AFL-2.1, and Expat and Tcl-BSDish, GPL-2+, BSD-3-clause and BSD-3-clause-generic, LGPL-2.1+, g10-permissive, Expat, Tcl-BSDish … |
| dconf | 0.40.0-5 | dconf-gsettings-backend dconf-service libdconf1 | quest | LGPL-2+, GPL-3 |
| e2fsprogs | 1.47.2-3 | libcom-err2 | base | GPL-2, GPL-2+, 0BSD, Expat, BSD-3-Clause-Variant, BSD-3-Clause, BSD-4-Clause-CMU … |
| elfutils | 0.192-4 | libelf1t64 | quest | GPL-3+, LGPL-3+ or GPL-2+, BSD-2-clause, GFDL-NIV-1.3, LGPL-2.1+, GPL-3+ with Bison exception |
| expat | 2.8.3-1~deb13u1 | libexpat1 | base | MIT |
| fontconfig | 2.15.0-2.3 | fontconfig fontconfig-config libfontconfig1 | quest | free text, see the file |
| fonts-dejavu | 2.37-8 | fonts-dejavu-core fonts-dejavu-mono | quest | bitstream-vera, GPL-2+ |
| freetype | 2.13.3+dfsg-1+deb13u1 | libfreetype6 | quest | FTL, FTL and Expat, BSD-3-Clause, FSFAP, GPL-3+, Expat, GPL-2+ … |
| fribidi | 1.0.16-1 | libfribidi0 | quest | LGPL-2.1+ |
| gcc-14 | 14.2.0-19 | libatomic1 | quest | free text; mentions GPL-3, GPL-2, LGPL, BSD, MIT/X11, zlib, public-domain, Artistic |
| gdbm | 1.24-2 | libgdbm-compat4t64 libgdbm6t64 | base | GPL-3+, GPL-2+, GFDL-NIV-1.3+ |
| gdk-pixbuf | 2.42.12+dfsg-4+deb13u1 | libgdk-pixbuf-2.0-0 libgdk-pixbuf2.0-common | quest | LGPL-2+ and LGPL-2.1+ and CC0-1.0, GPL-2+, CC0-1.0, LGPL-2+, LGPL-2.1+ |
| giflib | 5.2.2-1+deb13u1 | libgif7 | quest | MIT, ISC |
| git | 1:2.47.3-0+deb13u1 | git git-man | base | GPL-2, BSD-3-clause, Zlib, LGPL-2.1+, EDL-1.0, GPL-2+, Expat … |
| glib2.0 | 2.84.4-3~deb13u5 | libglib2.0-0t64 | quest | LGPL-2.1+, old-glib-tests, AFL-2.0 or LGPL-2.1+, Janik-permissive, LGPL-2+, CC0-1.0, FSFULLR … |
| gnutls28 | 3.8.9-3+deb13u4 | libgnutls30t64 | base | free text; mentions GPL-3, GPL-2, LGPL, Apache, BSD, MIT/X11, public-domain, CC0 |
| graphite2 | 1.3.14-2+deb13u1 | libgraphite2-3 | quest | LGPL-2.1+ or MPL-1.1 or GPL-2+, LGPL-2.1+, public-domain, Artistic or GPL-1+, LGPL-2.1+ or GPL-2+ or MPL-1.1, custom-sil-open-font-license, Artistic … |
| gtk+3.0 | 3.24.49-3 | libgtk-3-0t64 libgtk-3-common | quest | LGPL-2+ and LGPL-2.1+ and Expat, GPL-3+, LGPL-2+, CC-BY-SA-4.0, unencumbered, other, SWL … |
| gtk4 | 4.18.6+ds-2 | gtk-update-icon-cache | quest | LGPL-2.1+, LGPL-2+ and LGPL-2.1+ and sun-permissive and lcs-telegraphics-permissive and X11R5-permissive and Expat and BSD-3-clause-Google and Apache-2.0 and CC0-1.0 and ZPL-2.1, Unicode-DFS-2016, GPL-3+, Apache-2.0 with LLVM exception, CC0-1.0, Apache-2.0 … |
| harfbuzz | 10.2.0-1+deb13u1 | libharfbuzz0b | quest | MIT, Unicode, ISC, Apache-2.0, OFL-1.1, Monotype, CC0-1.0 … |
| hicolor-icon-theme | 0.18-2 | hicolor-icon-theme | quest | GPL-2+ |
| java-common | 0.76 | default-jdk-headless default-jre default-jre-headless java-common | quest | GPL-2+ |
| jbigkit | 2.1-6.1 | libjbig0 | quest | GPL-2+ |
| keyutils | 1.6.3-6 | libkeyutils1 | base | GPL-2+, LGPL-2+ |
| krb5 | 1.21.3-5+deb13u1 | libgssapi-krb5-2 libk5crypto3 libkrb5-3 libkrb5support0 | base | free text; mentions GPL-2, BSD, MIT/X11 |
| lcms2 | 2.16-2+deb13u2 | liblcms2-2 | quest | MIT, GPL-3, IJG, GPL-2+ |
| lerc | 4.0.0+ds-5 | liblerc4 | quest | Apache-2.0 |
| less | 668-1 | less | base | GPL-3+ or Less, GPL-3+, Less, GPL-3+ or Less, and X11, X11, Spencer-86, public-domain |
| libcloudproviders | 0.3.6-2 | libcloudproviders0 | quest | LGPL-3+ |
| libdatrie | 0.2.13-3 | libdatrie1 | quest | LGPL-2.1+, GPL-2+ |
| libdeflate | 1.23-2 | libdeflate0 | quest | Expat |
| libdrm | 2.4.124-2 | libdrm-amdgpu1 libdrm-common libdrm2 | quest | free text; mentions MIT/X11 |
| libedit | 3.1-20250104-1 | libedit2 | quest | BSD-3-clause |
| libepoxy | 1.5.10-2 | libepoxy0 | quest | Expat |
| liberror-perl | 0.17030-1 | liberror-perl | base | Artistic or GPL-1+, MIT/X11, Artistic, GPL-1+ |
| libffi | 3.4.8-2 | libffi8 | base | Expat, X11, GPL-2+, GPL-3+, MPL-1.1 or GPL-2+ or LGPL-2.1+, public-domain, GPL |
| libglvnd | 1.7.0-1 | libgl1 libglvnd0 libglx0 | quest | MIT, Apache-2.0, public-domain, GPL-3+, BSD-1-clause, GPL |
| libidn2 | 2.3.8-2 | libidn2-0 | base | GPL-3+, LGPL-3+ or GPL-2+, Expat, Unicode, GPL-2+, LGPL-3+ |
| libjpeg-turbo | 1:2.1.5-4 | libjpeg62-turbo | quest | BSD-BY-LC-NE, Zlib, BSD-3-clause, Expat, NTP, zlib |
| libpng1.6 | 1.6.48-1+deb13u5 | libpng16-16t64 | quest | libpng, expat, GPL-2+ or BSD-like-with-advertising-clause, libpng OR Apache-2.0 OR BSD-3-clause, Apache-2.0, GPL-2+, BSD-like-with-advertising-clause … |
| libpsl | 0.21.2-1.1 | libpsl5t64 | base | MIT, gnulib, Chromium |
| libssh2 | 1.11.1-1+deb13u2 | libssh2-1t64 | base | BSD3, ISC |
| libtasn1-6 | 4.20.0-2+deb13u1 | libtasn1-6 | base | free text; mentions GPL-3, GPL-2, LGPL |
| libthai | 0.1.29-2 | libthai-data libthai0 | quest | LGPL-2.1+, GPL-2+ |
| libunistring | 1.3-2 | libunistring5 | base | GPL-3+, LGPL-3+, LGPL-2+, GFDL-1.3+, GFDL-1.2+ or GPL-3+ or LGPL-3, LGPL-2.1+, LGPL-3 … |
| libwebp | 1.5.0-0.1 | libsharpyuv0 libwebp7 | quest | BSD-3-Clause, Apache-2.0 |
| libx11 | 2:1.8.12-1 | libx11-6 libx11-data libx11-xcb1 | quest | free text; mentions BSD, MIT/X11 |
| libxau | 1:1.0.11-1 | libxau6 | quest | free text, see the file |
| libxcb | 1.17.0-2 | libxcb-dri3-0 libxcb-glx0 libxcb-present0 libxcb-randr0 libxcb-render0 libxcb-shm0 libxcb-sync1 libxcb-xfixes0 libxcb1 | quest | free text; mentions MIT/X11 |
| libxcomposite | 1:0.4.6-1 | libxcomposite1 | quest | free text; mentions MIT/X11 |
| libxcursor | 1:1.2.3-1 | libxcursor1 | quest | free text, see the file |
| libxdamage | 1:1.1.6-1 | libxdamage1 | quest | free text, see the file |
| libxdmcp | 1:1.1.5-1 | libxdmcp6 | quest | free text, see the file |
| libxext | 2:1.3.4-1 | libxext6 | quest | free text; mentions MIT/X11 |
| libxfixes | 1:6.0.0-2 | libxfixes3 | quest | free text; mentions MIT/X11 |
| libxi | 2:1.8.2-1 | libxi6 | quest | free text; mentions MIT/X11 |
| libxinerama | 2:1.1.4-3 | libxinerama1 | quest | free text; mentions MIT/X11 |
| libxkbcommon | 1.7.0-2 | libxkbcommon0 | quest | free text; mentions MIT/X11 |
| libxml2 | 2.12.7+dfsg+really2.9.14-2.1+deb13u3 | libxml2 | quest | MIT-1, ISC |
| libxrandr | 2:1.5.4-1 | libxrandr2 | quest | free text, see the file |
| libxrender | 1:0.9.12-1 | libxrender1 | quest | free text, see the file |
| libxshmfence | 1.3.3-1 | libxshmfence1 | quest | free text, see the file |
| libxtst | 2:1.2.5-1 | libxtst6 | quest | free text; mentions MIT/X11 |
| libxxf86vm | 1:1.1.4-1 | libxxf86vm1 | quest | free text; mentions MIT/X11 |
| llvm-toolchain-19 | 1:19.1.7-3 | libllvm19 | quest | APACHE-2-LLVM-EXCEPTIONS, MIT, BSD-3-clause, solar-public-domain, Python, BSD-3-Clause |
| lm-sensors | 1:3.6.2-2 | libsensors-config libsensors5 | quest | GPL-2+, LGPL-2.1+, GPL-2, Linux-man-pages-copyleft |
| media-types | 13.0.0 | media-types | base | ad-hoc |
| mesa | 25.0.7-2+deb13u1 | libgbm1 libgl1-mesa-dri libglx-mesa0 mesa-libgallium | quest | MIT, GPL-2, GPL-2 or MIT, GPL-1+, BSD-3-google, Khronos, Apache-2.0 … |
| nano | 8.4-1+deb13u1 | nano | base | GPL-3+, GFDL-NIV+ or GPL-3+, GFDL-NIV+ |
| ncurses | 6.5+20250216-2 | libncursesw6 | base | MIT/X11, X11, BSD-3-clause |
| netbase | 6.5 | netbase | base | GPL-2 |
| nghttp2 | 1.64.0-1.1+deb13u1 | libnghttp2-14 | base | Expat, all-permissive, GPL-3+ with autoconf exception, MIT, BSD-2-clause |
| nghttp3 | 1.8.0-1 | libnghttp3-9 | base | MIT, FSFULLR, GPL-3+ with Autoconf generic exception, FSFUL, GPL-2+ with Autoconf generic exception, FSFULLR and MIT, FSFAP … |
| ngtcp2 | 1.11.0-1+deb13u1 | libngtcp2-16 libngtcp2-crypto-gnutls8 | base | MIT, FSFULLR, GPL-3+ with Autoconf generic exception, FSFUL, GPL-2+ with Autoconf generic exception, FSFAP, GPL-2+ with Libtool Exception … |
| nspr | 2:4.36-1 | libnspr4 | quest | MPL-2.0 |
| nss | 2:3.110-1+deb13u4 | libnss3 | quest | MPL-2.0, Zlib, BSD-3, public-domain |
| openjdk-21 | 21.0.12.1+1-1~deb13u1 | openjdk-21-jdk-headless openjdk-21-jre openjdk-21-jre-headless | quest | GPL2 with Classpath exception, BSD-3-Clause, MIT, LGPL-2.1 |
| openldap | 2.6.10+dfsg-1 | libldap2 | base | OpenLDAP-2.8, FSF-unlimited and GPL-2+ with Autoconf exception, GPL-3+ with Autoconf exception, FSF-unlimited, GPL-2+ with Libtool exception and GPL-3+ with Libtool exception and GPL-3+, GPL-2+ with Autoconf exception, GPL-2+ … |
| openssl | 3.5.7-1~deb13u2 | openssl | base | Apache-2.0, Artistic or GPL-1+, Artistic, GPL-1+ |
| p11-kit | 0.25.5-3 | libp11-kit0 | base | BSD-3-clause, FSFULLR, GPL-2+ with Autoconf-data exception, GPL-3+ with Autoconf-data exception, X11, ISC, customFSFULLRWD … |
| pango1.0 | 1.56.3-1 | libpango-1.0-0 libpangocairo-1.0-0 libpangoft2-1.0-0 | quest | LGPL-2+ and LGPL-2.1+, Example, LGPL-2+, LGPL-2+ and TCL, Unicode, LGPL-2+ and ICU, Chromium-BSD-style … |
| pcsc-lite | 2.3.3-1 | libpcsclite1 | quest | BSD-3-clause, ISC, MIT, GPL-3+ |
| perl | 5.40.1-6+deb13u1 | libperl5.40 perl perl-modules-5.40 | base | GPL-1+ or Artistic, Expat, REGCOMP, and GPL-1+ or Artistic, GPL-3+-WITH-BISON-EXCEPTION, Unicode, GPL-1+ or Artistic, and Unicode, BZIP … |
| pixman | 0.44.0-3 | libpixman-1-0 | quest | free text; mentions MIT/X11 |
| procps | 2:4.0.4-9 | libproc2-0 procps | base | LGPL-2.1+, LGPL-2.0+, GPL-2.0+ |
| protobuf | 3.21.12-11+deb13u1 | libprotobuf32t64 | quest | BSD-3-Clause~Google, GPLWithACException, Apache-2.0, Public-Domain or Expat, GPL-3, BSD-3-Clause, Public-Domain … |
| python3-defaults | 3.13.5-1 | libpython3-stdlib python3 python3-minimal | base | free text; mentions MIT/X11, PSF |
| python3.13 | 3.13.5-2+deb13u5 | libpython3.13-minimal libpython3.13-stdlib python3.13 python3.13-minimal | base | free text; mentions GPL-2, BSD, MIT/X11, zlib, PSF |
| readline | 8.2-6 | libreadline8t64 readline-common | base | GPL-3+, GPL-2+, GFDL-NIV-1.3+, ISC-no-attribution, GPL-3 |
| rtmpdump | 2.4+20151223.gitfa8646d.1-2 | librtmp1 | base | free text; mentions GPL-2, LGPL |
| shared-mime-info | 2.4-5 | shared-mime-info | quest | free text; mentions GPL-2 |
| systemd | 257.13-1~deb13u1 | libpam-systemd libsystemd-shared systemd systemd-sysv | quest | LGPL-2.1+, CC0-1.0, GPL-2 with Linux-syscall-note exception, Expat, public-domain, GPL-2+ |
| tiff | 4.7.0-3+deb13u3 | libtiff6 | quest | Hylafax |
| unzip | 6.0-29+deb13u1 | unzip | quest | free text; mentions Info-ZIP |
| vulkan-loader | 1.4.309.0-1 | libvulkan1 | quest | Apache-2.0, MIT, MIT or Apache-2.0 |
| wayland | 1.23.1-3 | libwayland-client0 libwayland-cursor0 libwayland-egl1 libwayland-server0 | quest | X11 |
| xkeyboard-config | 2.42-1 | xkb-data | quest | free text; mentions MIT/X11 |
| xorg | 1:7.7+24+deb13u1 | x11-common | quest | free text; mentions GPL-2, MIT/X11 |
| xz-utils | 5.8.1-1+deb13u1 | xz-utils | base | 0BSD, GPL-2+, LGPL-2.1+, FSFULLR, GPL-3.0-or-later-WITH-Autoconf-exception-macro, none, PD … |
| z3 | 4.13.3-1 | libz3-4 | quest | Expat, GPL-2+ |
| zopfli | 1.0.3-3 | libzopfli1 | quest | Apache-2.0, Zlib, GPL-3+ |
