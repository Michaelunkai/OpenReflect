# OpenReflect Image Format — `.orf` (v1)

Status: CONTRACT. Byte-level, little-endian everywhere. Implementations: `OpenReflect.Format`.
Readers MUST skip unknown fields/reserved regions and reject only what is specified below.

## 1. File layout

```
┌────────────────────────────┐ offset 0
│ Header (128 bytes)         │
├────────────────────────────┤
│ Chunk record 0             │ sequential records, ascending chunkIndex
│ Chunk record 1             │
│ …                          │
├────────────────────────────┤ indexOffset
│ Chunk Index                │ (written only at finalize)
├────────────────────────────┤ manifestOffset
│ Manifest (32 bytes)        │
├────────────────────────────┤
│ Footer (64 bytes)          │ last 64 bytes of file
└────────────────────────────┘
```

A file without a footer is **incomplete** (crash or cancelled job): the header's
dirty-shutdown flag remains set, and readers report `IsComplete = false`.
Salvage: chunk records up to EOF remain parseable.

## 2. Header (128 bytes)

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0x00 | 4 | magic | ASCII `ORFI` (0x49 0x46 0x52 0x4F) |
| 0x04 | 4 | formatVersion u32 | 1 |
| 0x08 | 4 | headerSize u32 | 128 |
| 0x0C | 16 | imageId Guid16 | RFC-4122 little-endian mixed; unique per image |
| 0x1C | 16 | parentImageId Guid16 | all-zero = chain root (full) |
| 0x2C | 1 | chainKind u8 | 0=full, 1=incremental, 2=differential |
| 0x2D | 1 | chunkSizeShift u8 | chunkSize = 1 « shift, valid 19..24 (512 KiB…16 MiB) |
| 0x2E | 2 | flags u16 | bit0 dirtyShutdown, bit1 encrypted, bit2 indexComplete |
| 0x30 | 8 | createdAtUnix u64 | UTC seconds |
| 0x38 | 32 | sourceLabel utf8 | NUL-padded, e.g. `PhysicalDrive0` |
| 0x58 | 4 | kdfIterations u32 | 0 when unencrypted; valid 100000…5000000 |
| 0x5C | 16 | kdfSalt | random 16 bytes; zeros when unencrypted |
| 0x6C | 4 | noncePrefix | random 4 bytes; GCM nonce prefix |
| 0x70 | 8 | logicalChunkCount u64 | ceil(logicalSize / chunkSize); authoritative after finalize; 0 while open |
| 0x78 | 4 | reserved | MUST be 0 |
| 0x7C | 4 | headerCrc32 | CRC-32 (IEEE) over header bytes 0x00..0x7B |

## 3. Chunk record

```
recordHeader (40 bytes):
  0x00 chunkIndex   u64   position in the logical volume concatenation
  0x08 logicalOffset u64  chunkIndex * chunkSize (redundant, for robustness)
  0x10 originalLen  u64   plaintext length (== chunkSize except final chunk)
  0x18 storedLen    u64   payload byte length
  0x20 flags        u16   bit0 compressed, bit1 encrypted
  0x22 reserved[6]  MUST be 0
sha256[32]                SHA-256 of PLAINTEXT chunk
payload[storedLen]
```

Total per-record overhead: 72 bytes (40 header + 32 hash).
Records are appended in ascending `chunkIndex` order. `chunkIndex` is the **logical**
position; with used-sector imaging a zero (or parent-unchanged) region may be omitted,
so logical indices **may gap** while `logicalOffset` remains authoritative for restore.
Final chunk of the source may be short (`originalLen < chunkSize`).

## 4. Chunk Index (post-finalize)

```
count u64
entries[count]: { chunkIndex u64, recordFileOffset u64 }
```

## 5. Manifest (32 bytes)

`SHA-256` over the canonical serialization of all index entries:
`for each entry in ascending chunkIndex: chunkIndex u64 ‖ recordFileOffset u64`.

## 6. Footer (64 bytes, last 64 of file)

| Offset | Size | Field |
|---|---|---|
| 0x00 | 4 | magic ASCII `ORFF` |
| 0x04 | 4 | formatVersion u32 = 1 |
| 0x08 | 8 | indexOffset u64 |
| 0x10 | 8 | indexLength u64 (bytes) |
| 0x18 | 8 | manifestOffset u64 |
| 0x20 | 8 | logicalSize u64 — EXACT source size (short final chunk safe) |
| 0x28 | 4 | footerCrc32 over footer bytes 0x00..0x27 |
| 0x2C | 20 | reserved (0) |

## 7. Transforms

**Compression** — per record, `flags.bit0`: DEFLATE (RFC-1951, `System.IO.Compression.DeflateStream`,
best-speed..optimal mapped from `compressionLevel` 1–9). A compressed record is kept only if
`storedLen < originalLen`. (zstd is specified for v0.2; see RISKS deferral — compression codec
is self-describing via the flag, no header change needed.)

**Encryption** — per record, `flags.bit1`: AES-256-GCM.
- Key: PBKDF2-HMAC-SHA256(password UTF-8, kdfSalt, kdfIterations, 32 bytes).
- Nonce: 12 bytes = `noncePrefix (4) ‖ chunkIndex u64 LE` (96-bit, unique per record).
- Payload layout: `ciphertext ‖ tag[16]` (tag appended).
- Header `flags.bit1` set when ANY record is encrypted; all records share the key.

**Plaintext hash** — `sha256` field is always over the plaintext (post-decompress,
post-decrypt), enabling content-addressed dedup across chain images.

## 8. Chain semantics

- Root: `chainKind=full`, `parentImageId=0`, contains every non-sparse region.
- Incremental / differential: contain only regions whose plaintext SHA-256 differs from the
  effective parent (incremental = previous image in chain; differential = chain root).
  Region inheritance: a region absent from an image resolves to the nearest ancestor.
- Chains MUST NOT extend past an image with `dirtyShutdown=1` (`ChainIncomplete`).

## 9. Golden example

Minimal 1-chunk unencrypted full image, chunkSize = 1 MiB (`shift=20`), plaintext of 64 `0xAA`
bytes, compressed (deflate of 64×0xAA ≈ 10 bytes):

```
00000000  4f 52 46 49 01 00 00 00 80 00 00 00 <guid16> <guid16 zeros>
0000002c  00 14 01 00 <createdAt u64> "sparse0" 00… 00 00 00 00 <salt16 zeros>
0000006c  <noncePrefix4> 01 00 00 00 00 00 00 00 00 00 00 00 <crc32>
record:
  00 00 00 00 00 00 00 00 | 00 00 00 00 00 00 00 00 | 40 00 … | 0a 00 …
  01 00 | 00×6 | <sha256 32B> | <10B deflate payload>
index: 01 00… <entry {0, 128+72}>  … footer: 4f 52 46 46 01…
```

(Exact bytes verified by `FormatGoldenTests.Golden_Header_And_Single_Chunk`.)
