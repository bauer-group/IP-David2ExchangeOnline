# Changelog

All notable changes to this project are documented here. This file is maintained
automatically by semantic-release on every release to `main`.

## [0.9.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.9.0...v0.9.1) (2026-11-02)

### 🐛 Bug Fixes

* **staging:** delete EML and attachments after successful import ([f99f2da](https://github.com/bauer-group/IP-David2Exchange/commit/f99f2dadd34d56aa599c86299c18af87b5d99e30))

## [0.9.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.8.1...v0.9.0) (2026-11-02)

### 🚀 Features

* **fax:** use A4 pages and PDF document metadata ([045d7fa](https://github.com/bauer-group/IP-David2Exchange/commit/045d7fafd34c3da1bd489ef92e936659d2949cf6))

## [0.8.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.8.0...v0.8.1) (2026-11-02)

### 🐛 Bug Fixes

* **ews:** retry failed batch items individually ([56cebec](https://github.com/bauer-group/IP-David2Exchange/commit/56cebec18fb5abe6250c0f8ed3d5a511664b3e5a))

## [0.8.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.7.0...v0.8.0) (2026-11-02)

### 🚀 Features

* **fax:** convert TIFF to named multi-page PDF ([20bb9fb](https://github.com/bauer-group/IP-David2Exchange/commit/20bb9fbe5bcecd736a7ace14ac028ddd90e4e651))

### 🐛 Bug Fixes

* **ews:** keep Inbox and Sent Items in separate batch groups ([ff8e260](https://github.com/bauer-group/IP-David2Exchange/commit/ff8e260515b6ae71cdb9a363eb422648f58662ff))

## [0.7.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.6.3...v0.7.0) (2026-11-02)

### 🚀 Features

* **ews:** batch 100 items and import with 8 parallel workers ([a061f28](https://github.com/bauer-group/IP-David2Exchange/commit/a061f28d4ce4ec623cfbfb2550c43277e2a9943b))

## [0.6.3](https://github.com/bauer-group/IP-David2Exchange/compare/v0.6.2...v0.6.3) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** support archiveRoot with or without archive segment ([b6c4209](https://github.com/bauer-group/IP-David2Exchange/commit/b6c42096bda39e7aef7fe691b76db644b56517bf))

## [0.6.2](https://github.com/bauer-group/IP-David2Exchange/compare/v0.6.1...v0.6.2) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** run migration on a single STA thread ([2510726](https://github.com/bauer-group/IP-David2Exchange/commit/2510726d16b524e144e2ea8476e77153adbeac7b))

## [0.6.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.6.0...v0.6.1) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** reuse David session for discover and export ([b3cdefb](https://github.com/bauer-group/IP-David2Exchange/commit/b3cdefbd05ecb6c370b9c69a7aad7ddda6e56a62))

## [0.6.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.5.3...v0.6.0) (2026-11-02)

### 🚀 Features

* **tasks:** migrate David tasks and preserve HTML bodies ([67f44d6](https://github.com/bauer-group/IP-David2Exchange/commit/67f44d60816badcf193f248e81243d68c1c10a98))

## [0.5.3](https://github.com/bauer-group/IP-David2Exchange/compare/v0.5.2...v0.5.3) (2026-11-02)

### 🐛 Bug Fixes

* **mail:** preserve HTML body in synthetic EML fallback ([6074042](https://github.com/bauer-group/IP-David2Exchange/commit/6074042f25c5fd0014f5f3530710edb2a6406b21))

## [0.5.2](https://github.com/bauer-group/IP-David2Exchange/compare/v0.5.1...v0.5.2) (2026-11-02)

### 🐛 Bug Fixes

* **mailboxes:** set shared mailbox sign-in name to target UPN ([39ff91b](https://github.com/bauer-group/IP-David2Exchange/commit/39ff91b336f619f34313dc31078dc1a0d13d716c))

## [0.5.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.5.0...v0.5.1) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** keep synthetic EML bodies free of provenance text ([40dfbe8](https://github.com/bauer-group/IP-David2Exchange/commit/40dfbe807819042c0eb00087de99cf54eb03e0cd))

## [0.5.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.6...v0.5.0) (2026-11-02)

### 🚀 Features

* **unpack:** mark all COMMON and USER archives as unpacked ([4961628](https://github.com/bauer-group/IP-David2Exchange/commit/4961628db40d8436c52a7114fda52c09f6fce8c4))

## [0.4.6](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.5...v0.4.6) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** read unpacked TIFF paths and remap archiveRoot ([2c9ecc0](https://github.com/bauer-group/IP-David2Exchange/commit/2c9ecc0bdfe9a47562b656dc32634a97508acec8))

## [0.4.5](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.4...v0.4.5) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** do not read packed David images through UNC ([1e64da9](https://github.com/bauer-group/IP-David2Exchange/commit/1e64da911c1da8c4d0113eb4df86b24d69d5aa22))

## [0.4.4](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.3...v0.4.4) (2026-11-02)

### 🐛 Bug Fixes

* **fax:** resolve David image paths against configured archiveRoot ([7a54623](https://github.com/bauer-group/IP-David2Exchange/commit/7a54623b5c19b29ac811f8b75deae5651cda74a9))

## [0.4.3](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.2...v0.4.3) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** extract mail body, protect non-mail export and type calendar folders ([8a9b15c](https://github.com/bauer-group/IP-David2Exchange/commit/8a9b15cd5ed8280f2c0de9a9b698d2de87ac54fb))

## [0.4.2](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.1...v0.4.2) (2026-11-02)

### 🐛 Bug Fixes

* **migration:** default to all items and harden DVEml export buffer ([5062f21](https://github.com/bauer-group/IP-David2Exchange/commit/5062f21c8ad4fcc4f2ef7b083cf44998d6078134))

## [0.4.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.4.0...v0.4.1) (2026-11-02)

### 🐛 Bug Fixes

* **mail:** fall back to generic EML when David export fails ([388e713](https://github.com/bauer-group/IP-David2Exchange/commit/388e713e855e12a64d7868bdd8ec01f883d9df98))

## [0.4.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.5...v0.4.0) (2026-11-02)

### 🚀 Features

* **config:** map common roots and unknown users to shared retention mailboxes ([9028a47](https://github.com/bauer-group/IP-David2Exchange/commit/9028a47933f0e53fa084ecbab0b7d78d7eb5fc6a))

### 🐛 Bug Fixes

* **targets:** keep device-code output out of JSON and add --out ([8a79842](https://github.com/bauer-group/IP-David2Exchange/commit/8a798426f33565e81da8b3ade0eedfe9da25e3ad))

## [0.3.5](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.4...v0.3.5) (2026-11-01)

### 🐛 Bug Fixes

* **migration:** prefer David EML for SMS, chat and voice ([05fefa3](https://github.com/bauer-group/IP-David2Exchange/commit/05fefa34a3f0015873a2f9c78a8b0380543b37bf))

## [0.3.4](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.3...v0.3.4) (2026-11-01)

### 🐛 Bug Fixes

* **ews:** resolve standard folders by archive type and WellKnownFolderName ([f77295a](https://github.com/bauer-group/IP-David2Exchange/commit/f77295aa632584033c48a30ca31d4adca09c17d5))

## [0.3.3](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.2...v0.3.3) (2026-11-01)

### 🐛 Bug Fixes

* **fax:** prefer David EML and fall back to original image attachment ([fe2f84c](https://github.com/bauer-group/IP-David2Exchange/commit/fe2f84c90834e2bedd28b5e6679687b65c9ac0a4))

## [0.3.2](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.1...v0.3.2) (2026-11-01)

### 🐛 Bug Fixes

* **voice:** preserve original WAV attachments in synthetic EML ([2a9172f](https://github.com/bauer-group/IP-David2Exchange/commit/2a9172fe8dfa05679ac3a968f2557c3a789228ec))

## [0.3.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.3.0...v0.3.1) (2026-11-01)

### 🐛 Bug Fixes

* **ews:** map David folders into existing Inbox/Sent/Calendar/Contacts ([f930030](https://github.com/bauer-group/IP-David2Exchange/commit/f93003088f682b519ea29bcc60a399d8e016c343))

## [0.3.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.2.2...v0.3.0) (2026-11-01)

### 🚀 Features

* **migration:** support fax, sms, voice, chat, calendar, contacts and folders ([ffba0f1](https://github.com/bauer-group/IP-David2Exchange/commit/ffba0f19f11b8f57d222eff485f7f4b82a97f333))

## [0.2.2](https://github.com/bauer-group/IP-David2Exchange/compare/v0.2.1...v0.2.2) (2026-11-01)

### 🐛 Bug Fixes

* **run:** honor limits.limit and add explicit --all ([e7b591d](https://github.com/bauer-group/IP-David2Exchange/commit/e7b591dd471cc8264732767de698e87fd4e4eb99))

## [0.2.1](https://github.com/bauer-group/IP-David2Exchange/compare/v0.2.0...v0.2.1) (2026-11-01)

### 🐛 Bug Fixes

* **setup:** use device-code auth and certreq certificate fallback ([8282931](https://github.com/bauer-group/IP-David2Exchange/commit/8282931b47dd8408820b8f5220a093d1729946a2))

## [0.2.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.1.0...v0.2.0) (2026-10-31)

### 🚀 Features

* **ews:** dedupe imports by internet message id for safe resume ([1b9da67](https://github.com/bauer-group/IP-David2Exchange/commit/1b9da67213c26314fb6a83a8fe69a22634ee6763))

## [0.1.0](https://github.com/bauer-group/IP-David2Exchange/compare/v0.0.0...v0.1.0) (2026-10-31)

### 🚀 Features

* **cli:** expose setup, config, discover, run, verify and report ([88bff11](https://github.com/bauer-group/IP-David2Exchange/commit/88bff11a5180f32e2513fe6a88a346f18fbab14f))
* **dvapi:** add David COM client, archive probe and local export ([9b6db22](https://github.com/bauer-group/IP-David2Exchange/commit/9b6db22ec61c49e16ad81493e624623c15b353dc))
* **migration:** add YAML config, LiteDB manifest and migration pipeline ([0ee2fe0](https://github.com/bauer-group/IP-David2Exchange/commit/0ee2fe093f5228b5965f37a8645797b09d01c4d1))
* **setup:** add tenant and mailbox provisioning tooling ([2b4c813](https://github.com/bauer-group/IP-David2Exchange/commit/2b4c8138302a3f300077b57bcc8aefddf75c5f08))
