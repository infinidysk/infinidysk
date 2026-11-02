# Changelog

## [2.0.0-alpha.17](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.16...v2.0.0-alpha.17) (2026-11-02)


### Bug Fixes

* **stream:** keep disk cache writes the startup scan used to delete ([9243333](https://github.com/infinidysk/infinidysk-rs/commit/924333348db4dbae583e9018c59d6a9bccabf491))
* **stream:** keep reading the disk cache after its directory link changes ([a62c59a](https://github.com/infinidysk/infinidysk-rs/commit/a62c59a102f0d76458bfca5f7504e2b1364b6be4))
* **stream:** stop a reopened disk cache deleting writes still in progress ([3f0d9e0](https://github.com/infinidysk/infinidysk-rs/commit/3f0d9e01b3a68657b5bb36c4c709d1f2026e52d3))

## [2.0.0-alpha.16](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.15...v2.0.0-alpha.16) (2026-11-02)


### Features

* **docker:** containers report healthy only once the mount and database are ready ([#48](https://github.com/infinidysk/infinidysk-rs/issues/48)) ([8368b59](https://github.com/infinidysk/infinidysk-rs/commit/8368b59d9bce057d2cf501ceb6cd854cb0d26122))


### Bug Fixes

* **ui:** ignore extension scripts when checking for updates ([fb2dcc1](https://github.com/infinidysk/infinidysk-rs/commit/fb2dcc1da9773028914fa1b069b7825228a4955f))
* **ui:** keep activity visible beside large spikes ([eecf334](https://github.com/infinidysk/infinidysk-rs/commit/eecf334155ce4993ae5e1cbca4f08807d5acff32))
* **ui:** keep the read position bar at a fixed width ([2431234](https://github.com/infinidysk/infinidysk-rs/commit/243123497d33fafa591a0e354386aa2424d272f2))
* **ui:** show playback in green and health checks in blue on the activity graph ([f49b3f8](https://github.com/infinidysk/infinidysk-rs/commit/f49b3f8aa627d5c4b1c8abdb06c5280dfb8b0009))

## [2.0.0-alpha.15](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.14...v2.0.0-alpha.15) (2026-11-01)


### Features

* **api:** check provider logins and report each provider's connection state ([e25ad95](https://github.com/infinidysk/infinidysk-rs/commit/e25ad95c051ef2450e0509efc60d8cb59e1538a8))
* **api:** search and filter health and import history ([87b59e4](https://github.com/infinidysk/infinidysk-rs/commit/87b59e4bb7002b5aa08696107df5723b1dd7a834))
* **health:** repair through Lidarr and Whisparr ([c3f6db2](https://github.com/infinidysk/infinidysk-rs/commit/c3f6db23b1e51d2bbdaf8eb9766e62a21996fc9a))
* **ui:** explain each health state on the Health page ([cf90a90](https://github.com/infinidysk/infinidysk-rs/commit/cf90a90d78be819fb70dd24a5034da174d771245))
* **ui:** search and filter import and health history ([2b18ba6](https://github.com/infinidysk/infinidysk-rs/commit/2b18ba680936fb4449c5e47a0fd3164e7779777e))
* **ui:** show which providers serve each read in progress ([ffc7669](https://github.com/infinidysk/infinidysk-rs/commit/ffc7669a2542180be7653cdc9a66bed0d5f014e6))
* **ui:** test provider connections and show their state ([0e510e4](https://github.com/infinidysk/infinidysk-rs/commit/0e510e4c16377a36a1e224637ad1bb094593cba4))


### Bug Fixes

* **deps:** bump the cargo-minor-patch group with 3 updates ([#45](https://github.com/infinidysk/infinidysk-rs/issues/45)) ([b23c2dd](https://github.com/infinidysk/infinidysk-rs/commit/b23c2dd25a2993c15df5912adb3cb41a6531c275))
* **health:** check ebooks instead of ignoring them ([3100e75](https://github.com/infinidysk/infinidysk-rs/commit/3100e75793a0af68c9d0d5ffc6a938dda21e95f0))
* **import:** check imported files with recorded misses right away ([334741e](https://github.com/infinidysk/infinidysk-rs/commit/334741eef10b280cee82a749b8495dde1fc270d6))
* **ui:** align overview read rows and drop the activity chart's y-axis ([db215a9](https://github.com/infinidysk/infinidysk-rs/commit/db215a9d117b8114d01d244c2c468eece0dce5ca))
* **ui:** bigger tap targets for slider thumbs, Advanced settings and inline links ([0bdb789](https://github.com/infinidysk/infinidysk-rs/commit/0bdb78952206c0778ad92bf54a11349bd76ec489))
* **ui:** call the stats library card Library and make provider settings a button ([aca696f](https://github.com/infinidysk/infinidysk-rs/commit/aca696f7fd582ad8573678bfc24d9c075a410d67))
* **ui:** clearer provider name and Arr download-client hints ([32fd40b](https://github.com/infinidysk/infinidysk-rs/commit/32fd40b01e0d0df520736caa88197fc7c7a46fdc))
* **ui:** fill the system card grid and give the mount path room on phones ([cb00690](https://github.com/infinidysk/infinidysk-rs/commit/cb00690290841ac61b2277e1320efb36d3e4b942))
* **ui:** full-width editor sheets on phones and an explicit provider pick ([a55c292](https://github.com/infinidysk/infinidysk-rs/commit/a55c2926c940da99ca919efbe5f9cdff8f9e7bcf))
* **ui:** name 1.x STRM links plainly and hide the notice when unused ([c114291](https://github.com/infinidysk/infinidysk-rs/commit/c114291e8d60874289616420fb61ceeecad9efa8))
* **ui:** one split button to start read traces ([089f733](https://github.com/infinidysk/infinidysk-rs/commit/089f7332822f7588ffebe84eb996c7dc278aa6fe))
* **ui:** readable maintenance errors, relative run times and why Run now is off ([2b61706](https://github.com/infinidysk/infinidysk-rs/commit/2b61706c4de785644a0df31a18fd8f1a5376656e))
* **ui:** show a dash for empty explorer cells and explain a missing import time ([23f7c5b](https://github.com/infinidysk/infinidysk-rs/commit/23f7c5bd1e15bef85ca5b84b5bad4e1c0182b0ca))
* **ui:** show provider misses as warnings on stats ([f61e50d](https://github.com/infinidysk/infinidysk-rs/commit/f61e50d74f1c49e79b2493dd9b6875d6d9124595))
* **ui:** show which performance settings the environment sets ([eb5fd6f](https://github.com/infinidysk/infinidysk-rs/commit/eb5fd6f80ed1186db78d52970160e567c72bd9bb))
* **ui:** stop the update banner from reloading every page ([481b7dc](https://github.com/infinidysk/infinidysk-rs/commit/481b7dce285127a280f7cf3ea200f59be9ca064e))
* **ui:** tidy phone layouts on overview, providers and explorer ([f5a4130](https://github.com/infinidysk/infinidysk-rs/commit/f5a4130b88033aa3c4c953fd0dc5c3b852f4ea48))
* **ui:** type delete to run unlinked-file removal by hand ([045ffdd](https://github.com/infinidysk/infinidysk-rs/commit/045ffdd66ebed083b6271f565d81e4cf0319deda))

## [2.0.0-alpha.14](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.13...v2.0.0-alpha.14) (2026-11-01)


### Features

* **ui:** show disk cache use and health on the System page ([c1ed06b](https://github.com/infinidysk/infinidysk-rs/commit/c1ed06bd4e13c139c102c1af360c8a15f08d9e3c))
* **ui:** show release notes from the System page ([1c525cc](https://github.com/infinidysk/infinidysk-rs/commit/1c525ccc5d3a1c3cb9dfd6b2627e015505f35855))


### Bug Fixes

* **api:** let empty folders of categories no longer offered be removed ([86f0c14](https://github.com/infinidysk/infinidysk-rs/commit/86f0c1471458be7148bcbc601ff5387043bf3161))
* **ui:** color the wordmark on the sign-in and setup screens ([76e197e](https://github.com/infinidysk/infinidysk-rs/commit/76e197ec8fe3bde98a92ce0301bf71ff9376ba92))
* **ui:** keep unsaved settings when saved settings change underneath ([d2451de](https://github.com/infinidysk/infinidysk-rs/commit/d2451de9b9c09ece0a51509a12eaf0596bd03320))
* **ui:** reload open tabs onto a new version after an upgrade ([8d16445](https://github.com/infinidysk/infinidysk-rs/commit/8d164459dd775c1d39e623adfb7f64c72a96bdd2))
* **ui:** show the release folder for obfuscated file names ([b36123d](https://github.com/infinidysk/infinidysk-rs/commit/b36123dc9ae709b34d2a0f7e493ce735f1c855f9))
* **ui:** tuck reader process ids behind an info tip ([22f8a02](https://github.com/infinidysk/infinidysk-rs/commit/22f8a02bb8c46b964d16b242217b4a2961e16bad))

## [2.0.0-alpha.13](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.12...v2.0.0-alpha.13) (2026-11-01)


### Features

* **import:** serve upgrade progress while importing C# data ([0ef0f27](https://github.com/infinidysk/infinidysk-rs/commit/0ef0f27f4b5307d798bce780cc60d97be1f55049))


### Bug Fixes

* **api:** check the public image registry for newer releases ([b1c4a29](https://github.com/infinidysk/infinidysk-rs/commit/b1c4a29ca6d07f8bab6e125585e5cce7dc1beed7))
* **import:** upgraded installs bring back files with an extra listed article ([#41](https://github.com/infinidysk/infinidysk-rs/issues/41)) ([8c9c71d](https://github.com/infinidysk/infinidysk-rs/commit/8c9c71d7f807773069c20d65a5826593faee077e))
* **ui:** preserve the app when upgrade status refresh fails ([7fcbd4c](https://github.com/infinidysk/infinidysk-rs/commit/7fcbd4cb5b0dd340d0a3960ce7cde7ca98787038))


### Performance Improvements

* **import:** reuse matching measured volume geometry ([#43](https://github.com/infinidysk/infinidysk-rs/issues/43)) ([ef945ae](https://github.com/infinidysk/infinidysk-rs/commit/ef945ae75d745898883a0ea48118c8490dbdeb4c))

## [2.0.0-alpha.12](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.11...v2.0.0-alpha.12) (2026-11-01)


### Bug Fixes

* **import:** upgrades carry over archive files with wrong sizes in their posts again ([#37](https://github.com/infinidysk/infinidysk-rs/issues/37)) ([a716d92](https://github.com/infinidysk/infinidysk-rs/commit/a716d92bfa98338e795f34b8d4a3a00ee9e8a251))
* **import:** upgrades no longer hide a missing article in archive volumes that match an earlier volume's size ([#40](https://github.com/infinidysk/infinidysk-rs/issues/40)) ([f62bc65](https://github.com/infinidysk/infinidysk-rs/commit/f62bc653fde1b319134780533831c3a6e0a6cf92))
* **stream:** hedge pending responses when the preferred provider has spare capacity ([#38](https://github.com/infinidysk/infinidysk-rs/issues/38)) ([d3dd3dc](https://github.com/infinidysk/infinidysk-rs/commit/d3dd3dc2a23b7e6cf78a38f22c7ce02855c0331f))

## [2.0.0-alpha.11](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.10...v2.0.0-alpha.11) (2026-10-31)


### Bug Fixes

* **import:** restore more skipped upgrade files and correct log ownership ([#34](https://github.com/infinidysk/infinidysk-rs/issues/34)) ([a49f118](https://github.com/infinidysk/infinidysk-rs/commit/a49f1186db0d71e850fd7211236cb70d02b9dd2a))

## [2.0.0-alpha.10](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.9...v2.0.0-alpha.10) (2026-10-31)


### Features

* **health:** count a 100% import as the first health check ([c25aaf1](https://github.com/infinidysk/infinidysk-rs/commit/c25aaf1c1e6180e9560be6522673fa60899027ec))
* **import:** note that WebDAV deletes stop working after upgrading from a writable C# setup ([ed445ac](https://github.com/infinidysk/infinidysk-rs/commit/ed445ac8f3e9df28e19c17fe5b9c012e7e437dbb))
* **ui:** explain what the health sample size covers ([3ed8e04](https://github.com/infinidysk/infinidysk-rs/commit/3ed8e0436c39c36cf09c0c22afd62b2e939c6c4c))


### Bug Fixes

* **auth:** accept the admin username in any case, as 1.x did ([24d6753](https://github.com/infinidysk/infinidysk-rs/commit/24d67534e6f68a9995c43c67a759c9eee2a3c140))
* **queue:** posts with wrong yEnc file sizes now import, and upgrades bring back files they skipped ([#33](https://github.com/infinidysk/infinidysk-rs/issues/33)) ([63d7b6e](https://github.com/infinidysk/infinidysk-rs/commit/63d7b6e21375ef6160296a8b31a718c37e5277a4))

## [2.0.0-alpha.9](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.8...v2.0.0-alpha.9) (2026-10-30)


### Features

* **api:** say which setting a validation error is about ([305e364](https://github.com/infinidysk/infinidysk-rs/commit/305e3642f71eb591422139a433b2b729f3c5ad22))
* **ui:** add a Stats page and show what needs attention on Overview ([5be5f01](https://github.com/infinidysk/infinidysk-rs/commit/5be5f01bc3ed91136d59ac4d700f5c8abccfd3bd))
* **ui:** color dysk in the sidebar wordmark ([923a712](https://github.com/infinidysk/infinidysk-rs/commit/923a7129be4c8b3be3c59b8a389a089812a5dcfa))
* **ui:** edit providers in a side sheet ([4e2ce95](https://github.com/infinidysk/infinidysk-rs/commit/4e2ce95435fbff309262eb59e3724716ca45f2f6))
* **ui:** lay settings out as grouped panels ([16b94bf](https://github.com/infinidysk/infinidysk-rs/commit/16b94bf9412e56186e3dd60ffc68e328de8cfc0f))
* **ui:** manage Arr instances in a list and side sheet ([9ca51f4](https://github.com/infinidysk/infinidysk-rs/commit/9ca51f424607524d76ee9b496074b66954fb3320))
* **ui:** merge Admin API and Authentication into Access ([601d5d0](https://github.com/infinidysk/infinidysk-rs/commit/601d5d092a941e4938217c4d35e14b1926c061a6))
* **ui:** move Support to the System page ([6ad8c2c](https://github.com/infinidysk/infinidysk-rs/commit/6ad8c2c5bfda5c450f692c11780d712e01113177))
* **ui:** move the download speed limit to Performance ([8fdfb8e](https://github.com/infinidysk/infinidysk-rs/commit/8fdfb8e8705badae3e1381d44554e7e5889c0fe9))
* **ui:** show connection details on WebDAV, Download client and Mount ([703f418](https://github.com/infinidysk/infinidysk-rs/commit/703f4186dac7ddc48ed85915e24118c1b91be5f1))
* **ui:** split Library settings from Imports ([ab4b2a6](https://github.com/infinidysk/infinidysk-rs/commit/ab4b2a635024e7de0ef28aafba1bdc823f9027a6))
* **ui:** switch experiments on and off in Settings ([deceb03](https://github.com/infinidysk/infinidysk-rs/commit/deceb039a8a2d62028949d090a2e2145e25917a4))
* **ui:** tidy provider row and header actions ([a65951a](https://github.com/infinidysk/infinidysk-rs/commit/a65951abf4034c584d8628cbf9c2ebab68b2e3e9))


### Bug Fixes

* **archive:** key RAR archives with the first 127 password characters ([134eda9](https://github.com/infinidysk/infinidysk-rs/commit/134eda965e295937b700530d161c0763f2ca6822))
* **deps:** bump crc32fast ([#31](https://github.com/infinidysk/infinidysk-rs/issues/31)) ([382d341](https://github.com/infinidysk/infinidysk-rs/commit/382d341757fb6fc1ee7f329309273f39f2552803))
* **stream:** keep win-floor out of analysis reads ([d348590](https://github.com/infinidysk/infinidysk-rs/commit/d34859096bab68848e84ad32a359195f8b27bd93))
* **stream:** stop speculative fetches past bounded read ranges ([9ab5f10](https://github.com/infinidysk/infinidysk-rs/commit/9ab5f10fe0b5092873dcd89358d2f0322af5878f))
* **ui:** fit explorer, queue and logs on phones; tidy stats and health empty states ([40b29b6](https://github.com/infinidysk/infinidysk-rs/commit/40b29b6f0a61f1265c90a24d6bc250f8809a24ea))
* **ui:** fit tables on phones and follow the design system on queue, explorer, health, system and stats ([8f8b3d7](https://github.com/infinidysk/infinidysk-rs/commit/8f8b3d7c8b98d37bf250d386e15fea627f914ca4))
* **ui:** fix settings copy and placeholders ([ac5f273](https://github.com/infinidysk/infinidysk-rs/commit/ac5f2735c071d032e771e7116504250a7bfc27fe))
* **ui:** format sizes the same way on Performance ([54193ae](https://github.com/infinidysk/infinidysk-rs/commit/54193aeba06081395f11f8a46f087dba13dedc00))
* **ui:** give icon-only buttons tooltips and specific names ([84bd40a](https://github.com/infinidysk/infinidysk-rs/commit/84bd40a075c6e68d233903a5f740abc3256baefc))
* **ui:** harden settings forms ([10be9ee](https://github.com/infinidysk/infinidysk-rs/commit/10be9ee672dcd52ee0d1ba3253e973b77d1c27e7))
* **ui:** keep text on the type scale ([4d04e07](https://github.com/infinidysk/infinidysk-rs/commit/4d04e0715448d851bfff2880d3022fd6c0a0c944))
* **ui:** keep the live status in the sidebar footer ([d387920](https://github.com/infinidysk/infinidysk-rs/commit/d38792072ab1d0a8fee18a6dd1c69b843bbcd425))
* **ui:** link the health-checks-off notice to health settings ([ac171f6](https://github.com/infinidysk/infinidysk-rs/commit/ac171f6589b2abeba61c665655d430c6460e3581))
* **ui:** make preset cards one radio group ([69c2d20](https://github.com/infinidysk/infinidysk-rs/commit/69c2d20a480afdd9e4bbc0d5b6de2a4158d3154c))
* **ui:** make sign-in errors, focus and button contrast accessible ([e6bbc19](https://github.com/infinidysk/infinidysk-rs/commit/e6bbc19c3b023cc8d2f3ee47448bb8b8b0f9582e))
* **ui:** mobile menu scrolls and closes on every navigation ([402d9e1](https://github.com/infinidysk/infinidysk-rs/commit/402d9e1237b60238191bbfeae63a7c38ce35f54e))
* **ui:** page titles, skip link and screen-reader labels in the shell ([de3e393](https://github.com/infinidysk/infinidysk-rs/commit/de3e393f89bbb630df989e1289fa3cc8cd142cf5))
* **ui:** put the WebDAV password beside its label ([a242960](https://github.com/infinidysk/infinidysk-rs/commit/a2429607b6c6bd86e36bd63636638c8f745ace4f))
* **ui:** readable warning and success text in light theme ([20f0e7c](https://github.com/infinidysk/infinidysk-rs/commit/20f0e7c1c5bfde478cd58a938fb55847bcf2cbe9))
* **ui:** rename Report 1970 file dates to Disable file timestamps ([4c2b064](https://github.com/infinidysk/infinidysk-rs/commit/4c2b0643d086d6132769bb27bd74d4a7208d776f))
* **ui:** shorten settings hints ([0888ab9](https://github.com/infinidysk/infinidysk-rs/commit/0888ab92b3cce6659c3b7f491f52a8bc621bb5c6))
* **ui:** show maintenance run reasons ([818ea08](https://github.com/infinidysk/infinidysk-rs/commit/818ea08548a92aa42c24a1af1ee84a8e4f852133))
* **ui:** show recent reads ten at a time ([85b5e1b](https://github.com/infinidysk/infinidysk-rs/commit/85b5e1b158b9463c262572359f7b0bc97d28cab8))
* **ui:** show server validation errors on their field ([8060418](https://github.com/infinidysk/infinidysk-rs/commit/80604188f989d4fd175b2885aa00316f2b651802))
* **ui:** show the provider speed test as a row button ([414f145](https://github.com/infinidysk/infinidysk-rs/commit/414f145876c1fbd077eb8d8202f17392020faecc))
* **ui:** use the Advanced settings toggle in Performance ([70a5ada](https://github.com/infinidysk/infinidysk-rs/commit/70a5adadf7c7a1cdd67f051e6c413cc0406cefc6))


### Performance Improvements

* **nntp:** add pipe-weight experiment that charges large articles more download slots ([7976f72](https://github.com/infinidysk/infinidysk-rs/commit/7976f7286282340796c0bc4d64a9f0e3d40ae2c7))
* **nntp:** charge large playback articles one download slot per MB ([2d9e717](https://github.com/infinidysk/infinidysk-rs/commit/2d9e71747ee554676ffb9f024dff50b2939c75cf))
* **ui:** load app pages on demand so sign-in renders first ([396f6ad](https://github.com/infinidysk/infinidysk-rs/commit/396f6ade272afc547a34fdb3301360e928bdbe59))

## [2.0.0-alpha.8](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.7...v2.0.0-alpha.8) (2026-10-27)


### Features

* **mount:** optional 1970 file dates to match rclone --no-modtime mounts ([df80059](https://github.com/infinidysk/infinidysk-rs/commit/df80059bb3bde85474f4271a0d6f859a22c44b57))


### Bug Fixes

* **api:** show why an Arr rejected a request ([eae6f72](https://github.com/infinidysk/infinidysk-rs/commit/eae6f723b6c393104408a409416f67bba1394398))
* **mount:** answer GETATTR on an open file from its handle ([#27](https://github.com/infinidysk/infinidysk-rs/issues/27)) ([8a8dbb3](https://github.com/infinidysk/infinidysk-rs/commit/8a8dbb3ac187a59095df70671cd69da3bf051cde))
* **mount:** keep the FUSE session at error when RUST_LOG is set ([9d0bbd5](https://github.com/infinidysk/infinidysk-rs/commit/9d0bbd54d25519b3c9a250fec2684c1ed209d09c))
* **stream:** stream progress only for the two segments read next ([#28](https://github.com/infinidysk/infinidysk-rs/issues/28)) ([18a20b7](https://github.com/infinidysk/infinidysk-rs/commit/18a20b763cb77764915f743fe2a15205bfc7808d))


### Performance Improvements

* **db:** skip the idle-connection ping before SQLite queries ([8ea2b97](https://github.com/infinidysk/infinidysk-rs/commit/8ea2b971a731117d8ae388f06b6b3053139aeb36))
* **mount:** read kernel requests without a blocking-pool hop when running as root ([47b1fbe](https://github.com/infinidysk/infinidysk-rs/commit/47b1fbe24629682e492557c90c4e002eb2d3e534))
* **mount:** stop revalidating fixed directories every second ([7a4ab81](https://github.com/infinidysk/infinidysk-rs/commit/7a4ab81c159645aa8f4b42601c91c7ac2621ef5b))
* **nntp:** share decoded bodies with the memory cache instead of copying them ([c3afc1d](https://github.com/infinidysk/infinidysk-rs/commit/c3afc1d678d71b67b7e7ba5641387c943a0c5bd4))
* **queue:** parse PAR2 index articles once as they arrive ([b2dfef8](https://github.com/infinidysk/infinidysk-rs/commit/b2dfef8f9e78ebc9d89ceedc297a32d8409038af))
* reopen files without re-reading their metadata ([0352afb](https://github.com/infinidysk/infinidysk-rs/commit/0352afb8b9d9104a787c414edb7c8a3e3c9a5707))
* **stream:** skip a body copy on each disk cache write and hit ([b606490](https://github.com/infinidysk/infinidysk-rs/commit/b6064907c277d3a09144b49a4ee0e3b0bb06edf4))

## [2.0.0-alpha.7](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.6...v2.0.0-alpha.7) (2026-10-26)


### Features

* **ui:** show warm provider connections in the sidebar ([5c6686f](https://github.com/infinidysk/infinidysk-rs/commit/5c6686fc57ea282d94709d581f5bcfe4921a0bb1))


### Bug Fixes

* **mount:** allow group members to unlink completed symlinks ([329f7e2](https://github.com/infinidysk/infinidysk-rs/commit/329f7e2f9019dee20cb3041282d7d2d172d2ed61))
* **mount:** stop FUSE reads stalling forever behind a busy-reader probe ([b430775](https://github.com/infinidysk/infinidysk-rs/commit/b4307755854b3c2a2425ca60019eb188f1c0cd15))
* **nntp:** log what a provider replied when it sends an unexpected response ([bccc511](https://github.com/infinidysk/infinidysk-rs/commit/bccc511a5d80a646ed0a180ac507adcf31589c3e))
* **nntp:** retain connection headroom during background requests ([206c817](https://github.com/infinidysk/infinidysk-rs/commit/206c8176adfccea79eae89dacabbe41e700262ad))
* **queue:** group old-style RAR volumes past .r99 with their set ([adae9cc](https://github.com/infinidysk/infinidysk-rs/commit/adae9cc2e0932f2125b2759900ee3a9826eab575))
* **queue:** import files whose NZB lists a part twice ([0471e1a](https://github.com/infinidysk/infinidysk-rs/commit/0471e1a6541daba1451d0bbd2786e2a60bc21703))
* **queue:** stop the article check at the first missing segment of a media file ([2c9922b](https://github.com/infinidysk/infinidysk-rs/commit/2c9922b36d403a4d12b0017707c137cc157ca68f))
* **queue:** time out STRM directory checks and writes during handoff ([4b7c0e2](https://github.com/infinidysk/infinidysk-rs/commit/4b7c0e229a105fab7a70aa26ba1756c63dfb1c6c))
* **ui:** copy keys and stream links on plain-HTTP LAN addresses ([95c1e44](https://github.com/infinidysk/infinidysk-rs/commit/95c1e44640216ed625f37aa3e18505d33c513a94))

## [2.0.0-alpha.6](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.5...v2.0.0-alpha.6) (2026-10-25)


### Features

* **api:** warn when a read stalls and add stall details to live status ([c2d934e](https://github.com/infinidysk/infinidysk-rs/commit/c2d934eb43ffb1d96014da6be19da6b91ac9c087))
* **mount:** report the effective kernel FUSE limits and queue ([e7c0eba](https://github.com/infinidysk/infinidysk-rs/commit/e7c0eba0a3ac052a21b31de50db12986a6b5f76f))
* **nntp:** show what each busy provider connection is waiting for ([af75117](https://github.com/infinidysk/infinidysk-rs/commit/af7511795f1da287261f22172a21b0f4cbca4efe))
* **stream:** show which stage each waiting read is stuck in ([2e8014e](https://github.com/infinidysk/infinidysk-rs/commit/2e8014eae4ec1e3f1dc2efe6367541b961d62220))
* **ui:** restart from settings to apply startup-only changes ([2279cf1](https://github.com/infinidysk/infinidysk-rs/commit/2279cf1bb274549cbdca22e8d76f785da57ef4ef))


### Bug Fixes

* **deps:** bump the cargo-minor-patch group with 2 updates ([#23](https://github.com/infinidysk/infinidysk-rs/issues/23)) ([3c3177a](https://github.com/infinidysk/infinidysk-rs/commit/3c3177aa76f79ee8597ac47469fca4d07e89e26a))
* **deps:** bump the npm-minor-patch group in /ui with 6 updates ([#24](https://github.com/infinidysk/infinidysk-rs/issues/24)) ([5ea22cf](https://github.com/infinidysk/infinidysk-rs/commit/5ea22cf97c2575dbb4da7d01c4f44eb9ba92375a))
* **deps:** update ctutils to 0.4.4 for RUSTSEC-2026-0385 ([0b8287f](https://github.com/infinidysk/infinidysk-rs/commit/0b8287fea6c74947112d7ecddd19dec9bd594bda))
* **import:** refuse C# installs older than v1.3.0 before reading their schema ([341cfb1](https://github.com/infinidysk/infinidysk-rs/commit/341cfb1f3209daa3d4691f16dc8181af395623ee))
* **stream:** never let a blocked read wait on the read-ahead budget ([dfff4d1](https://github.com/infinidysk/infinidysk-rs/commit/dfff4d10e508e69cad713b0036ef3913a8ea0411))
* **stream:** warn about misaligned segments once a minute per file ([ff4a9ea](https://github.com/infinidysk/infinidysk-rs/commit/ff4a9ea27b7c75f4ec05b05f11b0a7b07b151302))
* **ui:** make settings usable on touch and guard unsaved changes ([c0d31a0](https://github.com/infinidysk/infinidysk-rs/commit/c0d31a02027d6c157a1e9b868742e02a19b2477c))


### Build System

* **deps:** bump rust-toolchain from 1.99.0 to 1.99.1 ([#22](https://github.com/infinidysk/infinidysk-rs/issues/22)) ([a65b7d3](https://github.com/infinidysk/infinidysk-rs/commit/a65b7d3ff07a16c8a331890acee0ac8e1362579c))

## [2.0.0-alpha.5](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.4...v2.0.0-alpha.5) (2026-10-23)


### Features

* **health:** remove dead library links and have the Arr search again ([3c42a83](https://github.com/infinidysk/infinidysk-rs/commit/3c42a834bcdcee63733c7abf731f620b60e718b6))
* **health:** repair migrated files that have no download through the Arr that links them ([37ab952](https://github.com/infinidysk/infinidysk-rs/commit/37ab952a03436f1fa25f2087b940a239b1fa2acc))
* **ui:** mark service restarts in the logs ([91db0c2](https://github.com/infinidysk/infinidysk-rs/commit/91db0c25c16d1817bcdf17bacdb2d9e455f8b6a8))
* **ui:** show queue and action-needed counts in the sidebar nav ([8f35a8f](https://github.com/infinidysk/infinidysk-rs/commit/8f35a8ff6c220cd12e211a4ea5d5315ca6a8a195))


### Bug Fixes

* **api:** record owners of earlier imports as soon as the Arr is reachable ([f628994](https://github.com/infinidysk/infinidysk-rs/commit/f62899444241dab426e042797a93a2a71bb33c47))
* **health:** a renamed file no longer counts as replaced ([226ffcb](https://github.com/infinidysk/infinidysk-rs/commit/226ffcb7e8e7994001c55981251085fcfe32bdc5))
* **health:** never delete a migrated file before its Arrs were asked ([7918a36](https://github.com/infinidysk/infinidysk-rs/commit/7918a36e90b7ca8b6cbac444fbcd89a109febc28))
* **import:** bring back skipped migrated files the library still links to ([41a1b8f](https://github.com/infinidysk/infinidysk-rs/commit/41a1b8fb16038ed3df31e301f32dd99029af0ffd))
* **import:** carry over migrated files whose C# segment list lacks an article ([45e9790](https://github.com/infinidysk/infinidysk-rs/commit/45e97908793374dcac2f608c09ba3c5e4f390df8))
* **import:** find the Arr download of migrated files so broken ones get replaced ([5ff3860](https://github.com/infinidysk/infinidysk-rs/commit/5ff38609b6aaeec019c77f8669da474555b3bb27))
* **import:** keep broken migrated files for review instead of deleting them ([8515207](https://github.com/infinidysk/infinidysk-rs/commit/8515207dae52df1f1e0ad0c8557c5d018f67f704))
* **import:** keep broken unowned files for review on installs already migrated from C# ([d9502c3](https://github.com/infinidysk/infinidysk-rs/commit/d9502c34b48723d6e3429cc862501a0f857ba367))
* **import:** list every file the C# import left out ([acc12dd](https://github.com/infinidysk/infinidysk-rs/commit/acc12dda5bead3a86b818a73ea417f9d4dab33a2))
* **import:** recognise library links into the C# mount folder when the mount moved ([7e4477a](https://github.com/infinidysk/infinidysk-rs/commit/7e4477aa0cf3afceb484c284a80696c1b4037e3f))
* **mount:** keep out-of-order kernel reads from resetting the read-ahead ([43bfda5](https://github.com/infinidysk/infinidysk-rs/commit/43bfda5092215f3b074c15adbe8c1ed0b0ffdb10))
* **nntp:** an article the NZB never listed counts as missing ([16e70d6](https://github.com/infinidysk/infinidysk-rs/commit/16e70d67371765ccc738ee2269790670cd0e062b))
* **ui:** reword the setting for broken files not imported by an Arr ([eb269bd](https://github.com/infinidysk/infinidysk-rs/commit/eb269bdbac37b1862cf00b3d92f765a47a596267))
* **ui:** say nothing to reclaim when compacting a database is skipped ([e556f3b](https://github.com/infinidysk/infinidysk-rs/commit/e556f3b447bda8f984dd6ba6547fbd89263ba14e))
* **ui:** show the maintenance window in the server's time zone ([92ec7b7](https://github.com/infinidysk/infinidysk-rs/commit/92ec7b79a7f671683b94dc47296e7665b80a87c7))

## [2.0.0-alpha.4](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.3...v2.0.0-alpha.4) (2026-10-23)


### Features

* **api:** remove unlinked files as a scheduled maintenance job ([a053e12](https://github.com/infinidysk/infinidysk-rs/commit/a053e124476474759d4e58f28d5f6c670a55f3f4))
* **db:** compact the databases in the maintenance window ([6b63569](https://github.com/infinidysk/infinidysk-rs/commit/6b635696dc07246cff0452201d154e8c279fab1a))
* **import:** carry over the C# unlinked-file cleanup schedule ([532b0ab](https://github.com/infinidysk/infinidysk-rs/commit/532b0abc699b4bdc29b6c8ed6005f89a602f6c1e))
* **import:** carry the C# download limit over ([9fa0d2d](https://github.com/infinidysk/infinidysk-rs/commit/9fa0d2d3cdd121736d7f4d85187269a5bb391300))
* **nntp:** cap total download speed from providers ([cf4bfd4](https://github.com/infinidysk/infinidysk-rs/commit/cf4bfd4ada92cd0c1bf58d1001878cfad6efe88e))
* **ui:** download limit under Settings &gt; Network ([e35a7cc](https://github.com/infinidysk/infinidysk-rs/commit/e35a7cc149fcaebfbe408a3b0a1b92fe5c0f06bf))
* **ui:** explain stalls in the recent reads table ([54b8827](https://github.com/infinidysk/infinidysk-rs/commit/54b88273c05e2add20bedde880e9da760fa4e781))
* **ui:** list Maintenance under System in settings ([e444763](https://github.com/infinidysk/infinidysk-rs/commit/e4447639c6a6362bcf06db46edffe40875e3214e))
* **ui:** maintenance page with remove unlinked files ([9f1fd9c](https://github.com/infinidysk/infinidysk-rs/commit/9f1fd9c65c56978cd942cf0f9b307f8961f6b9da))
* **ui:** move live activity to the middle of the sidebar and shorten the version label ([db34c37](https://github.com/infinidysk/infinidysk-rs/commit/db34c3718a3f16998c64b9671545bd595d3411d9))


### Bug Fixes

* **api:** give maintenance schemas unique names and query paging params ([b7b34fe](https://github.com/infinidysk/infinidysk-rs/commit/b7b34fe52843439b0353c58d90e24c41d8f194e6))
* **db:** pause background writes while compacting the database ([206d5bb](https://github.com/infinidysk/infinidysk-rs/commit/206d5bbe810a3ee2b05e126abd9ca21b57c4955b))
* **health:** catch articles that decode but belong to another part of the file ([5a6c3a6](https://github.com/infinidysk/infinidysk-rs/commit/5a6c3a612e233cd455d5c3d79727c5f1c3a57091))
* **ui:** clearer maintenance job description and schedule toggle ([0bd2a33](https://github.com/infinidysk/infinidysk-rs/commit/0bd2a3379ce8ed16ba2290693b164be112108cd2))
* **ui:** connections in use never exceeds the connection limit ([cefd487](https://github.com/infinidysk/infinidysk-rs/commit/cefd487fa3918b6eebb3f758b518353f44b2c0e7))


### Performance Improvements

* **db:** keep query planner statistics fresh ([910ceea](https://github.com/infinidysk/infinidysk-rs/commit/910ceea501a28110cac5907577a131fdd6b1518b))
* **mount:** cap max playback read-ahead memory at 2000 MB ([8f32731](https://github.com/infinidysk/infinidysk-rs/commit/8f327313f78d44961eedf588e14f471c4383fad3))
* **mount:** size preset read-ahead memory for 5, 10 and 20 streams ([13e200a](https://github.com/infinidysk/infinidysk-rs/commit/13e200ac57f4cd9f51e48a45441c91c0c7f52034))


### Refactors

* **health:** call broken files no Arr imported "unowned" ([277392a](https://github.com/infinidysk/infinidysk-rs/commit/277392a91f1de8b2165497bf25f05b7e49a21803))

## [2.0.0-alpha.3](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.2...v2.0.0-alpha.3) (2026-10-22)


### Features

* **queue:** show import progress and time left ([04bbdaf](https://github.com/infinidysk/infinidysk-rs/commit/04bbdafa741b0115e7ee61eec206cbd63716baf3))


### Bug Fixes

* **health:** judge a segment by the message-id playback reads ([071bc50](https://github.com/infinidysk/infinidysk-rs/commit/071bc50ad8e31a0c77b9f4a1c34c81516820b739))
* **mount:** serve modified times in whole seconds ([140449a](https://github.com/infinidysk/infinidysk-rs/commit/140449a30601ea6ae8168e8043f5137bb75b3dd7))
* **queue:** blame the NZB, not the providers, when it lacks a file's first segment ([712ae3d](https://github.com/infinidysk/infinidysk-rs/commit/712ae3d582e909f55b1c720f4495540eb5a0c9e7))
* **queue:** fail NZBs that lack segments without downloading the whole file ([e335620](https://github.com/infinidysk/infinidysk-rs/commit/e335620e49863269f611b8355763f55703d7899c))
* **queue:** import obfuscated RAR sets listed out of order ([2ab7f58](https://github.com/infinidysk/infinidysk-rs/commit/2ab7f58f5fc33ffd517ece3d4aa25c0ca79639af))
* **queue:** leave out an incomplete non-media RAR member instead of failing the job ([747b33b](https://github.com/infinidysk/infinidysk-rs/commit/747b33b893d71cf8c12bfa9ddb7e212a8d3609c5))
* **queue:** wait out the retry backoff when a job's result cannot be written ([01fda5a](https://github.com/infinidysk/infinidysk-rs/commit/01fda5abcda7517af9739c1148fe3f6a4fb65a26))
* **sab:** accept the speedlimit command and ignore it ([3a6f299](https://github.com/infinidysk/infinidysk-rs/commit/3a6f299f8074409946293169bf5d31fbcb008a35))
* **stream:** report a corrupt segment even after the reader streamed past it ([604b33d](https://github.com/infinidysk/infinidysk-rs/commit/604b33d8d61f9cf7f76d2abba6890b63eb6f88ca))
* **ui:** keep the explorer video player inside its window ([82e8fe2](https://github.com/infinidysk/infinidysk-rs/commit/82e8fe27bc14ff79bf9456473c3458e597c1246c))


### Performance Improvements

* **queue:** parse, hash and compress NZBs off the async runtime ([70629e9](https://github.com/infinidysk/infinidysk-rs/commit/70629e9c0b7da3256d4eadfb82910a091369465b))

## [2.0.0-alpha.2](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.1...v2.0.0-alpha.2) (2026-10-22)


### Bug Fixes

* **import:** read C# times in the container's time zone ([9bffcec](https://github.com/infinidysk/infinidysk-rs/commit/9bffcecfeca8a1b2fe6f9cbea5d42e53d20d66a8))
* **stream:** hedge a slow last segment ([0590cac](https://github.com/infinidysk/infinidysk-rs/commit/0590cac4732589d73551bf4fbc5f63e4aeafdd58))


### Performance Improvements

* **nntp:** send hedges only to idle connections ([8f5484a](https://github.com/infinidysk/infinidysk-rs/commit/8f5484aad62545170c483f9ae11f8c4be92bac00))
* **stream:** read 60 MB ahead by default ([e87dfc0](https://github.com/infinidysk/infinidysk-rs/commit/e87dfc07b68c57fd17ea2543d71e10765b438269))

## [2.0.0-alpha.1](https://github.com/infinidysk/infinidysk-rs/compare/v2.0.0-alpha.0...v2.0.0-alpha.1) (2026-10-21)


### ⚠ BREAKING CHANGES

* **db:** databases created by earlier pre-releases no longer open. Back up the config directory (database, -wal and metrics files), then start with a new database.

### Features

* **api:** add DB-backed settings with an authoritative env overlay, catalog providers and arr instances ([f2cc1c2](https://github.com/infinidysk/infinidysk-rs/commit/f2cc1c2715042d26d470139f508897d34f24bf68))
* **api:** add per-file read history, most-read files and workload split ([7b2e733](https://github.com/infinidysk/infinidysk-rs/commit/7b2e733fc791ae71e75ac291b18ff2526bf4ff30))
* **api:** add provider speed test and pipeline auto-tune ([0ca4c0d](https://github.com/infinidysk/infinidysk-rs/commit/0ca4c0d2bd2c70839c3835556bbb7ab853722d97))
* **api:** add public URL setting under network ([07859b8](https://github.com/infinidysk/infinidysk-rs/commit/07859b8063290768ab7f7e385bce03a7effaccef))
* **api:** add read, import, health and cache trends and usage by app to the overview ([62ae570](https://github.com/infinidysk/infinidysk-rs/commit/62ae5700e6f725bdd87b1cfb06ca19abbc1db359))
* **api:** admin API with OpenAPI document and live settings ([8388929](https://github.com/infinidysk/infinidysk-rs/commit/83889290f9108bcb4f8144e585fcee47aaa0e6b5))
* **api:** discover which Arr download clients point at this installation ([e85bbe6](https://github.com/infinidysk/infinidysk-rs/commit/e85bbe66640a4e29f358cdffc69f590c6bd6d18f))
* **api:** download queued and finished NZBs and retry failed jobs from the queue page ([bc78444](https://github.com/infinidysk/infinidysk-rs/commit/bc7844460032d098e5a94fd0e2dfc14402b48b3f))
* **api:** explorer API with signed player links ([aced729](https://github.com/infinidysk/infinidysk-rs/commit/aced7298375c51d8f5817acafe35fd3af253625c))
* **api:** export redacted traces for bug reports ([2439503](https://github.com/infinidysk/infinidysk-rs/commit/2439503a6e877b05a7dfa07e09211d2fcdb683b5))
* **api:** keep logs across restarts ([6084a64](https://github.com/infinidysk/infinidysk-rs/commit/6084a64a9105eaa6f9fd858ab74dd9a8478f8ca0))
* **api:** list rejected releases and report stuck counts on queued jobs ([62e54bd](https://github.com/infinidysk/infinidysk-rs/commit/62e54bd043c4a9e8bf2f8767ff212647d838a138))
* **api:** live updates over server-sent events ([d1b72cc](https://github.com/infinidysk/infinidysk-rs/commit/d1b72cc359649f8e9785169e9921c623e692916e))
* **api:** provider download quotas with monthly or block resets, and a timed speed test ([9bd788c](https://github.com/infinidysk/infinidysk-rs/commit/9bd788c407b3e4d6922d483bcc82e7bd598a1ebe))
* **api:** record usage metrics with live samples, overview history and Prometheus export ([c5559ec](https://github.com/infinidysk/infinidysk-rs/commit/c5559ec8713b595562cc8c3570419503f4eb253b))
* **api:** report container CPU and memory in live metrics ([9e54070](https://github.com/infinidysk/infinidysk-rs/commit/9e54070c7ba8f39566bbf81ada52e16058210b7a))
* **api:** reveal stored API and download keys in settings ([7632acc](https://github.com/infinidysk/infinidysk-rs/commit/7632acc5f967dd090276267688f47dd1496d1275))
* **api:** richer overview metrics with per-provider series, records, catalogue and live minute totals ([33ef72b](https://github.com/infinidysk/infinidysk-rs/commit/33ef72b53c1777b68ca5aa981c55c494080811a1))
* **api:** run the server with the SAB API and WebDAV on one port, the queue worker and the FUSE mount ([928aa63](https://github.com/infinidysk/infinidysk-rs/commit/928aa632e160d2dbe6b9c73cefcec8f955ef29de))
* **api:** toggle read tracing and download traces ([f35b08e](https://github.com/infinidysk/infinidysk-rs/commit/f35b08e43edb9eb6390af0d0caf6d7114c9de379))
* **api:** trust local network ranges as reverse proxies by default ([2a692c0](https://github.com/infinidysk/infinidysk-rs/commit/2a692c0aa15ee0225f600e06bbfa1fbea849441b))
* **api:** upload NZBs into a category through the admin API ([4f02113](https://github.com/infinidysk/infinidysk-rs/commit/4f02113bf1b4c4c26b2d1e7c5b266f8dd5912be0))
* **archive:** add a sans-I/O RAR4/RAR5 header scanner that maps members to byte ranges ([2766c66](https://github.com/infinidysk/infinidysk-rs/commit/2766c6642453fab4343d8543797255cc04745bc4))
* **archive:** read 7z headers, including LZMA-encoded ones, and map stored members to byte ranges ([54d909f](https://github.com/infinidysk/infinidysk-rs/commit/54d909f28a23014cfb5261bed9747f349b1e0901))
* **arr:** clear stuck imports from Arr queues ([ba6c3fe](https://github.com/infinidysk/infinidysk-rs/commit/ba6c3fe201548cec6e0e29e51bfb4cbdb95477d3))
* **arr:** reconcile owned files against each Arr's library daily ([909b2e6](https://github.com/infinidysk/infinidysk-rs/commit/909b2e60175a6ecacd60f894e8864f993bb48889))
* **arr:** record owners of earlier imports from each Arr's history ([296ed62](https://github.com/infinidysk/infinidysk-rs/commit/296ed62644c284d202eb8a9fafb716133f5dfa85))
* **arr:** register webhooks and delete items their Arr deleted ([f6a0976](https://github.com/infinidysk/infinidysk-rs/commit/f6a097676ec79d24da6ab6cc0a1d27516d207772))
* **auth:** admin account with argon2id, signed session cookies and an admin API key ([f4092cf](https://github.com/infinidysk/infinidysk-rs/commit/f4092cf26e97ff864999aae13f7917ba27ed0d9b))
* **auth:** let sign-in be required always, off for local addresses, or off ([1ab8f93](https://github.com/infinidysk/infinidysk-rs/commit/1ab8f931d060b6bb8dc205ace4785c4c7d6307e5))
* **core:** add item ids, file metadata and the versioned zstd blob format ([97813f9](https://github.com/infinidysk/infinidysk-rs/commit/97813f9c48720b1ea3deb6eccae2080b1cc958eb))
* **db:** add SQLite and PostgreSQL storage for items, blobs and config ([6d0174b](https://github.com/infinidysk/infinidysk-rs/commit/6d0174b3321473edae2f92f6b4c4ce9d40f69602))
* **db:** add the metrics store with minute, hour and lifetime rollups ([bc408cc](https://github.com/infinidysk/infinidysk-rs/commit/bc408cc921d3b31cfc18a9f9d13778acf48a018e))
* **db:** broadcast queue, history and health changes ([149b40f](https://github.com/infinidysk/infinidysk-rs/commit/149b40f7f1e79ca35321bb972fd1943ab390b1d7))
* **db:** health check schedule and results ([893bb4d](https://github.com/infinidysk/infinidysk-rs/commit/893bb4d97b692e09ce102f837e70327fa55372d1))
* **db:** keep unreferenced NZBs for a retention window and prune old history ([f468df1](https://github.com/infinidysk/infinidysk-rs/commit/f468df1c1ac7ce33b8c9d6d84a808873bde4ceae))
* **db:** list recent health check results ([77dde54](https://github.com/infinidysk/infinidysk-rs/commit/77dde54ed6030edd3711a88233c148af9c2c6c81))
* **db:** paged, filtered file queries for the explorer ([913862d](https://github.com/infinidysk/infinidysk-rs/commit/913862d1f98fb43812497c20ce89c7ebf6aed1e0))
* **db:** query id shards, completed download dirs and remove queued jobs ([2ff1943](https://github.com/infinidysk/infinidysk-rs/commit/2ff19431f48a7bcfbd21b3867caa661d58e859ab))
* **db:** record which item each finished read was of ([46b3560](https://github.com/infinidysk/infinidysk-rs/commit/46b3560ffb91189504bdee5976e875a408732ba8))
* **db:** squash migrations into one initial schema ([5dfb260](https://github.com/infinidysk/infinidysk-rs/commit/5dfb26010941546a04a4e92b0d5b008f1de9b474))
* **db:** store queue and history ([b7585eb](https://github.com/infinidysk/infinidysk-rs/commit/b7585eb873cebda688be634a43e89b0dc827f3b0))
* **db:** store the single admin login in accounts ([d212af1](https://github.com/infinidysk/infinidysk-rs/commit/d212af16e34a7893f3b0231a7f0b85ab3bf9c956))
* **health:** analyze media with ffprobe and repair broken MP4 containers ([f2f2d5e](https://github.com/infinidysk/infinidysk-rs/commit/f2f2d5e87c9dcb49fab3619d982692eebe327f22))
* **health:** choose the check sample size with a slider ([f8b6fe7](https://github.com/infinidysk/infinidysk-rs/commit/f8b6fe7c976d360ad0f37b90f4e34f32d74e8abd))
* **health:** find the Arr that imported a STRM handoff ([ce39c04](https://github.com/infinidysk/infinidysk-rs/commit/ce39c04f02acf60c079146c8155030451c973782))
* **health:** report playback damage, hide replaced files and retry with 503 ([96a42f1](https://github.com/infinidysk/infinidysk-rs/commit/96a42f1e384fef3ce754d0d869fde182053f2823))
* **health:** share a durable per-media search limit ([ad40175](https://github.com/infinidysk/infinidysk-rs/commit/ad4017522d12c39858773aa49573d48f85f9fa11))
* **health:** show the check schedule and link checked files to the explorer ([175932f](https://github.com/infinidysk/infinidysk-rs/commit/175932f8753da0b67731384fa18bf152deb7843d))
* **health:** STAT health checks with Sonarr/Radarr repair ([0305f9d](https://github.com/infinidysk/infinidysk-rs/commit/0305f9dd906e3633323790f4aa083186d77bc45e))
* **health:** two-strike checks, damage classification and Arr replacement ([afc3862](https://github.com/infinidysk/infinidysk-rs/commit/afc3862063aba486e585e3ba0e5430d4fca206a4))
* **import:** accept C# admin and WebDAV passwords at the first sign-in ([894d69d](https://github.com/infinidysk/infinidysk-rs/commit/894d69dcdaa136a2336265219f92901ced530641))
* **import:** carry a C# install's library, history and settings over on first boot ([e396fb8](https://github.com/infinidysk/infinidysk-rs/commit/e396fb86d82f292d17611cfcc5748775f1779b8b))
* **import:** carry C#'s STRM setup over and keep its STRM links playing ([db6dbe0](https://github.com/infinidysk/infinidysk-rs/commit/db6dbe018126d75fddaad83c0941f0a843c65dd8))
* **import:** convert the library when the import strategy, STRM key, public URL or mount changes ([a13015d](https://github.com/infinidysk/infinidysk-rs/commit/a13015d902f4bcaac0bdecfa648dbe6d0a8960d4))
* **import:** keep Arrs waiting when the upgrade from C# fails ([1c8d406](https://github.com/infinidysk/infinidysk-rs/commit/1c8d406c1d1d3aa17f7ace0bae9bb08b0429d45a))
* **import:** map C# settings onto ours in the upgrade report ([6415982](https://github.com/infinidysk/infinidysk-rs/commit/6415982da0e0d54b419d904a4b5776df8865f32b))
* **import:** report what importing a C# /config would carry over ([ff64ded](https://github.com/infinidysk/infinidysk-rs/commit/ff64ded8f1e695d25587bc6856a5b099cee16ea8))
* **mount:** log FUSE requests and mount lifecycle ([8208933](https://github.com/infinidysk/infinidysk-rs/commit/820893310b5721c6ea414b6b1c365c54a5822efa))
* **mount:** mount the tree read-only over FUSE with remount backoff and kernel tuning ([8ef1eff](https://github.com/infinidysk/infinidysk-rs/commit/8ef1eff60ace8a27cf52d8eac6e52e64e66f85e7))
* **mount:** name FUSE readers by process, pid and uid/gid ([e08c550](https://github.com/infinidysk/infinidysk-rs/commit/e08c550eafb95f697e659789e643eb70b7a5632c))
* **nntp:** add community-maintained provider catalog compiled from providers/*.toml ([4f8562c](https://github.com/infinidysk/infinidysk-rs/commit/4f8562cc3fbec64ec2e612e2234de94f247b0a2b))
* **nntp:** add Easyusenet and more provider servers to the catalog ([ffe6eca](https://github.com/infinidysk/infinidysk-rs/commit/ffe6eca739ae4f2b5936a6c5de781a199cd57c52))
* **nntp:** add implicit TLS, pipelined BODY and zero-copy streaming decode ([5a44715](https://github.com/infinidysk/infinidysk-rs/commit/5a447155e7ada2d8397974691c450eddf1caec68))
* **nntp:** add missing provider servers to the catalog ([47a13a1](https://github.com/infinidysk/infinidysk-rs/commit/47a13a1e6ae48c761e1451968ac594b042325f50))
* **nntp:** add pipelined per-provider connection pool with global download permits ([7370caa](https://github.com/infinidysk/infinidysk-rs/commit/7370caaf16f4ceecc0aa95bd3824a90a8c7ec9de))
* **nntp:** add pipelined STAT and a keepalive warm connection floor ([c50587a](https://github.com/infinidysk/infinidysk-rs/commit/c50587adc17d18388222af68d6fd13b3bd77d2d2))
* **nntp:** count breaker trips and provider rescues, trace reply latency and decode ([97fda95](https://github.com/infinidysk/infinidysk-rs/commit/97fda9596a454ba90af7864d30eb388fad41f866))
* **nntp:** count provider requests and trace segment fetches ([b13ffb0](https://github.com/infinidysk/infinidysk-rs/commit/b13ffb0d20acf827064eaffbf9c0467fa888ef15))
* **nntp:** fail over across providers with per-provider circuit breakers and connection-limit throttling ([9506ed2](https://github.com/infinidysk/infinidysk-rs/commit/9506ed2c42541e90b5f85a816093bd000d64460e))
* **nntp:** fetch and decode yEnc article bodies over one connection ([8e91ff0](https://github.com/infinidysk/infinidysk-rs/commit/8e91ff006ba0931a4d6693313cd715471b2df761))
* **nntp:** let diagnostics take a provider exclusively and yield to playback ([b0e3696](https://github.com/infinidysk/infinidysk-rs/commit/b0e369663dc4843a44da208a0f5710cd9a9cb29b))
* **nntp:** log provider outages and connection limits once as warnings ([337b6bf](https://github.com/infinidysk/infinidysk-rs/commit/337b6bf1aa831e697a6025c0213ea9cb9c5c8378))
* **nntp:** meter provider downloads and skip providers whose quota is used up ([37bfa08](https://github.com/infinidysk/infinidysk-rs/commit/37bfa081e8399985069c63e4420ad0fdbbd5100e))
* **nntp:** open connections ahead of demand with prewarm ([0f11843](https://github.com/infinidysk/infinidysk-rs/commit/0f11843decbd4c4112d28d691de7d8599974cdae))
* **nntp:** route by provider priority levels and skip missed backbones ([59b3c70](https://github.com/infinidysk/infinidysk-rs/commit/59b3c70c78df4c2a57541ee7f12bd8f2bde4e7c3))
* **nntp:** skip providers that recently lacked an article ([4ae340e](https://github.com/infinidysk/infinidysk-rs/commit/4ae340ea3852dab4495ede1c0a23063fc8829a9b))
* **par2:** read PAR2 file descriptions and slice checksums from partial files and verify slices ([2332d96](https://github.com/infinidysk/infinidysk-rs/commit/2332d96a4bbb498bf8ef9626c90142515550edee))
* **queue:** count imports and health check results since start ([0de7c93](https://github.com/infinidysk/infinidysk-rs/commit/0de7c93d4efa8deaf1b1165d468a55f711b69b46))
* **queue:** expand stored archives nested inside archives ([769ac15](https://github.com/infinidysk/infinidysk-rs/commit/769ac15cf7264b513cc06cf1be7b69b176b3d32e))
* **queue:** fail full disc releases in video categories ([0966e12](https://github.com/infinidysk/infinidysk-rs/commit/0966e1258790ab6fb56882a8a73428389a07a29c))
* **queue:** fail releases with missing or invalid media and enforce the owning Arr's media type ([f3d6dcf](https://github.com/infinidysk/infinidysk-rs/commit/f3d6dcf739c16a3c2e84e0bce9829584a1e794b3))
* **queue:** hand jobs off as STRM files in STRM mode ([0c53e51](https://github.com/infinidysk/infinidysk-rs/commit/0c53e516f996bfbc53cba993b198da24ad09f87b))
* **queue:** import and stream AES-encrypted RAR and 7z members ([cb1b56d](https://github.com/infinidysk/infinidysk-rs/commit/cb1b56d6f61b2033bfd454863bb8550e166f5edf))
* **queue:** import queued NZBs into /content with PAR2 names, archive members and STAT sampling ([34a0a28](https://github.com/infinidysk/infinidysk-rs/commit/34a0a284716e6677203a340f04ee02d9dacf6a9f))
* **queue:** import two jobs at once and drop results of jobs removed mid-import ([f5549ab](https://github.com/infinidysk/infinidysk-rs/commit/f5549abd533624f4c80e7e543c8f4bbf118d2866))
* **queue:** name obfuscated files after their release folder ([86fb11e](https://github.com/infinidysk/infinidysk-rs/commit/86fb11e0101f2540958af771205387b113772626))
* **queue:** parse NZBs with SABnzbd's acceptance rules, including gzip and namespaced files ([382d2a7](https://github.com/infinidysk/infinidysk-rs/commit/382d2a7a68039cfa23fa939ae901879f07911161))
* **queue:** pause the queue or single jobs, configure concurrency and back off retries ([b709cc8](https://github.com/infinidysk/infinidysk-rs/commit/b709cc81e8992a655f2b1db1bff63ba78571fd81))
* **queue:** requeue imports that stall and fail them on the third stall ([d55db62](https://github.com/infinidysk/infinidysk-rs/commit/d55db622bd1813e51129c9f4452a833643df68d3))
* **queue:** return the queued job for a duplicate NZB and record refused NZBs as failed ([141d3fb](https://github.com/infinidysk/infinidysk-rs/commit/141d3fbc6691e71847635c45f0dc258753b1bf80))
* **queue:** store Windows-safe file and folder names ([fb77634](https://github.com/infinidysk/infinidysk-rs/commit/fb77634a459862f3e603f19de12ee131a3b5f139))
* **sab:** add NZBs by URL with mode=addurl ([2d8d515](https://github.com/infinidysk/infinidysk-rs/commit/2d8d515131f5ff6963a858764cbc2f17b4ba472b))
* **sab:** identify as SABnzbd when fetching NZBs ([354de13](https://github.com/infinidysk/infinidysk-rs/commit/354de1329cec1a6f50b1ac65f1a582473040e94f))
* **sab:** retry failed jobs from their stored NZB ([78e31b1](https://github.com/infinidysk/infinidysk-rs/commit/78e31b10dd2974c23208656239b1cb0b14ba9b86))
* **sab:** serve the SABnzbd API for adding NZBs, queue and history ([0fd706b](https://github.com/infinidysk/infinidysk-rs/commit/0fd706b3594d154e65c6534c6be17f66eb95f073))
* **sab:** tell owning Arrs about finished jobs ([0b5301d](https://github.com/infinidysk/infinidysk-rs/commit/0b5301da3fe5d4be56819c00b4f12396c2e4b964))
* **stream:** add a shared segment cache and performance settings ([6f161b5](https://github.com/infinidysk/infinidysk-rs/commit/6f161b5789d8e41d60684e6e912570a4be56c6b9))
* **stream:** add seekable segment reader with ordered prefetch ([b6136ff](https://github.com/infinidysk/infinidysk-rs/commit/b6136ff09643eb7b6e1e70bbcfb02f13e3462e31))
* **stream:** bound decoded bytes in flight with a shared in-order budget ([b182f16](https://github.com/infinidysk/infinidysk-rs/commit/b182f16db650290da9fca4301051d35db7a2b137))
* **stream:** fill missing MPEG-TS segments with null packets ([d181da0](https://github.com/infinidysk/infinidysk-rs/commit/d181da02f1446223bcc7cd9852207cf51f069c29))
* **stream:** stop playback past too much missing data ([d19df78](https://github.com/infinidysk/infinidysk-rs/commit/d19df781036f7a2e6308ee6d4019c0e5974e1b3e))
* **stream:** trace read-ahead window, connection queueing, hedge wins and drained replies ([75f4110](https://github.com/infinidysk/infinidysk-rs/commit/75f41107f38319e1a90b3c57ac603798343cc0be))
* **ui:** add a performance settings page ([5e366d2](https://github.com/infinidysk/infinidysk-rs/commit/5e366d2906907187df92b922908f2cfee4ec03f9))
* **ui:** add Network settings with trusted proxies ([fd7f832](https://github.com/infinidysk/infinidysk-rs/commit/fd7f832f09eadb46e11cf1aa95354ef146591d78))
* **ui:** add NZBs from the queue page with a chosen category ([2f8a77e](https://github.com/infinidysk/infinidysk-rs/commit/2f8a77e8c6c0b18221e263f9c5d427f4223a68c6))
* **ui:** add provider and settings screens ([9b7bbdf](https://github.com/infinidysk/infinidysk-rs/commit/9b7bbdff80a91dc8fc1be901277b36a9987d757a))
* **ui:** add provider speed test and auto-tune pipeline actions ([20cee2e](https://github.com/infinidysk/infinidysk-rs/commit/20cee2ef231083196ffe886f94825e724978b5a2))
* **ui:** add sign-in requirement to Settings &gt; Authentication ([7d19457](https://github.com/infinidysk/infinidysk-rs/commit/7d194579a9286c4bd713f1e16b7b6a14d66db727))
* **ui:** add sign-in, queue, history and health screens ([3dde553](https://github.com/infinidysk/infinidysk-rs/commit/3dde553032637418b3a8dc9dd8f619000d295423))
* **ui:** add Sportarr instances, move providers under settings as NNTP Providers and settings below system ([f5ca148](https://github.com/infinidysk/infinidysk-rs/commit/f5ca14845df520c19287ea85484c9030f76093b4))
* **ui:** add the overview dashboard, Prometheus toggle and trace export ([120f586](https://github.com/infinidysk/infinidysk-rs/commit/120f5867fb85d2591cc3a4f7c755656097064584))
* **ui:** chip input for download client categories ([d1abf21](https://github.com/infinidysk/infinidysk-rs/commit/d1abf215174f95f25105e39e063cb6134b3f2ebc))
* **ui:** choose between symlinks and STRM files on the Imports page ([922ee60](https://github.com/infinidysk/infinidysk-rs/commit/922ee6029229fc8d48d828147a29350a5ba74da6))
* **ui:** collapse subtitle tracks into one media line ([aa72376](https://github.com/infinidysk/infinidysk-rs/commit/aa723767737cde0dc28758ffe8cae02d4233d921))
* **ui:** confirm the admin password during first-time setup ([a0ffd6b](https://github.com/infinidysk/infinidysk-rs/commit/a0ffd6bda113b67b90916a76eeab69c47157c91e))
* **ui:** download a support pack from Settings &gt; Support ([50f01c5](https://github.com/infinidysk/infinidysk-rs/commit/50f01c5f2c1f88a3503e1ce4ca2711328e67e544))
* **ui:** edit extra blocked filename patterns in import settings ([99aa1a9](https://github.com/infinidysk/infinidysk-rs/commit/99aa1a92c87d053c073fb668b3e53b35d7203f22))
* **ui:** filter the Explorer by encryption and show it in file details ([53f86b9](https://github.com/infinidysk/infinidysk-rs/commit/53f86b925486be705aefbdcded94aadeb88504c7))
* **ui:** group repeated reads of a file into one playback row ([f43890c](https://github.com/infinidysk/infinidysk-rs/commit/f43890ccc5c0c224454f83e881ffe8016cc72285))
* **ui:** group settings into platform, automation and system sections and rename account to authentication ([7ed9cd5](https://github.com/infinidysk/infinidysk-rs/commit/7ed9cd500659863534108cad34edfd7fe8eff9b7))
* **ui:** health monitoring settings, state filters and needs-attention list ([69650ef](https://github.com/infinidysk/infinidysk-rs/commit/69650ef5ebcf2838fbe219bb2a3bfe7a1f8b24a5))
* **ui:** hide system folders in the explorer tree behind a toggle ([8beb323](https://github.com/infinidysk/infinidysk-rs/commit/8beb323b25ad1cad34b29f79c8d3848539374042))
* **ui:** in-browser player with recovery and playback diagnostics ([03e97a6](https://github.com/infinidysk/infinidysk-rs/commit/03e97a6285f1abecb0f28dc347b1e50586999930))
* **ui:** library explorer with tree, filters, details and removal preview ([066d097](https://github.com/infinidysk/infinidysk-rs/commit/066d09752e61fccc82acbceb4952a03318957a5f))
* **ui:** link completed queue jobs to their files in the explorer ([ebb9506](https://github.com/infinidysk/infinidysk-rs/commit/ebb9506bfc2c2a7441cc843d6b501d3e28200309))
* **ui:** list overview providers in failover order ([aef2c72](https://github.com/infinidysk/infinidysk-rs/commit/aef2c728473ce4a91e569b8714e9c2cda155089b))
* **ui:** mute release details after the year in item names ([41421a1](https://github.com/infinidysk/infinidysk-rs/commit/41421a182ac622f7eb7ee92f28a308480fe90c0e))
* **ui:** pause and resume the queue and jobs, set import concurrency ([ce968b3](https://github.com/infinidysk/infinidysk-rs/commit/ce968b3b9d93d4469c9fa7d474248e9695196277))
* **ui:** preview and follow library conversions from the Imports page ([9344473](https://github.com/infinidysk/infinidysk-rs/commit/93444730bce260180445d443b9da7cb031c9aad5))
* **ui:** provider quota settings, usage bars, usage reset and timed test progress ([e443c88](https://github.com/infinidysk/infinidysk-rs/commit/e443c88059edd319a397b2529db9b056287d00ad))
* **ui:** redesign the overview dashboard with activity, provider, rescue, record and catalogue panels ([6a32933](https://github.com/infinidysk/infinidysk-rs/commit/6a32933a7b346eddb7e69058da6be4d71fd46bc8))
* **ui:** replace the logo with a simpler vector mark ([2009428](https://github.com/infinidysk/infinidysk-rs/commit/200942852f764f982ccee1ce09d441c112a1926f))
* **ui:** restyle with shadcn and the logo palette ([b58c09c](https://github.com/infinidysk/infinidysk-rs/commit/b58c09cb11f40ad7f571c768c5be7aba77e16df3))
* **ui:** rework overview layout, chart controls and stat hierarchy ([02c349a](https://github.com/infinidysk/infinidysk-rs/commit/02c349aa8d62af73f311852883fa0cebc9944f4d))
* **ui:** say on the health page when routine checks are off ([23a2370](https://github.com/infinidysk/infinidysk-rs/commit/23a23702393bd33878b0ff44e915ce21732a319e))
* **ui:** serve the embedded web UI ([05be499](https://github.com/infinidysk/infinidysk-rs/commit/05be499ea9c8c53fd2750ed6c19b6afd4b808190))
* **ui:** show container CPU and memory on the System page ([29c60e4](https://github.com/infinidysk/infinidysk-rs/commit/29c60e4337e9c27898084cf396b643c1fc4e7021))
* **ui:** show each Arr's SABnzbd clients and whether they point here ([16d1a25](https://github.com/infinidysk/infinidysk-rs/commit/16d1a25d66792f03aafa64792efc0faf83784f80))
* **ui:** show history below the queue and drop the history page ([8650959](https://github.com/infinidysk/infinidysk-rs/commit/8650959176e672f2c2fd51c2bfe25f413390e9fa))
* **ui:** show import errors in a details dialog and quiet queue rows ([ce14517](https://github.com/infinidysk/infinidysk-rs/commit/ce14517333b2b756ca5b5bdc8564a6a0f1711824))
* **ui:** show media analysis and broken containers in the explorer ([86c2fc3](https://github.com/infinidysk/infinidysk-rs/commit/86c2fc322d25f240799fdfbdc0d2251e5aee7171))
* **ui:** show mount status and recent logs ([750d559](https://github.com/infinidysk/infinidysk-rs/commit/750d55906849f786ac364099e44cdeafb55a0ac8))
* **ui:** show shared backbones and failover order on the providers page ([3d6985e](https://github.com/infinidysk/infinidysk-rs/commit/3d6985ec2507cf445d649826798bb62b60587748))
* **ui:** show streaming quality, provider tail latency, workload cost and read history ([4ef84b5](https://github.com/infinidysk/infinidysk-rs/commit/4ef84b5f302e8bc60f7dbb31b0f6f3a7a05a9ea6))
* **ui:** show the segment cache budget on the memory bar ([0a60ff0](https://github.com/infinidysk/infinidysk-rs/commit/0a60ff034c61b12f5ff454f644eed25eaf128396))
* **ui:** show trends, usage by app, rejected releases, stall counts and import durations ([156e28c](https://github.com/infinidysk/infinidysk-rs/commit/156e28c1e09158e3fa7db58ac93f613d418d731a))
* **ui:** show version and live activity in the sidebar ([f7599f8](https://github.com/infinidysk/infinidysk-rs/commit/f7599f820cf34bb063e3ba8d989188783ccb6353))
* **ui:** show when a newer build is available on main ([9b29f42](https://github.com/infinidysk/infinidysk-rs/commit/9b29f424f64c8f1a4dac541c4059f9d2d01bbe7c))
* **ui:** split segment cache from app memory on the System page ([fbef5ad](https://github.com/infinidysk/infinidysk-rs/commit/fbef5ad37c58f7b7124ccd1152ef79bb2c5f05b7))
* **ui:** split settings into a page per category ([dab0fbb](https://github.com/infinidysk/infinidysk-rs/commit/dab0fbb3468dbe1ae2c9dd131b0252ef14188033))
* **ui:** test an Arr connection from Settings &gt; Arr instances ([7cc14e0](https://github.com/infinidysk/infinidysk-rs/commit/7cc14e0181d8ef4651878c6245fdcd52f683c10c))
* **ui:** trace reads and show where playback stalled ([a0fb803](https://github.com/infinidysk/infinidysk-rs/commit/a0fb80399f679d1cc0cd3629a4e5447c4a3a1116))
* **ui:** view the C# import report again from Settings &gt; Support ([9136d4a](https://github.com/infinidysk/infinidysk-rs/commit/9136d4a38fd0561f03fcf73669a064d92aaa0fdc))
* **vfs:** list open reads and trace them on demand ([2214698](https://github.com/infinidysk/infinidysk-rs/commit/221469846c65b034422d8aef22d236db4c37d2e3))
* **vfs:** serve /content, /.ids, /completed-symlinks and /nzbs with multipart readers ([b638aa5](https://github.com/infinidysk/infinidysk-rs/commit/b638aa53a1b6dc75fed8613d3051bd3c735ec365))
* **webdav:** serve the tree over WebDAV and /view with ranges, Basic auth and NZB drop-in ([a479346](https://github.com/infinidysk/infinidysk-rs/commit/a47934638186d1b6fafc96ce098e0c465fd177cf))
* **webdav:** show the client address in read history ([3232f32](https://github.com/infinidysk/infinidysk-rs/commit/3232f3213de8bd3642039af7cc0f35fd8e47e76a))
* **yenc:** add incremental decode that stops at yEnc control lines and the NNTP terminator ([4739e20](https://github.com/infinidysk/infinidysk-rs/commit/4739e20247655c438f9e4c244fdecb2800dfd522))
* **yenc:** add rapidyenc-backed yEnc decode, encode and CRC32 crate ([c8b9928](https://github.com/infinidysk/infinidysk-rs/commit/c8b99281acc3e0b566f27f555af746a3e158f9b0))


### Bug Fixes

* **api:** check for newer releases instead of newer commits on main ([1ca624f](https://github.com/infinidysk/infinidysk-rs/commit/1ca624fed3ef755db52b41e1097321fca3333cd1))
* **api:** count only failures the caller sees as errors ([479e234](https://github.com/infinidysk/infinidysk-rs/commit/479e2342dbde6136414824037da05682b55834dd))
* **api:** decimal performance presets that keep at least the previous byte budgets ([338e982](https://github.com/infinidysk/infinidysk-rs/commit/338e982f31abeffd77c6ebd582a3b061bd086d4b))
* **api:** group read usage by the app's first word ([#15](https://github.com/infinidysk/infinidysk-rs/issues/15)) ([ad4964d](https://github.com/infinidysk/infinidysk-rs/commit/ad4964df3dc7ac7361a17b582f13fe338e4821ef))
* **api:** load library folders saved before they gained an Arr path ([349803c](https://github.com/infinidysk/infinidysk-rs/commit/349803c74dca9f53d4d1c6a9be4b2d81c855cdd2))
* **api:** no trailing dot on links converted from STRM files for extensionless items ([cfd9948](https://github.com/infinidysk/infinidysk-rs/commit/cfd9948ecefef06c2a82b0bd9dd248045d0b7e33))
* **api:** read the player filter of /reads from the query string ([dbb4998](https://github.com/infinidysk/infinidysk-rs/commit/dbb49983d5f7e49d03d42066ae7f4909a2012d47))
* **api:** say the server keeps no copy of a support pack ([0015264](https://github.com/infinidysk/infinidysk-rs/commit/0015264243b1840b5888e6afb34bb0ce1345f950))
* **api:** start with defaults instead of failing on an out-of-range stored setting ([25fd6eb](https://github.com/infinidysk/infinidysk-rs/commit/25fd6eb4e39f97287bee4dd9a8cfaaf731bf45fa))
* **api:** write the support pack generated_at as an RFC 3339 time ([495ad31](https://github.com/infinidysk/infinidysk-rs/commit/495ad31b15811ed0872caf13e8e3f373c6fbc8eb))
* **arr:** reconcile only after the first discovery found each Arr's clients ([b5db573](https://github.com/infinidysk/infinidysk-rs/commit/b5db573fd9614d1134587f1d0bbdbbb84beadcf5))
* **auth:** say the username or password is wrong when sign-in fails ([9810fb4](https://github.com/infinidysk/infinidysk-rs/commit/9810fb4bb0e0ccdbeefa6f7b375614e49383679f))
* **db:** delete a job folder emptied through its subfolders ([794a4f3](https://github.com/infinidysk/infinidysk-rs/commit/794a4f3b9cedeb5188e28a98aec090b8811b8f79))
* **db:** delete job folders left empty when their last file is deleted ([f139331](https://github.com/infinidysk/infinidysk-rs/commit/f139331e69b219585527b474b0b9109704d8e503))
* **db:** keep metrics stores that fail migration and quarantine corrupt ones ([9c11cfb](https://github.com/infinidysk/infinidysk-rs/commit/9c11cfb4566428474d32695960397ee14cbc3ce4))
* **deps:** update rustls to 0.23.46 for RUSTSEC-2026-0346..0349 ([4553728](https://github.com/infinidysk/infinidysk-rs/commit/4553728a6e07eee04af2401d6935c809fe27017f))
* **deps:** update rustls-webpki and webpki-roots for distrusted CA advisories ([d978bd1](https://github.com/infinidysk/infinidysk-rs/commit/d978bd1d410bf23a6ada3c7f66e26b8e7cffcc83))
* **health:** keep items when an Arr reports a file missing from disk ([afb4115](https://github.com/infinidysk/infinidysk-rs/commit/afb411546fdc37cddfa53c5bd15e02b728d418ce))
* **health:** keep repair owner lookup working after SAB history deletion ([79379db](https://github.com/infinidysk/infinidysk-rs/commit/79379db84fc1c10950b2d736495d66adb349381a))
* **health:** never replace a file while it is being watched ([8916e5a](https://github.com/infinidysk/infinidysk-rs/commit/8916e5a242bf9c6575a9cc44e81ded326b91ef75))
* **health:** one failing file no longer stops health checks for the library ([96b704a](https://github.com/infinidysk/infinidysk-rs/commit/96b704ab677c4193e406ecf70a5479d313402bea))
* **health:** repair migrated files whose download history is gone ([a924fda](https://github.com/infinidysk/infinidysk-rs/commit/a924fda34a79aa1e88bc2c313d986f381c6e7e37))
* **import:** accept a library folder that holds the mount or STRM directory ([639b1ac](https://github.com/infinidysk/infinidysk-rs/commit/639b1ac0e7db2160647bef25be01a00c2e48371a))
* **import:** bring C# health checks scheduled years out within 90 days ([dc8c4ef](https://github.com/infinidysk/infinidysk-rs/commit/dc8c4efd4361e9b591df12ae36d930b42e28a05b))
* **import:** carry over C# downloads with an empty category or unknown size ([4a2519f](https://github.com/infinidysk/infinidysk-rs/commit/4a2519f9b1780feacf66962cfcf551aa46902148))
* **import:** carry over C# quota usage and storage-group backbones ([8da8a96](https://github.com/infinidysk/infinidysk-rs/commit/8da8a96f5e62c2f0ab4598a79a986a73280f9d17))
* **mount:** date fixed directories from server start instead of 1970 ([d99456a](https://github.com/infinidysk/infinidysk-rs/commit/d99456a7e411d1966d29331af0cca2f737b1018c))
* **mount:** give fixed folders a constant date instead of server start ([4c0e77d](https://github.com/infinidysk/infinidysk-rs/commit/4c0e77d8f645658eb57bcb69b240e4cc2cf55e6c))
* **mount:** keep FUSE reads in order and survive panics and kills ([6b5aaee](https://github.com/infinidysk/infinidysk-rs/commit/6b5aaee34e1da8e486c8d9c7e490c28cf1ef4aec))
* **mount:** let Arr apps import links from the FUSE mount ([0d04db8](https://github.com/infinidysk/infinidysk-rs/commit/0d04db8b6e523ffb0549a2584b496d4db7f46a8c))
* **mount:** measure first byte from the first read, not from open ([d4540c0](https://github.com/infinidysk/infinidysk-rs/commit/d4540c0cdf4ef675ceb63c28d33ccd3c5a643631))
* **mount:** warn when FUSE readahead can't be raised in Docker ([a4194e4](https://github.com/infinidysk/infinidysk-rs/commit/a4194e4faaba2fcaa5ed88779cbfbe532dc1349a))
* **mount:** warn when the mount is not visible outside the container ([a5370e4](https://github.com/infinidysk/infinidysk-rs/commit/a5370e4428b20a88d3cbab751861e95300f0e71d))
* **nntp:** a STAT success no longer resets the breaker back-off ([0bc42ff](https://github.com/infinidysk/infinidysk-rs/commit/0bc42ff9408d48b1dc5d2c56ffb827bb5666f657))
* **nntp:** count a dropped provider connection as one failure ([3f2e0fb](https://github.com/infinidysk/infinidysk-rs/commit/3f2e0fb191f7c0e2d8db39cf047f377e0023f818))
* **nntp:** fail over to the next provider when a stream read times out ([fb0bafe](https://github.com/infinidysk/infinidysk-rs/commit/fb0bafefc5104fecbb03495928af9f6e65f350fb))
* **nntp:** fail over when a provider serves the wrong part of a post ([99e5e76](https://github.com/infinidysk/infinidysk-rs/commit/99e5e76c37e172e5b7225cb8a56cb8179e1b478e))
* **nntp:** probe a tripped provider once its cooldown ends ([211afd8](https://github.com/infinidysk/infinidysk-rs/commit/211afd8a949a9339ed35dd3ab1b2174d698d370c))
* **nntp:** report unavailable rather than corrupt when a provider was skipped ([6cfe1e5](https://github.com/infinidysk/infinidysk-rs/commit/6cfe1e58019b06de01313fd1f8fedf3ef488d2ba))
* **nntp:** requests that finish after a trip no longer close the breaker ([371b393](https://github.com/infinidysk/infinidysk-rs/commit/371b393f90def09525df531d496f0630778890bd))
* **nntp:** stop hung new connections from stalling playback ([fb67417](https://github.com/infinidysk/infinidysk-rs/commit/fb67417d57d38cb56fdd4eaeb5812ae14792e46d))
* **nntp:** stream reads give up after 30 s without a download slot ([3804962](https://github.com/infinidysk/infinidysk-rs/commit/3804962d4bc668f1effa9107d05959a3f53cacce))
* **queue:** cap each import at 16 provider requests across all steps ([af72ba3](https://github.com/infinidysk/infinidysk-rs/commit/af72ba38aa499d9f3eec7e33d6462b8db5849e4f))
* **queue:** fail over when a provider serves the wrong article during import ([3cfc3e2](https://github.com/infinidysk/infinidysk-rs/commit/3cfc3e2b0d8183abfcc5b8c66e8eb51a44a1f75f))
* **queue:** one crashing import no longer stops the queue ([d3a827a](https://github.com/infinidysk/infinidysk-rs/commit/d3a827ad2de4764a88837a23b0eb4bb2f3014e75))
* **queue:** rename every file of packs that carry one PAR2 set per file ([4f4e333](https://github.com/infinidysk/infinidysk-rs/commit/4f4e33359583ec9d3b964f3c61f8068b4e540367))
* **sab:** create category folders up front so Arr path checks pass ([6e93912](https://github.com/infinidysk/infinidysk-rs/commit/6e93912a5a6168cd736c98f397100512318fbdfd))
* **sab:** match parameter and NZB file field names case-insensitively ([ea15513](https://github.com/infinidysk/infinidysk-rs/commit/ea155133912c81b1cccf102f662da556cea1dce2))
* **stream:** keep disk cache recency across restarts ([ca07837](https://github.com/infinidysk/infinidysk-rs/commit/ca07837cfb90cbe6b43b55239bcd4dc308378a6f))
* **stream:** stamp body progress at arrival and allow per-reader experiment switches ([2bbbba1](https://github.com/infinidysk/infinidysk-rs/commit/2bbbba118f718527fcab6471f612f064ef1c11c5))
* **ui:** align overview tile trends and show short read times in seconds ([134d60d](https://github.com/infinidysk/infinidysk-rs/commit/134d60df17ad1d2e03bba01ce739c034f49776aa))
* **ui:** allow picking NZB files on iOS ([a3ce004](https://github.com/infinidysk/infinidysk-rs/commit/a3ce0043ffc1157c8d8bc48607d151cc111ffc1d))
* **ui:** clarify the read-ahead hint ([7b8757c](https://github.com/infinidysk/infinidysk-rs/commit/7b8757c06576ec3c799fda21afd77fab0bee0f8f))
* **ui:** drop decimals from overview activity totals ([a696c3e](https://github.com/infinidysk/infinidysk-rs/commit/a696c3e6be4f696df5f248bd67434e55d76aba81))
* **ui:** fit provider settings on mobile and name slider thumbs ([dba17bf](https://github.com/infinidysk/infinidysk-rs/commit/dba17bf0863e8f63d6b28bb4cff664d7483c693d))
* **ui:** fit the player to the window and warn about undecodable audio ([8f38d67](https://github.com/infinidysk/infinidysk-rs/commit/8f38d6755fd237345eb4799da35b9732262208dc))
* **ui:** focus the username field on the sign-in page ([848de2b](https://github.com/infinidysk/infinidysk-rs/commit/848de2b4a01f5fea1d25c7e0f3ca5095eefa31f8))
* **ui:** format sub-millisecond first bytes and single stalls in read history ([e277b39](https://github.com/infinidysk/infinidysk-rs/commit/e277b39291b217031c1ef5b957a0ba3ad124d6b3))
* **ui:** give settings section headers their own tier ([54729ed](https://github.com/infinidysk/infinidysk-rs/commit/54729ed7346129e637e80b8d2e9be52aa586cfe7))
* **ui:** indent the orphan action under auto-repair ([c19415b](https://github.com/infinidysk/infinidysk-rs/commit/c19415b8e751afd40f983c091af1f69f04bfcbe2))
* **ui:** mute release details in overview file names ([#14](https://github.com/infinidysk/infinidysk-rs/issues/14)) ([efe973f](https://github.com/infinidysk/infinidysk-rs/commit/efe973f2fce3e403d83643bdf870e6ce0fcc7b52))
* **ui:** open the player on plain-HTTP LAN addresses ([a046a14](https://github.com/infinidysk/infinidysk-rs/commit/a046a14af608b5a8a0aa76869aff1633c9c5be74))
* **ui:** pluralize held reconciliation counts ([91847b2](https://github.com/infinidysk/infinidysk-rs/commit/91847b2a1656da8f20e9aa5a48c7e85dc1061bc9))
* **ui:** put Add Arr instance at the top like Add provider ([d9cf641](https://github.com/infinidysk/infinidysk-rs/commit/d9cf64149a0d09b9f257bded7b03a6ea6d2f0bf2))
* **ui:** queue page no longer crashes when the default category is empty ([cbe3de1](https://github.com/infinidysk/infinidysk-rs/commit/cbe3de13bcf1b4ab45442d1c6c973098b4194d7f))
* **ui:** rename the providers settings link to Providers ([db6c2ac](https://github.com/infinidysk/infinidysk-rs/commit/db6c2accbfe254703bf15089c74f8705055d6102))
* **ui:** scroll a new Arr instance fully into view ([98bd85e](https://github.com/infinidysk/infinidysk-rs/commit/98bd85e8713f3b74f95a1cc317f5f005b4820892))
* **ui:** scroll dialogs that are taller than the window ([7bea98e](https://github.com/infinidysk/infinidysk-rs/commit/7bea98ec023a80d72743b5e425edb5ffe18cbd95))
* **ui:** scroll to the file details card when it opens in Explorer ([102d4c1](https://github.com/infinidysk/infinidysk-rs/commit/102d4c1d0877f702ec7a41e0eab8869f41a26f00))
* **ui:** show an error with a reload button instead of a blank page ([4c32b1b](https://github.com/infinidysk/infinidysk-rs/commit/4c32b1b73248be4c9a821df2fa260a2e47f76180))
* **ui:** show sizes, speeds and tuning in decimal MB and GB ([d7504a2](https://github.com/infinidysk/infinidysk-rs/commit/d7504a2621fe22ba505d8f2d0cd7f9a2dcbae29d))
* **ui:** show the hostname, port, TLS and backbone a provider profile sets ([070fe58](https://github.com/infinidysk/infinidysk-rs/commit/070fe5859218671803e6d868e7121fee34d48a0a))
* **ui:** show the orphan action only while auto-repair is on ([017c029](https://github.com/infinidysk/infinidysk-rs/commit/017c029ce19c25ef22c16e2219e4cc8942d6bfec))
* **ui:** stop asking to convert a library that needs no changes ([188954a](https://github.com/infinidysk/infinidysk-rs/commit/188954ae086edda35ac31cabdc39fc44775554fc))
* **ui:** stop library conversion buttons from saving settings ([0494d4c](https://github.com/infinidysk/infinidysk-rs/commit/0494d4c47b5afb52c352b6e727791272930dc7e7))
* **ui:** wrap page header actions on narrow screens ([31a5235](https://github.com/infinidysk/infinidysk-rs/commit/31a523502ae75c8f4cb42d5ccd3947e71e34e2b3))


### Performance Improvements

* **db:** index job folders, NZB references and latest health checks ([2aaeccb](https://github.com/infinidysk/infinidysk-rs/commit/2aaeccb791cab2e27a470b4e6cf07de171791be9))
* **db:** keep an hourly provider-bytes summary for metrics records ([f57c42e](https://github.com/infinidysk/infinidysk-rs/commit/f57c42e665ea0ecfa9d10259c4bea499fe605ec4))
* **db:** look up latest health only for the explorer page ([d612316](https://github.com/infinidysk/infinidysk-rs/commit/d612316606127980b9d4eaf4220878b9c8164bb8))
* **db:** open the metrics store in the metrics task ([2dc6521](https://github.com/infinidysk/infinidysk-rs/commit/2dc65214167094be458d65b77b2aac12030cf07a))
* **mount:** raise FUSE background limits inside containers ([cfb9972](https://github.com/infinidysk/infinidysk-rs/commit/cfb99728154ae7e862d53948fbeac70ca6756ad8))
* **nntp:** keep two connections warm to each primary provider ([21b9a34](https://github.com/infinidysk/infinidysk-rs/commit/21b9a34c0b706abf12023f0597f45b4bbce544f4))
* **nntp:** serve waiting playback requests before imports and health ([87d70bd](https://github.com/infinidysk/infinidysk-rs/commit/87d70bd3c39b020520eb4206e194a19a9ba7a463))
* **nntp:** spread requests across idle connections before pipelining ([bba8dff](https://github.com/infinidysk/infinidysk-rs/commit/bba8dff0363b1b25eeefa11cf4ca145bb1be8e58))
* **nntp:** stream decoded body chunks and skip requests cancelled while queued ([e7e2f97](https://github.com/infinidysk/infinidysk-rs/commit/e7e2f973ae132cff433b15af506b91d39724d64b))
* **nntp:** wait for a connecting slot instead of queueing behind a busy connection ([5a1ea14](https://github.com/infinidysk/infinidysk-rs/commit/5a1ea1498898582aff56be55fea29ce2945edc64))
* **sab:** filter and page history in SQL ([4e7eb37](https://github.com/infinidysk/infinidysk-rs/commit/4e7eb37892ebaffe3233a9fee06777f2a55c39a9))
* **stream:** cache bodies that arrive after their reader left ([273089d](https://github.com/infinidysk/infinidysk-rs/commit/273089d251e969a4639423f5e05e2fe5ff20c00b))
* **stream:** hedge slow head segments and warn on size mismatches ([be23221](https://github.com/infinidysk/infinidysk-rs/commit/be23221d391a720a31d94ca213d46750d5e26740))
* **stream:** keep the previous segment so out-of-order reads do not reset prefetch ([d4d2e14](https://github.com/infinidysk/infinidysk-rs/commit/d4d2e1490c3ad858c5e45f436ff283dc31fb2476))
* **stream:** keep the read-ahead open when playback keeps pace with downloads ([9f25608](https://github.com/infinidysk/infinidysk-rs/commit/9f256086a6602c874a4830380645baae4a5f4873))
* **stream:** open the read-ahead window fully after the first segment ([e187590](https://github.com/infinidysk/infinidysk-rs/commit/e187590eb41fa10323db258461a233c6f4360745))
* **stream:** prewarm connections when a read window starts ([d85323b](https://github.com/infinidysk/infinidysk-rs/commit/d85323b7397fd09f1e3ca12bfe09d477de7dbcb5))
* **stream:** serve the awaited segment as it arrives and open bounded ranges fully ([91a2daf](https://github.com/infinidysk/infinidysk-rs/commit/91a2dafe70764de9be32c0396f2f3b06d8830e2c))
* **stream:** stream every prefetched segment in as it arrives ([ca70fb6](https://github.com/infinidysk/infinidysk-rs/commit/ca70fb6c81594b2e3a0f5fa053b75dfcf2269abb))


### Refactors

* **health:** move the Arr client into an arr crate shared by all six profiles ([ef15181](https://github.com/infinidysk/infinidysk-rs/commit/ef15181965a4bec7e9fb4a71d1eb235239415c05))


### Build System

* **docker:** build the web UI into the image ([cfb9e0d](https://github.com/infinidysk/infinidysk-rs/commit/cfb9e0df2842cc8586abad51a37f3c9982ac3191))
* **docker:** include ffprobe in the runtime image ([03b3328](https://github.com/infinidysk/infinidysk-rs/commit/03b3328fc291bbf59e442150da546bc31d293130))
* **docker:** move the image and release builds to Ubuntu 26.04 ([d70d7b2](https://github.com/infinidysk/infinidysk-rs/commit/d70d7b28ce016202baf15fc3be1234f06752b3c1))
* **docker:** ship the server as an ubuntu:24.04 image with FUSE support ([b3850af](https://github.com/infinidysk/infinidysk-rs/commit/b3850af2800e6d8b50b99435bf2a296734734f66))
* target glibc on Ubuntu 24.04 instead of musl ([7569ed5](https://github.com/infinidysk/infinidysk-rs/commit/7569ed5e626ca6a857e130a9a3eeb20800d9a035))
