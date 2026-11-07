# Changelog

All notable changes to this project are documented here. This file is maintained
automatically by semantic-release on every release to `main`.

## [1.3.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.2.3...v1.3.0) (2026-11-07)

### 🚀 Features

* **tools:** added Graph scripts for mailbox clean-up ([5056e64](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/5056e6438029d9faee6a7e7c1217a15e629a1fc6))

## [1.2.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.2.2...v1.2.3) (2026-11-05)

### 🐛 Bug Fixes

* **runtime:** enabled compaction of the large object heap ([4b3aa54](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/4b3aa547a36444cd1f9244a2efbc20b15a4b13fa))

## [1.2.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.2.1...v1.2.2) (2026-11-05)

### 🐛 Bug Fixes

* **transfer:** capped the bytes all workers send at once ([d165cfc](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/d165cfc73de24911aad8b3ea52d218ffc11b761a))

## [1.2.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.2.0...v1.2.1) (2026-11-04)

### 🐛 Bug Fixes

* **migration:** stopped treating series mail entries as duplicates ([1a5d437](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/1a5d437b685acdd115793acffc3056e35e85510b))

## [1.2.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.1.0...v1.2.0) (2026-11-03)

### 🚀 Features

* **mailboxes:** removed surplus addresses from shared mailboxes ([f77a436](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/f77a4361a7e6677a6a736d4b6e7b587360f7c9f5))

## [1.1.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.0.1...v1.1.0) (2026-11-03)

### 🚀 Features

* **mailboxes:** enabled sent item copies on shared mailboxes ([4337e19](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/4337e19f0b2f58f9f580971d302dd12a79fe9ed6))

### 🐛 Bug Fixes

* **mailboxes:** made the sign-in name follow the mail address ([cbfb3ee](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/cbfb3ee13d91089d5767f437b3f48ec7d9b37dd4))

## [1.0.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v1.0.0...v1.0.1) (2026-11-03)

### 🐛 Bug Fixes

* **mailboxes:** fixed shared mailbox creation in mailboxes ensure ([636b6e4](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/636b6e4a921b033315ff219f5ae4ce7d74047429))

### 🔧 Maintenance

* **scripts:** added source.email to the validate config ([df69e41](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/df69e413ce7314be22e43896b2e4771378e5b73b))

## [1.0.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.10.0...v1.0.0) (2026-11-03)

### ⚠ BREAKING CHANGES

* **migration:** manifests older than schema 3 are refused, because the
  item key changed. Back up and remove the manifest, manifest-log.db and
  eml-stage. Items already in Exchange are then sent again.

### 🚀 Features

* **migration:** transferred all fields and attachments of real David items ([7953d8d](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/7953d8d69e3daf18347fb81315166628ff118c16))

### 🐛 Bug Fixes

* **ews:** hardened failure handling across the transfer path ([0887077](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/0887077939f00c6896db421a953e9d9034548050))
* **ews:** restored settle wait after an unreadable response ([013cfa6](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/013cfa663cad3c0f31b7521cca4f53c168cef529))
* **ews:** stopped retrying an illegal item key on the mail path ([00aae74](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/00aae74be9f8795e9185b6d2c6d99da2ca39e81d))
* **manifest:** replaced lone surrogates before writing to LiteDB ([9061f25](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/9061f252bb81e30b4eab05aa4fff61dd51ba145f))
* **setup:** made setup --check fail when EWS is not usable ([74d40fc](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/74d40fccd4915c5d6cc7f3c1e3a6a8184cf05fd2))

## [0.10.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.13...v0.10.0) (2026-11-03)

### 🚀 Features

* **migration:** rebuilt transfer as a verified parallel queue ([7004892](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/7004892982d9d7b3b80dc838c706b9dc753117ab))

## [0.9.13](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.12...v0.9.13) (2026-11-02)

### 🐛 Bug Fixes

* **manifest:** avoid read-then-write during export and queue ([c5f759b](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/c5f759b5d98016ff087b5f02fcd751b2575dc0d3))

## [0.9.12](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.11...v0.9.12) (2026-11-02)

### 🐛 Bug Fixes

* **manifest:** serialize LiteDB reads and writes ([d37bffb](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/d37bffbde32899312dd1a4abe77d9eeb713a3767))

## [0.9.11](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.10...v0.9.11) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** use per-worker service for queue verification ([3391054](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/339105458806be74e65d88479d2a41f785167cf3))

## [0.9.10](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.9...v0.9.10) (2026-11-02)

### ♻️ Code Refactoring

* **migration:** export to LiteDB then import via 10-item queue ([8caa98c](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/8caa98cca5bc1ba897d0e8440584295d631f3f4f))

