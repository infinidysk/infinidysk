# Vendored in-tree libraries

These projects are developed in this repository and consumed by the backend via
`ProjectReference`. They are **not** published to NuGet from this repo.

| Library | Source | Tag / pin | Commit |
|---------|--------|-----------|--------|
| SharpCompress | https://github.com/nzbdav/sharpcompress | v0.54.0 | `f3b491c13ab9f1b0695dc1a391e8371fc13d612f` |
| UsenetSharp | https://github.com/nzbdav/UsenetSharp | v3.3.0 | `ded1219a9de8b0e0914039f8ba9e3bde534fb479` |
| RapidYencSharp | https://github.com/nzbdav/RapidYencSharp | v3.0.0 | `aa473d74b49b66dd5d33f8fe3cee6756b8f7996c` |
| rapidyenc (submodule) | https://github.com/nzbdav/rapidyenc | submodule pin | `81b6ed33c6eac449738adfecfeb55d3680c9b845` |

`libs/rapidyenc` remains a standalone active repository and is consumed here as a
git submodule. Build natives with `scripts/build-rapidyenc.sh` (host RID by
default). Docker/CI build musl natives from the submodule for Alpine images.

Full history for the C# libraries remains in the archived source repositories.
Licensing and attribution files live beside each library.
