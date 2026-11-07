# Third-party binaries in this folder

| File | Upstream | Version | License |
|---|---|---|---|
| libproot.so | PRoot, Termux fork (https://github.com/termux/proot), `bin/proot` | 5.1.107.102 | GPL-2.0-or-later |
| libproot-loader.so | same package, `libexec/proot/loader` | 5.1.107.102 | GPL-2.0-or-later |
| libtalloc.so | talloc (https://talloc.samba.org) | 2.5.0 | LGPL-3.0-or-later |
| libandroid-shmem.so | https://github.com/termux/libandroid-shmem | 0.7 | BSD-3-Clause |

Taken unmodified from the Termux package repository (aarch64), except: file names changed to lib*.so,
DT_NEEDED libtalloc.so.2 -> libtalloc.so, RUNPATH removed (patchelf). They run as separate
executables / libraries; the source is available at the URLs above. Regenerate with Linux/fetch-proot.sh.