## [0.9.9](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.8...v0.9.9) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** save each mail directly into explicit folder ([4b6428b](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/4b6428b6777bb6c9ae43dc015fa149a02a7bafed))

## [0.9.8](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.7...v0.9.8) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** disable batching and import each item explicitly ([a78a4b2](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/a78a4b250f396a0584d474f0d81d5b43f7cfcef2))

## [0.9.7](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.6...v0.9.7) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** verify batch items and move misplaced messages ([bb6527d](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/bb6527d05b4b0e6a189650d6e8fb9358edb64513))

## [0.9.6](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.5...v0.9.6) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** derive Inbox/Sent root from David archive path ([79362dd](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/79362dd74512e5e540c073bddc236b3ade49bd81))

## [0.9.5](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.4...v0.9.5) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** verify batch results and retry missing items ([04c1993](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/04c1993d95fc6fd65ff7a5f7a0c470fd7e05a675))
* **fax:** use license-free PdfSharpCore for PDF conversion ([5299aed](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/5299aed69a934b1ee447f74b53cfc870ef473c02))

## [0.9.4](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.3...v0.9.4) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** stream pending items in bounded batches ([baa5837](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/baa5837525d78979cdd978a78744ed0fe56594a0))

## [0.9.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.2...v0.9.3) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** use 10-item batches and retry throttling ([d270f72](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/d270f7283fcdce1f5ed903ed1476d8ea934174c6))

## [0.9.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.1...v0.9.2) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** use [FAX] as fallback subject ([0f261e1](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/0f261e193a472df9b3345c15c7e120e36ac89f0a))

## [0.9.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.9.0...v0.9.1) (2026-11-02)

### 🐛 Bug Fixes

* **staging:** delete EML and attachments after successful import ([3710552](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/37105529631bb0edee96311aec3d27971c3d53d8))

## [0.9.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.8.1...v0.9.0) (2026-11-02)

### 🚀 Features

* **fax:** use A4 pages and PDF document metadata ([1e21c6a](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/1e21c6a10b22264762d80d508b3f87b56570559b))

## [0.8.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.8.0...v0.8.1) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** retry failed batch items individually ([6a8aacc](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/6a8aacc82963076dc4bf4b208840a5e78d56d356))

## [0.8.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.7.0...v0.8.0) (2026-11-02)

### 🚀 Features

* **fax:** convert TIFF to named multi-page PDF ([464e967](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/464e967d3d9126e75b44cd8b03a177b24725974f))

### 🐛 Bug Fixes

* **ews:** keep Inbox and Sent Items in separate batch groups ([95e68ff](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/95e68ffeb84bcbe9804d89f87f22dcdb843b4060))

## [0.7.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.6.3...v0.7.0) (2026-11-02)

### 🚀 Features

* **ews:** batch 100 items and import with 8 parallel workers ([b3f23fb](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/b3f23fba90097c12896d8ae082621a356baf6836))

## [0.6.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.6.2...v0.6.3) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** support archiveRoot with or without archive segment ([e674382](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/e6743821d081edcf5eb23b1e0c51c97ae6226d6a))

## [0.6.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.6.1...v0.6.2) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** run migration on a single STA thread ([3dcf215](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/3dcf215f072e56a2db0d5b234a68614608b431be))

## [0.6.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.6.0...v0.6.1) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** reuse David session for discover and export ([43f897d](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/43f897dad22699b19135d8efc8de54237552f40c))

## [0.6.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.5.3...v0.6.0) (2026-11-02)

### 🚀 Features

* **tasks:** migrate David tasks and preserve HTML bodies ([1dbd7e1](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/1dbd7e14c3f7ed73f553bafdeac8c867af171edc))

## [0.5.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.5.2...v0.5.3) (2026-11-02)

### 🐛 Bug Fixes

* **mail:** preserve HTML body in synthetic EML fallback ([b1e50d1](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/b1e50d12edf5472887eb63eaad045106d093be7a))

## [0.5.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.5.1...v0.5.2) (2026-11-02)

### 🐛 Bug Fixes

* **mailboxes:** set shared mailbox sign-in name to target UPN ([750752e](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/750752e9b0d19c1b9b3330bb25500ea0b58e30c9))

## [0.5.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.5.0...v0.5.1) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** keep synthetic EML bodies free of provenance text ([4e90682](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/4e90682445783b2b22c152a4c6e7a4a77416235b))

## [0.5.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.6...v0.5.0) (2026-11-02)

### 🚀 Features

* **unpack:** mark all COMMON and USER archives as unpacked ([d92de79](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/d92de796723607b7c1ff99b1d73458868a716008))

