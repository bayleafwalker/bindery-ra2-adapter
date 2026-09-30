# Playbook induction

> Selection on outcome: only proposals from matches the arm won are used, so this is conditioned on winning and shows what the model did when it won, not what made it win.

- arm `llm-t1`, split `training`, minimum supporting won matches 6
- match manifests read 24; of the arm 24; in the split 24 (0 duplicate log(s) dropped); won 24

## `induced-allied-boom-allied-553cbefc`

- cluster: Allied, base `allied-boom`
- supporting: 317 proposals in 12 won matches; the arm chose this base in 12 matches of the split, won 12 of them
- first launch under this playbook, time (s), 12/12 matches launched: median 205.27, IQR 178.37 to 295.55, range 177.87 to 686.73
- first launch under this playbook, army value, 12/12 matches launched: median 1900, IQR 1600 to 2800, range 1600 to 9400
- attack phase enters at OwnArmyValue >= 1900 and GameSeconds >= 178.37; the playbook's top-level attack conditions (in force in every phase) carry the same army and time bounds
- parameter `expandAtSeconds` [60, 240], 12 matches (317 proposals), per-match medians: median 60, IQR 60 to 105, range 60 to 120; default 60
- source logs (SHA-256):
  - `llm-t1_ai-air_island-bridges_3.ndjson` a119f6edc2756a1b48011bc344f630387ac859284c55c3351f8fabed9721d083
  - `llm-t1_ai-air_river-crossing_3.ndjson` c3ee493f484bec6ae0081ac28e54d005385c30210b659501be05fa4a8aae87c7
  - `llm-t1_ai-air_twin-valley_3.ndjson` fcb4890ed596e15e76363a422145b387fa81601f63b9a2a521dfcce93ab15db2
  - `llm-t1_ai-balanced_island-bridges_3.ndjson` 321901202915d31ed8b2c086d64fa6fdd3b8259df19e687203bff912ef744016
  - `llm-t1_ai-balanced_river-crossing_3.ndjson` 31f439500daf7b8acd7dcb0e1f13a6d01b98045f8341e59c0ffa86ab5915fd88
  - `llm-t1_ai-balanced_twin-valley_3.ndjson` 9c9e2368ee01eaa994641308d2428d64926782f7b5c52e80c34f027da65dc96a
  - `llm-t1_ai-rush_island-bridges_3.ndjson` 6c1af191c613867b49ec3f1931499cae93eaa3271e0bbf6e5262bd25b9f74b71
  - `llm-t1_ai-rush_river-crossing_3.ndjson` 2bc4e51152b2d578b25bea323a3e5abb9f28220b21fe0eea4468607a83fc6562
  - `llm-t1_ai-rush_twin-valley_3.ndjson` 94aa24b32ab58bfc4e626fd6d905bab7313bb310775d12632f640f2113e29e33
  - `llm-t1_ai-turtle_island-bridges_3.ndjson` 65ea178cfea8c2652f94770bbae7a5d55744dae684e75b70c2222f4f50726e88
  - `llm-t1_ai-turtle_river-crossing_3.ndjson` f24239d3ec9fcf2a55ce3072a9bea3ddf5db7ac0e0759ca12481f4bf7746774e
  - `llm-t1_ai-turtle_twin-valley_3.ndjson` a8d11604fd9a1df33b836c4b3a4059896080814ec18924bf3dfa25da41f6241a

## `induced-generic-expand-soviet-8f797e00`

