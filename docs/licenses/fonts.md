# Fonts: copyright notices and licences

Read from each font file's own name table (copyright, version, licence fields) on 2026-11-10, and checked again on
2026-11-12 against APK 0.7.16 (which fonts are inside it: font names searched in the build's asset files; the same eight). All are redistributed unmodified; MetaCodex builds TextMesh Pro SDF atlases
from them at run time (that is "use", not a modified font). The full licence texts ship next to this file on the
headset (`/usr/share/doc/metacodex/licenses/fonts/`) and are in the repository next to the fonts.

## In the app (APK 0.7.16)

| Font | Version | Copyright (verbatim from the font) | Licence | Full text in the repo |
|---|---|---|---|---|
| Inter Medium (body text) | 4.001;git-9221beed3 | Copyright 2016 The Inter Project Authors | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Inter-OFL.txt` |
| Rajdhani SemiBold (titles, buttons) | 1.201 | Copyright (c) 2014 Indian Type Foundry (info@indiantypefoundry.com) | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Rajdhani-OFL.txt` |
| DejaVu Sans Mono (terminal, code) | 2.37 | Copyright (c) 2003 by Bitstream, Inc. All Rights Reserved. DejaVu changes are in public domain | Bitstream Vera Fonts licence (+ public domain for the DejaVu changes) | `unity/Assets/MetaCodex/Fonts/DejaVu-LICENSE.txt` |
| DejaVu Sans (fallback) | 2.37 | Copyright (c) 2003 by Bitstream, Inc. All Rights Reserved. Copyright (c) 2006 by Tavmjong Bah. All Rights Reserved. DejaVu changes are in public domain | Bitstream Vera Fonts licence + Arev Fonts licence (public domain for the DejaVu changes) | `unity/Assets/MetaCodex/Fonts/DejaVu-LICENSE.txt` |
| Noto Sans Symbols 2 (fallback: ⏺) | 2.008 | Copyright 2022 The Noto Project Authors (https://github.com/notofonts/symbols) | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Noto-OFL.txt` |
| Noto Sans Symbols (fallback: ⎿) | 2.003 | Copyright 2022 The Noto Project Authors (https://github.com/notofonts/symbols) | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Noto-OFL.txt` |
| Noto Emoji (fallback, monochrome) | 3.002 | Copyright 2013 Google LLC | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Noto-OFL.txt` (the OFL text; its header names the Noto Symbols authors, this line is Noto Emoji's own notice) |
| Liberation Sans (TextMesh Pro's default font) | 2.00.1 | Digitized data copyright (c) 2010 Google Corporation. Copyright (c) 2012 Red Hat, Inc. | SIL Open Font License 1.1 | `unity/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` |

## In the repository only (not referenced by the build, not in the APK)

| Font | Version | Copyright | Licence | Full text |
|---|---|---|---|---|
| Orbitron | 2.001 | Copyright 2018 The Orbitron Project Authors (https://github.com/theleagueof/orbitron), with Reserved Font Name: "Orbitron". | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Orbitron-OFL.txt` |
| Inter SemiBold | 4.001 | Copyright 2016 The Inter Project Authors | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Inter-OFL.txt` |
| Rajdhani Medium | 1.201 | Copyright (c) 2014 Indian Type Foundry | SIL Open Font License 1.1 | `unity/Assets/MetaCodex/Fonts/Rajdhani-OFL.txt` |

## Other copies of fonts in the repository

| Where | Fonts | Licence | Notes |
|---|---|---|---|
| `docs/diagrams/fonts/` (diagram kit, 2026-11-12; may grow) | Inter Medium, Inter SemiBold, Rajdhani SemiBold, DejaVu Sans Mono, with `Inter-OFL.txt`, `Rajdhani-OFL.txt`, `DejaVu-LICENSE.txt` | OFL-1.1; Bitstream Vera | used by `docs/diagrams/kit.css` through `@font-face`; Windows Chrome renders the PNGs, nothing is shipped in the APK |
| `native/xr/mcx_font.h`, `userland/overlay/opt/metacodex/quest-native/xr/mcx_font.h` (in the overlay) | DejaVu Sans Mono rasterised to a 16 x 32 ASCII coverage atlas by `native/xr/tools/make-font.py` | Bitstream Vera licence; DejaVu changes public domain | a derived work of the font: the notice must go with every copy, in apps built from the template too (gap G12 in `docs/LICENSES.md`) |

## What the licences ask

- **SIL OFL 1.1**: each copy of the font must carry its copyright notice and the licence (done: this file and the
  `*-OFL.txt` texts ship with the app); the font may not be sold by itself; a modified font may not use a Reserved
  Font Name (Orbitron has one; we never modify fonts).
- **Bitstream Vera / Arev**: keep the copyright and licence notices; the names "Bitstream" and "Vera" may not be used
  for modified fonts (we never modify them).
