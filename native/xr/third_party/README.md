# Third-party code in native/xr/third_party

Vendored single-file libraries, used by `mcx_model.h` (glTF models). Both files are unmodified copies of the
upstream files. Do not edit them: to update, fetch the new release, replace the file, and update this table.

| File | Upstream | Pinned to | Fetched from | Licence | sha256 |
|---|---|---|---|---|---|
| `cgltf.h` | https://github.com/jkuhlmann/cgltf | release tag `v1.15` (2025-02-09), commit `360db1a95480fe102ae9c69b27c5d101167ff5ba` | https://raw.githubusercontent.com/jkuhlmann/cgltf/v1.15/cgltf.h | MIT: "Copyright (c) 2018-2021 Johannes Kuhlmann" (the notice is at the end of the file) | `e378a21c084bf1f288bb799de827bb26906efb024255f1ecf1705ea13f11c6ec` |
| `stb_image.h` | https://github.com/nothings/stb | no release tags upstream. Pinned to commit `9f755b604db3492df2cbeb39bf84b81e3e3932e3` (2026-11-12), the last commit that changed the file: its header says `stb_image - v2.32` | https://raw.githubusercontent.com/nothings/stb/9f755b604db3492df2cbeb39bf84b81e3e3932e3/stb_image.h | Public domain, or MIT (ALTERNATIVE A and B in the file: choose either) | `deb4f18c9ca21b751a1323019031c94c0bf5ca1d2929ef5913cc56f00aab6e91` |

How the kit uses them:

- `mcx_model.h` defines `MCX_MODEL_IMPLEMENTATION` in one `.cpp` of the app. That `.cpp` compiles both libraries once.
- `mcx_model.h` sets `STBI_ONLY_PNG` and `STBI_ONLY_JPEG` (glTF images are PNG or JPEG) and `STBI_NO_STDIO`
  (images come from memory).
- cgltf reads external files through the kit's callbacks, so the same code reads the file system and the app's
  `assets/` folder.

To check a copy: `sha256sum native/xr/third_party/*.h` must match the table.