- cluster: Soviet, base `generic-expand`
- supporting: 348 proposals in 12 won matches; the arm chose this base in 12 matches of the split, won 12 of them
- first launch under this playbook, time (s), 6/12 matches launched: median 490.73, IQR 490.45 to 490.92, range 490.33 to 676.13
- first launch under this playbook, army value, 6/12 matches launched: median 5800, IQR 5550 to 5900, range 5500 to 8100
- attack phase enters at OwnArmyValue >= 5800 and GameSeconds >= 490.45; the playbook's top-level attack conditions (in force in every phase) carry the same army and time bounds
- parameter `expandAtSeconds` [30, 180], 12 matches (348 proposals), per-match medians: median 30, IQR 30 to 30, range 30 to 75; default 30
- source logs (SHA-256):
  - `llm-t1_ai-air_island-bridges_2.ndjson` 75a96ce961cbbffd3f29631cf84f0c2001fd7693f033aae3d680a83ec4b3d3f8
  - `llm-t1_ai-air_river-crossing_2.ndjson` 6631bed9768e4a3b8cf14c002e26ba37013308beed3fe8e8214a4b6740d6f41f
  - `llm-t1_ai-air_twin-valley_2.ndjson` e59d44f1ed87a1db2dbb492b7ae4ddfeeb316163efed2bec543d74e9012f4b5f
  - `llm-t1_ai-balanced_island-bridges_2.ndjson` 47f69b79eebda17be0247603665d3fa518237103998798a949530f7c403a528c
  - `llm-t1_ai-balanced_river-crossing_2.ndjson` f0f706c8159837abcf5c3891d31ac2f06d8730a4c8604149f17527e001740f55
  - `llm-t1_ai-balanced_twin-valley_2.ndjson` 8893458dc789044b4b6e15d31096512439fbde3256ee03e78f77159466c6f062
  - `llm-t1_ai-rush_island-bridges_2.ndjson` b38d7a4ead0725811b3db577c3f06145e126f8d63bbcad3d92576b7e0bbc83d1
  - `llm-t1_ai-rush_river-crossing_2.ndjson` 63e281c02e9f9b6fb523718c1db794334cd5aeaae3a6f6ea768290db2195f0e5
  - `llm-t1_ai-rush_twin-valley_2.ndjson` 55c484890921cc276ca814eff13a750da8ca25b0aee56a74ba3139eaa8c09c3e
  - `llm-t1_ai-turtle_island-bridges_2.ndjson` 143335e88022cf0ac36b9873bf5978221e25cbb737c64dc39d3d8ad6cd2e2bb3
  - `llm-t1_ai-turtle_river-crossing_2.ndjson` 5c051fc48aee27c5c8d66b43bf7eb81adc4d5f24a13837b8637e41403b180bc2
  - `llm-t1_ai-turtle_twin-valley_2.ndjson` 217bd609ab30950915339c3fe3ae57d5e6fe34a0d2eb4fd10ce290cc07324783

## `induced-soviet-rhino-rush-soviet-7d66f1a3`

- cluster: Soviet, base `soviet-rhino-rush`
- supporting: 28 proposals in 6 won matches; the arm chose this base in 6 matches of the split, won 6 of them
- first launch under this playbook, time (s), 3/6 matches launched: median 430.07, IQR 419.63 to 586.7, range 409.2 to 743.33
- first launch under this playbook, army value, 3/6 matches launched: median 4900, IQR 4800 to 5750, range 4700 to 6600
- attack phase enters at OwnArmyValue >= 4900 and GameSeconds >= 419.63; the playbook's top-level attack conditions (in force in every phase) carry the same army and time bounds
- parameter `attackArmyValue` [600, 2000], 6 matches (28 proposals), per-match medians: median 1000, IQR 1000 to 1000, range 1000 to 1000; default 1000
- source logs (SHA-256):
  - `llm-t1_ai-air_river-crossing_2.ndjson` 6631bed9768e4a3b8cf14c002e26ba37013308beed3fe8e8214a4b6740d6f41f
  - `llm-t1_ai-balanced_twin-valley_2.ndjson` 8893458dc789044b4b6e15d31096512439fbde3256ee03e78f77159466c6f062
  - `llm-t1_ai-rush_island-bridges_2.ndjson` b38d7a4ead0725811b3db577c3f06145e126f8d63bbcad3d92576b7e0bbc83d1
  - `llm-t1_ai-rush_twin-valley_2.ndjson` 55c484890921cc276ca814eff13a750da8ca25b0aee56a74ba3139eaa8c09c3e
  - `llm-t1_ai-turtle_island-bridges_2.ndjson` 143335e88022cf0ac36b9873bf5978221e25cbb737c64dc39d3d8ad6cd2e2bb3
  - `llm-t1_ai-turtle_river-crossing_2.ndjson` 5c051fc48aee27c5c8d66b43bf7eb81adc4d5f24a13837b8637e41403b180bc2

## Clusters not induced

- Allied / allied-grizzly-timing: 3 supporting won matches (30 proposals), below 6
- Soviet / soviet-flak-mix: 3 supporting won matches (9 proposals), below 6
