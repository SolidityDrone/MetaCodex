| pipeline | all | en/single | en/multi | en/noisy | en/none | it/single | it/multi | it/noisy | it/none | en | it | ms / sentence (PC) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| today: fast path -> LLM (Qwen3 0.6B) | 58 | 86 | 76 | 72 | 73 | 33 | 4 | 40 | 80 | 82 | 34 | LLM 209 (median, on the 237 the fast path misses) |
| LLM alone | 48 | 68 | 28 | 64 | 73 | 33 | 4 | 40 | 80 | 62 | 34 | 205 |
| fast path alone | 40 | 71 | 72 | 48 | 100 | 1 | 0 | 0 | 100 | 71 | 9 | - |
| fast path -> multilingual MiniLM-L12 (118M) | 81 | 94 | 92 | 60 | 93 | 69 | 76 | 75 | 87 | 89 | 72 | 6.0 (router, θ 0.48) |
| multilingual MiniLM-L12 (118M) alone | 74 | 82 | 76 | 48 | 93 | 68 | 76 | 75 | 87 | 77 | 71 | 6.0 |
| fast path -> multilingual MiniLM-L12 (118M) + IT examples | 80 | 94 | 92 | 60 | 80 | 70 | 80 | 85 | 67 | 88 | 73 | 6.3 (router, θ 0.52) |
| multilingual MiniLM-L12 (118M) + IT examples alone | 75 | 83 | 80 | 48 | 80 | 70 | 80 | 85 | 67 | 77 | 73 | 6.3 |
| fast path -> multilingual-e5-small (118M) | 78 | 91 | 88 | 76 | 73 | 70 | 52 | 75 | 87 | 87 | 69 | 6.5 (router, θ 0.30) |
| multilingual-e5-small (118M) alone | 75 | 82 | 80 | 76 | 73 | 70 | 52 | 75 | 87 | 80 | 69 | 6.5 |
| fast path -> multilingual-e5-small (118M) + IT examples | 79 | 91 | 88 | 76 | 73 | 68 | 72 | 85 | 67 | 87 | 70 | 6.3 (router, θ 0.30) |
| multilingual-e5-small (118M) + IT examples alone | 75 | 82 | 80 | 76 | 73 | 68 | 72 | 85 | 67 | 80 | 70 | 6.3 |
| fast path -> bge-small-en (33M, English) | 64 | 94 | 92 | 72 | 73 | 37 | 24 | 35 | 80 | 89 | 39 | 5.7 (router, θ 0.62) |
| bge-small-en (33M, English) alone | 59 | 83 | 84 | 68 | 73 | 37 | 24 | 35 | 80 | 80 | 38 | 5.7 |
| fast path -> bge-small-en (33M, English) + IT examples | 74 | 94 | 92 | 72 | 73 | 55 | 56 | 80 | 67 | 89 | 59 | 5.6 (router, θ 0.66) |
| bge-small-en (33M, English) + IT examples alone | 69 | 83 | 84 | 68 | 73 | 54 | 56 | 80 | 67 | 80 | 58 | 5.6 |
| fast path -> potion-multilingual (static) | 75 | 92 | 88 | 72 | 67 | 59 | 64 | 65 | 87 | 87 | 63 | 0.3 (router, θ 0.34) |
| potion-multilingual (static) alone | 69 | 76 | 76 | 76 | 67 | 59 | 64 | 65 | 87 | 75 | 63 | 0.3 |
| fast path -> potion-multilingual (static) + IT examples | 75 | 92 | 88 | 72 | 67 | 61 | 68 | 55 | 73 | 87 | 62 | 0.3 (router, θ 0.34) |
| potion-multilingual (static) + IT examples alone | 69 | 76 | 76 | 76 | 67 | 61 | 68 | 55 | 73 | 75 | 62 | 0.3 |
| fast path -> potion-base-8M (static, English) | 57 | 91 | 92 | 72 | 73 | 23 | 12 | 10 | 100 | 87 | 26 | 0.3 (router, θ 0.32) |
| potion-base-8M (static, English) alone | 52 | 80 | 84 | 64 | 73 | 22 | 12 | 10 | 100 | 78 | 26 | 0.3 |
| fast path -> potion-base-8M (static, English) + IT examples | 75 | 91 | 92 | 72 | 73 | 58 | 64 | 70 | 87 | 87 | 63 | 0.3 (router, θ 0.36) |
| potion-base-8M (static, English) + IT examples alone | 70 | 80 | 84 | 64 | 73 | 58 | 64 | 70 | 87 | 78 | 63 | 0.3 |