## [0.4.6](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.5...v0.4.6) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** read unpacked TIFF paths and remap archiveRoot ([1968670](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/1968670c652104e4de0923b1ddb1dc5a7f70c925))

## [0.4.5](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.4...v0.4.5) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** do not read packed David images through UNC ([6b6400b](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/6b6400b4d441e92997861ad0a6e0a7efd81aa3c1))

## [0.4.4](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.3...v0.4.4) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** resolve David image paths against configured archiveRoot ([f246b95](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/f246b95f373bc88aeba3f16b2cea30eaad62652e))

## [0.4.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.2...v0.4.3) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** extract mail body, protect non-mail export and type calendar folders ([163604d](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/163604d7d35e2babd072470deece57ea7ce200a1))

## [0.4.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.1...v0.4.2) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** default to all items and harden DVEml export buffer ([ae8b4f6](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/ae8b4f6de3fab8a92b385cf2500ee73f8a8aeebe))

## [0.4.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.4.0...v0.4.1) (2026-11-02)

### 🐛 Bug Fixes

* **mail:** fall back to generic EML when David export fails ([0f2833c](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/0f2833c988dd081eb749fc42d4af4a233916ece3))

## [0.4.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.5...v0.4.0) (2026-11-02)

### 🚀 Features

* **config:** map common roots and unknown users to shared retention mailboxes ([615afa0](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/615afa05fd864dfbbeab3ce38ff615c1620dfb32))

### 🐛 Bug Fixes

* **targets:** keep device-code output out of JSON and add --out ([3864304](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/3864304b87eef8bed29bd036d4b278df29b77e79))

## [0.3.5](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.4...v0.3.5) (2026-11-01)

### 🐛 Bug Fixes

* **migration:** prefer David EML for SMS, chat and voice ([fa61004](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/fa61004b8a845201294d313b6a4ccc5bc449e478))

## [0.3.4](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.3...v0.3.4) (2026-11-01)

### 🐛 Bug Fixes

* **ews:** resolve standard folders by archive type and WellKnownFolderName ([3d79bb3](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/3d79bb3c3d3246d0902c4d995e5d79daa9c8ee22))

## [0.3.3](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.2...v0.3.3) (2026-11-01)

### 🐛 Bug Fixes

* **fax:** prefer David EML and fall back to original image attachment ([0cb29f7](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/0cb29f75f33e2b928592298d1b0f803925a77df4))

## [0.3.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.1...v0.3.2) (2026-11-01)

### 🐛 Bug Fixes

* **voice:** preserve original WAV attachments in synthetic EML ([95adfbb](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/95adfbb408030e08b68d83421eb8180bc9d04104))

## [0.3.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.3.0...v0.3.1) (2026-11-01)

### 🐛 Bug Fixes

* **ews:** map David folders into existing Inbox/Sent/Calendar/Contacts ([6961876](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/69618762a833807101a4140db3095f61167fcd7e))

## [0.3.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.2.2...v0.3.0) (2026-11-01)

### 🚀 Features

* **migration:** support fax, sms, voice, chat, calendar, contacts and folders ([0892059](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/089205903c8fe04c56ca4cf0a4569739731e0d21))

## [0.2.2](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.2.1...v0.2.2) (2026-11-01)

### 🐛 Bug Fixes

* **run:** honor limits.limit and add explicit --all ([56ab47e](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/56ab47ec24176cba069af42be46d87ad2d765463))

## [0.2.1](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.2.0...v0.2.1) (2026-11-01)

### 🐛 Bug Fixes

* **setup:** use device-code auth and certreq certificate fallback ([60f68e0](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/60f68e0a09b61f554e2a536df6d5e86b348f9de8))

## [0.2.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.1.0...v0.2.0) (2026-10-31)

### 🚀 Features

* **ews:** dedupe imports by internet message id for safe resume ([2271921](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/22719211ed61dc0623b394d72d176005fc125a16))

## [0.1.0](https://github.com/bauer-group/IP-David2ExchangeOnline/compare/v0.0.0...v0.1.0) (2026-10-31)

### 🚀 Features

* **cli:** expose setup, config, discover, run, verify and report ([934db26](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/934db26a910a692829479ca76583e4c18757a6a2))
* **dvapi:** add David COM client, archive probe and local export ([dcc4b89](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/dcc4b8955365935a7f6c4ea6822c372904e494a7))
* **migration:** add YAML config, LiteDB manifest and migration pipeline ([4d004f0](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/4d004f09923456f43d39f87dece3267d37ab3c57))
* **setup:** add tenant and mailbox provisioning tooling ([b1f2f7d](https://github.com/bauer-group/IP-David2ExchangeOnline/commit/b1f2f7d58c387e7b252cdf08bcefa6596b649139))
