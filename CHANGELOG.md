# Changelog

All notable changes to this project are documented here. This file is maintained
automatically by semantic-release on every release to `main`.

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
