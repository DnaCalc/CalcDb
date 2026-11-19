# Licence ledger

Third-party exact material in CalcDb (README Charter: Licence; A-01 permitted uses). CalcDb is MIT; each row below
keeps its own licence. A row is added when material enters the repository, and changes when its use changes.

## Jackcess (Apache License 2.0)

Source: <https://github.com/jahlborn/jackcess>, immutable commit `be0a2139b087a227d14cafd4ea231306a5a3a862`
(2026-09-13, SVN trunk revision 1514; not identified with a release). Files fetched from
`https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/<path>` on 2026-11-20
into a new folder and checked against A-01 before use: every size and SHA-256 matched. They are kept unmodified,
byte for byte (`.gitattributes`: `third_party/** -text`), under `third_party/jackcess/`, with the project's
`LICENSE.txt` and `NOTICE.txt` from the same commit. Selected under PRD-05 for W-04/weight-tables-per-sort-order.

| File in `third_party/jackcess/` | A-01 source ID | Upstream path | Bytes | SHA-256 | Kind | Used in |
| --- | --- | --- | ---: | --- | --- | --- |
| `index_codes_gen.txt` | S-JACK-RES-GEN | `src/main/resources/com/healthmarketscience/jackcess/index_codes_gen.txt` | 1512 | `a0554f81d328732d3d6fcac5d44476130efeaccb96bdc1eda30590dd71ecb8b5` | data, embedded | `CalcDb.Format`: `SortDescriptors.General` |
| `index_codes_ext_gen.txt` | S-JACK-RES-GEN-EXT | `src/main/resources/com/healthmarketscience/jackcess/index_codes_ext_gen.txt` | 495523 | `0f54f68c0592ba5066d505cb3b6782e64edb344da6a0a24959277dea9a478c28` | data, embedded | `CalcDb.Format`: `SortDescriptors.General` |
| `index_codes_genleg.txt` | S-JACK-RES-GENLEG | `src/main/resources/com/healthmarketscience/jackcess/index_codes_genleg.txt` | 1241 | `acc97846553560c0b2cdde822c990cea2a2bea733ca071e850e180b9ce3cd9ee` | data, embedded | `CalcDb.Format`: `SortDescriptors.GeneralLegacy` |
| `index_codes_ext_genleg.txt` | S-JACK-RES-GENLEG-EXT | `src/main/resources/com/healthmarketscience/jackcess/index_codes_ext_genleg.txt` | 305459 | `d60378601aebc22f579b8c5dd35a4c0d3e77c371275372f25511d73fe3e92ae7` | data, embedded | `CalcDb.Format`: `SortDescriptors.GeneralLegacy` |
| `index_codes_gen_97.txt` | S-JACK-RES-GEN97 | `src/main/resources/com/healthmarketscience/jackcess/index_codes_gen_97.txt` | 1200 | `ad74d251bd8cf441976446e50d84bc7366c28e0ba2c970ada1b52904d816bad9` | data, embedded | `CalcDb.Format`: `SortDescriptors.General97` |
| `index_mappings_ext_gen_97.txt` | S-JACK-RES-GEN97-MAP | `src/main/resources/com/healthmarketscience/jackcess/index_mappings_ext_gen_97.txt` | 233 | `60cb99d942c69c778bc4f2162af54168b6b3d631db502bce5d4b3a318883935c` | data, embedded | `CalcDb.Format`: `SortDescriptors.General97` |
| `LICENSE.txt` | S-JACK-RESOURCE-LICENSE-CONTEXT | `LICENSE.txt` | 11358 | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` | licence | distributed with the material above |
| `NOTICE.txt` | S-JACK-RESOURCE-NOTICE-CONTEXT | `NOTICE.txt` | 415 | `937c6d63028fe96f278ed40b000f889b602d9c411dd3c8be010f4a8ab7c3c641` | notice | distributed with the material above; it concerns financial functions, not the sort tables |


**Read for facts only, not in the repository.** A-01 classifies the pinned Java sources S-JACK-LEGACY-PIN,
S-JACK-GENERAL-PIN, S-JACK-GENERAL97-PIN, S-JACK-IDXCODES-PIN and S-JACK-BYTEUTIL as facts with no port or adaptation
(II-05 §13: selecting the resources does not select the Java implementations). A first implementation that ported them
was discarded before commit (2026-11-20); the encoder in `src/CalcDb.Format/TextKeys.cs` is CalcDb's own, written from
II-05 §5 and the resource grammar above, and checked against ACE's native index entries.
`src/CalcDb.Format/SortDescriptor.cs` names each resource's source ID, size and SHA-256 and refuses to compile from bytes
that differ.

**Not taken from Jackcess.** The long-key finish in `IndexKeys.FinishComposite` follows ACE's native entries
(r-sort-3/create-keys-accdb), not the pinned `IndexData` rule, which drops the final byte; it is written from II-05 §6
and the native records. Unsupported sort orders are refused (NotModelled.SortOrder), not written with the reference's
General – Legacy fallback.