# II-05. Indexes and sort-order keys

Revision 1 · Stage 2, response 4 · 2026-11-13

The index implementation consists of a native key encoder, a checked page codec and a tree editor that prepares page/reference changes for II-03's phase-image executor. **Use inclusive child-maximum fences, retain logical row-root locators, and plan splits from recomputed compressed byte occupancy. Provisionally retain nonempty underfull pages and the physical root page; unlink empty children without introducing eager merging.** These choices fit the concrete reference mechanics developed here and limit the pages involved in the priority DAO update loop.[^authority] Native acceptance, cursor effects and crash classes decide where they change.

**Requirement trace:** §§1–6 implement RQ-SEM-02's native comparison/key obligation and the index portion of RQ-FMT-03/RQ-TYPE-01; §§7–11 implement shared navigation and mutation for RQ-ISAM-01/RQ-DAO-02. The page/reference plans support RQ-STORE-01/02/04 and the normal-write interoperability of RQ-PROD-04. Bulk construction supplies the early rebuilding mechanism required by RQ-LATE-04 without implementing the later repair product. II-02 owns the real platform Automation value boundary; II-03 owns immutable page images and physical I/O; II-04 owns row roots and allocation. Sections 12–14 make the physical phases, cost model and acceptance concrete under K-KEY/K-FORMAT/K-PREFLIGHT.

**Evidence boundary.** All numeric database examples below are constructed, arithmetically checked hypotheses. None is an ACE capture. The exact reference material is Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`; A-01 records each acquired file's hash, inspected passages and licence status. This immutable pin is distinct from both a release label and the newer live xrefs. Some live xrefs now expose mechanisms absent from this pin; their content is not silently attributed to it. The initial oracle remains the user-reported ACEDAO.DLL **16.0.20527.20016**, with actual host/provider identity still to capture. The CalcDb algorithms below are original systems-design pseudocode, not a source-code translation or an adoption of reference write ordering.

## 1. Logical definitions and physical index ownership

Bind one physical index descriptor to its owner-map identity, root page and ordered physical columns. Logical names and relationship records can alias that same tree. Binding is cached by schema incarnation; a renamed or recreated public Index object cannot silently reuse a descriptor from an earlier incarnation.

All offsets in this section are relative to the logical TableDef stream described in II-04. A block can cross a continuation-page boundary; use that stream's checked reader instead of treating a logical offset as an offset in the first 4096-byte page.

**Figure F-29 — three TableDef index structures.** Source-backed hypothesis tables and coordinates; not ACE-confirmed or render-verified.

### 1.1 Twelve-byte physical counter blocks

The physical-index counter area begins at logical offset63. Physical index number `j` has a 12-byte block at `63+12*j`. The pinned reader consumes a little-endian32-bit value at block offset4, named `uniqueEntryCount`. Its source explicitly describes a count that increases on certain distinct additions and does not decrease on removal. Therefore it is not a reliable current row count or a replacement for counting live leaf entries. The other eight bytes are not interpreted by that reader. Preserve them; an empty reference creator starts this area with zeroed bytes, which is a construction choice. R-FMT-2 should compare delete/reinsert/duplicate additions before assigning the counter a native semantic name. [^S-JACK-INDEX-PIN][^S-JACK-FMT]

### 1.2 Fifty-two-byte physical descriptors

Physical descriptors follow the physical counter area, ordinary column descriptors and variable-size column names. There is one52-byte physical descriptor per physical index. Its ten column slots use physical column numbers, not public display ordinals. [^S-JACK-INDEX-PIN][^S-JACK-FMT]

| Relative offset | Width | Meaning / reference behavior |
| --- | --- | --- |
| 0 | 4 | Pinned writer emits1923 (`83 07 00 00`); reader skips. Preserve original rather than treating this as a validated universal magic. |
| 4+3i, for i=0…9 | 2+1 | `u16LE columnNumber` then flag byte; `ffff` is unused; bit 01 means ascending. Other flag bits remain preserved/unresolved. |
| 34 | 1 | Index-owned usage-map declaration row. |
| 35 | 3 | Usage-map declaration page, little endian. |
| 38 | 4 | Root page number, little endian. |
| 42 | 4 | Pinned writer's comment identifies page residue; reader skips; preserve. |
| 46 | 1 | Index flags:01 unique,02 ignore nulls,08 required,80 unknown in source terminology. |
| 47 | 1 | Pinned writer emits02 for an index containing a complex column, zero otherwise; reader skips with remainder. |
| 48 | 4 | Pinned writer advances over zero bytes; reader skips. |

The usage-map reference is the ordinary II-04 map locator: row first, then24-bit little-endian page. It is neither a root pointer nor the big-endian row locator appended to an index key. It names a declaration row whose map membership covers this physical index's pages. Do not replace it with the table's ordinary-data owned map merely because page header owner4 identifies the same TableDef. Index allocation/deallocation must update the physical index owner map and the global free map through the common page planner. The exact native publication order is a crash experiment result, not something this descriptor settles. [^S-JACK-MAP][^S-JACK-INDEX-PIN]

Do not interpret `unique`, `required` and `ignore nulls` bits by independently inventing SQL null semantics. They choose the applicable shared index/row semantic rules; source paths and the target need separate probes for all-null and partially-null compound keys. Likewise,80 is a bit to preserve, not permission to clear it as “unused.”

### 1.3 Twenty-eight-byte logical descriptors and aliases

After all physical52-byte descriptors come the logical28-byte descriptors, followed by separately encoded index names. Logical and physical counts differ because more than one logical index may share the same physical index data. [^S-JACK-IDXIMPL-PIN][^S-JACK-INDEX-PIN][^S-JACK-FMT]

| Relative offset | Width | Meaning / reference behavior |
| --- | --- | --- |
| 0 | 4 | Pinned writer emits TableDef-associated value1625 (`59 06 00 00`); reader skips. |
| 4 | 4 | Logical index number, little endian. |
| 8 | 4 | Physical index data number, little endian. This selects the shared physical descriptor/tree. |
| 12 | 1 | Related table type for foreign-key metadata. Source constants1 primary-side,2 secondary-side. |
| 13 | 4 | Related logical index number, little endian; source uses signed−1 for absent. |
| 17 | 4 | Related table's TableDef page number, little endian. |
| 21 | 1 | Cascade-update flags; source bit 01 enables updates. |
| 22 | 1 | Cascade-delete flags; source bit 01 enables deletes, bit 02 requests null on delete. |
| 23 | 1 | Source type1 primary-key index;2 foreign-key index. Other native types require profile interpretation. |
| 24 | 4 | Pinned writer emits zero; reader skips. |

Names follow all28-byte records, in logical-index order; they are not inline28-byte fields. In the selected Jet4/inherited ACE profile, the same-pin TableImpl name reader/writer and JetFormat constants describe a two-byte little-endian byte length followed by uncompressed UTF-16LE name bytes. The stream may cross continuation pages, so the segmentation mechanism described in II-04 still applies. Two logical descriptors with physical index data number3 are aliases onto one root, owner map, ordered column descriptor and native physical entry set. Row mutation must produce one physical delete/insert per shared tree while checking each relevant logical constraint and relationship. Creating a fresh duplicate tree per logical descriptor changes native metadata, space use, constraints and update behavior.[^S-JACK-TABLE][^S-JACK-FMT]

The connection back to the row engine is therefore precise: resolve logical metadata to a physical descriptor; obtain its columns by physical number; encode a full key for the old/new row; apply one tree delta to each affected physical descriptor; and serialize affected TableDef counters/descriptors only through the existing logical-stream patch planner. Neither the source's method call order nor its zeroing choices establish ACE's write/crash ordering.

## 2. The native index-page envelope

**Figure F-30 — index header, boundary mask and payload.** Source-backed numeric hypothesis table; not ACE-confirmed or render-verified.

The pinned Jet4 profile and its ACE profile subclasses share the following index-page offsets. These are not the ordinary kind 01 data-page offsets after byte 11: an index page has an entry-boundary bitmap, not a row directory. In particular, **bytes 8–11 are not an index-entry count**. The pinned writer labels them a write stamp and emits zero; that source choice does not authorize zeroing an existing native stamp. [^S-JACK-FMT][^S-JACK-INDEX-PIN]

| Decimal offset | Hex offset | Width | Proposed decoded field / source-backed meaning |
| --- | --- | --- | --- |
| 0 | 00 | 1 | Page kind: `03` interior, `04` leaf. |
| 1 | 01 | 1 | Reference writer emits `01`; preserve an existing value pending profile rules. |
| 2 | 02 | 2 | Unsigned little-endian free byte count. Reference computes `4096 - endOfStoredEntries`. |
| 4 | 04 | 4 | Little-endian owning TableDef page number. This is not the root page number. |
| 8 | 08 | 4 | Write-stamp field in pinned source terminology. Opaque to the initial CalcDb implementation until a native rule is established. |
| 12 | 0C | 4 | Little-endian previous peer page, zero when absent. |
| 16 | 10 | 4 | Little-endian next peer page, zero when absent. |
| 20 | 14 | 4 | Little-endian tail-child page, zero when absent. It is not a next-leaf link. |
| 24 | 18 | 2 | Little-endian common-prefix byte count. |
| 26 | 1A | 1 | Level: leaf zero; in the pinned model, a parent is one greater than its children. |
| 27–479 | 1B–1DF | 453 | Entry-boundary bitmap. |
| 480–4095 | 1E0–FFF | 3616 | Stored entry stream followed by unused bytes. |

The source accepts a zero recorded level on an interior node and derives a level from a child; it treats zero as an unfilled value in that situation. Therefore the initial reader must not turn the simple rule “nonleaf level must be positive” into a claimed native invariant. Track a stored level and, when needed, a checked structural level. A profile can later require exact consistency after R-FMT-2 establishes what the target creates and accepts. The peer links have the same-level relationship in the source validator; the source also distinguishes some interior-peer relationships from leaf links. A generic assertion that every level is one uninterrupted global doubly linked list is too strong. [^S-JACK-IDXCACHE-PIN]

The owner and page links are fixed-width page numbers, not byte offsets. Offset computation belongs to the pager's checked `pageNumber * 4096` operation. Do not apply a host structure cast: the header is little endian while entry trailers below are big endian.

### 2.1 Exact entry-mask convention and capacity

Number the bits of the bitmap from zero, with the least significant bit first in each byte. A set bit at position `b` means **an exclusive end offset `b` measured from payload byte 480**. Equivalently:

```
bitmapByte = floor(b / 8)
bitmapBit  = b mod 8
physical bitmap byte = 27 + bitmapByte
entry physical end   = 480 + b
```

There is no subtraction of one. Bit9 marks the end after nine stored bytes. The next entry begins at that boundary. The page's stored entry count is the bitmap's population count, not a header row count and not the data-page 255-slot limit. [^S-JACK-INDEX-PIN]

The bitmap has3624 bit positions, numbered0–3623; the actual entry area has3616 bytes. A completely filled stream ends at 3616 and uses bitmap byte 452, bit 0, which is absolute page byte 479. Bits1–7 of that last bitmap byte would claim ends outside the payload. This explains why453 mask bytes are necessary even though452×8 equals3616: boundary3616 itself still needs a representable bit. Bit0 cannot terminate a nonempty entry. A bounded parser rejects either impossible case before slicing the page. The pinned capacity computation takes the minimum of page data space and bitmap bit capacity; for this profile it resolves to3616. [^S-JACK-FMT][^S-JACK-INDEX-PIN]

Let reconstructed full entries be `E0…E(n−1)`, and let their stored common prefix be `P` of length `p`. The physical stream is:

```
P || E0[p:] || E1[p:] || … || E(n−1)[p:]
```

The **first physical entry is complete**, because its span includes `P`. Subsequent physical spans omit `P`. Thus `b0 = |E0|`, and later boundaries accumulate `|Ei|−p`. For `n > 0`, compressed occupancy is:

`C = sum(|Ei|) − p × (n−1)`.

For the empty reference root, `n=0`, `p=0`, `C=0`. Do not use a nonempty-page formula to justify an empty page with an arbitrary nonzero prefix.

The prefix is page-wide, not a chain of differences against the immediately previous entry. This permits constant-time selection of an entry span once the bitmap has been decoded. A reader needs at most two immutable slices for a later entry: the first `p` bytes of the payload and that entry's stored suffix. It does not need to rebuild every preceding key.

## 3. Reconstructed entries and worked page images

| Entry | Reconstructed bytes | Trailer width |
| --- | --- | --- |
| Leaf | `K || u24BE(dataPage) || u8(rowSlot)` | 4 |
| Interior | `K || u24BE(dataPage) || u8(rowSlot) || u32BE(childPage)` | 8 |

`K` is the complete native sort-key byte sequence produced for the physical index's ordered columns. The index entry's row location is **three big-endian page bytes followed by a one-byte row slot**. This differs in both order and byte order from the ordinary row/LVAL/usage-map locator used elsewhere: `u8(row) || u24LE(page)`. Sharing a generic four-byte “row reference” decoder across these contexts would silently address the wrong page. [^S-JACK-INDEX-PIN][^S-JACK-MAP]

The interior entry retains the full leaf-style row location before its child pointer. It is an inclusive high-key witness for the child: the source validator compares its key and row location to that child's last logical entry. The child pointer does not participate in key order. The source compares `K` as unsigned bytes, then row page and row slot to distinguish duplicates. Consequently, a seek using a separator's SQL value but a different row locator can route to a different child. Treating an interior entry as merely `userKey || childPage` loses that distinction. [^S-JACK-INDEX-PIN][^S-JACK-IDXCACHE-PIN]

The reference entry serializer can preserve a prefix extending through the three row-page bytes. For example, duplicate keys whose rows are on the same page can share all of `K || u24BE(page)` and leave only their differing slots. Its serializer does not accept extending the prefix into the row slot; its source notes the singleton edge case rather than establishing a complete native rule. **The pinned cache's newly calculated prefix is more conservative: it compares only the first and last real entries' key bytes, excluding locator bytes and excluding the tail child.** These are separate facts. Reader support for an existing locator-bearing prefix must not be discarded merely because the reference writer chooses a shorter prefix when constructing one. Nor does the source demonstrate that every native singleton prefix is writable by that implementation. [^S-JACK-INDEX-PIN][^S-JACK-IDXCACHE-PIN]

For CalcDb, prefer these provisional policies: preserve an existing legal prefix for a lossless unchanged-page path; recompute a safe prefix for changed content; initially choose a key-only prefix for newly packed pages unless the target experiment admits the fuller optimization. Read admissibility, write admissibility and byte-for-byte reproduction are distinct checks. R-FMT-2 should contain duplicate-key pages whose prefix reaches zero, one, two and three locator page bytes, plus singleton and empty cases.

### 3.1 Tail children and high-key descent

If an interior page has stored entries `(H0,c0)…(H(n−1),c(n−1))` and a nonzero header tail child `t`, it has `n+1` children. The final child's pointer is in the little-endian header, and its maximum entry is not another on-page mask-delimited entry. The source constructs a virtual tail entry in memory by reading the tail child's maximum. Do not count that virtual entry when building a bitmap or calculating page prefix/occupancy. [^S-JACK-IDXCACHE-PIN]

For an ordered lookup, choose the first stored high key greater than or equal to the full probe. If none qualifies and a tail exists, descend to the tail. A source-backed subtlety is that **interior pages without a tail are supported**. For those pages, a probe above the largest stored high key goes to the last stored child; this is also the insertion destination for a new maximum. The reference preparation code promotes a tail only when the last child is a leaf and there is more than one child. It demotes a sole tail into a real stored entry. Do not impose the textbook “every nonleaf has `n+1` children” rule on these pages. Native construction and deletion shapes remain R-FMT-2 questions. [^S-JACK-IDXCACHE-PIN]

Original routing specification, separate from the reference implementation's cache structure:

```
choose_child(node, probe):
    require node.kind == interior
    i = lower_bound(node.real_entries, probe,
                    compare key_bytes then data_page then row_slot)
    if i < node.real_entry_count:
        return node.real_entries[i].child_page
    if node.tail_child != 0:
        return node.tail_child
    require node.real_entry_count > 0
    return node.real_entries[last].child_page
```

The last branch is not permission to invent a child for an empty malformed node. Bounds, ownership, self-links and cycles are checked before the next page is admitted to the cursor stack.

### 3.2 Three duplicate Long entries and a locator-bearing prefix

**Figure F-31 — a complete compressed leaf image.** Constructed 4096-byte witness; not an ACE-created page or render-verified figure.

Assume a nonunique single-column ascending Access Long index and three rows whose value is42. The pinned scalar encoder gives the hypothetical native key `7f 80 00 00 2a`: a nonnull ascending marker and the sign-adjusted big-endian32-bit integer. The scalar calculations in §4 independently give the same candidate bytes. Each row is on data page `0x012345`, in slots `02`, `07` and `fe`. These are isolated page witnesses, not an executed or complete database. [^S-JACK-INDEX-PIN][^S-JACK-IDXCODES-PIN]

| Entry | Full reconstructed bytes |
| --- | --- |
| E0 | `7f 80 00 00 2a 01 23 45 02` |
| E1 | `7f 80 00 00 2a 01 23 45 07` |
| E2 | `7f 80 00 00 2a 01 23 45 fe` |

Choose prefix `7f 80 00 00 2a 01 23 45`, length8. The physical payload is11 bytes:

```
7f 80 00 00 2a 01 23 45 02 07 fe
```

The first entry ends at 9. The next two one-byte suffixes end at 10 and11. Mask bits 9,10,11 are set, so mask byte 1 is `0e`. Compressed occupancy is `27−8×2=11`; free space is `3616−11=3605=0x0e15`. The prefix is a valid serialization witness for the reader/entry writer's mechanism; it is not claimed to be the prefix the pinned cache would newly choose.

This table specifies every byte of the 4096-byte fixture; unspecified ranges are explicitly zero for reproducibility, without claiming native ACE zeroes unused space:

| Page range | Bytes or rule |
| --- | --- |
| 0000–001A | `04 01 15 0e 20 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 08 00 00` |
| 001B | `00` |
| 001C | `0e` |
| 001D–01DF | Zero bytes |
| 01E0–01EA | `7f 80 00 00 2a 01 23 45 02 07 fe` |
| 01EB–0FFF | Zero bytes |

Owner TableDef is page `0x20`; peer and tail pointers are zero; leaf level is zero. A parser that incorrectly treats the second stored span `07` as a standalone entry will underflow its four-byte locator trailer. A parser that reconstructs the page-wide prefix obtains the complete E1 and correctly identifies page `0x012345`, slot7.

### 3.3 Two high keys and a header tail

**Figure F-32 — an interior page with three leaf children.** Constructed high-key, locator and byte-order witness; not ACE-confirmed or render-verified.

Use ascending Long keys10 and20, and an interior page whose three children are `0x100`, `0x101`, `0x102`. Its first two stored high keys retain row locations `(0x001234,7)` and `(0x001235,9)` respectively. Its tail pointer is `0x102`. The intended children hold these ordered rows:

| Child page | Value and row locator | Maximum used by parent |
| --- | --- | --- |
| 0100 | `5@(001233,02)`, `10@(001234,07)` | `10@(001234,07)` in stored E0 |
| 0101 | `15@(001235,03)`, `20@(001235,09)` | `20@(001235,09)` in stored E1 |
| 0102 | `25@(001236,01)`, `30@(001236,02)` | `30@(001236,02)` only in an in-memory tail view |

The full parent entries are13 bytes each:

```
E0 = 7f 80 00 00 0a 00 12 34 07 00 00 01 00
E1 = 7f 80 00 00 14 00 12 35 09 00 00 01 01
```

Prefix is `7f 80 00 00`, length4. The stored stream is22 bytes, with exclusive ends13 and22. Bitmap byte 1 has bit 5 set (`20`); byte 2 has bit 6 set (`40`). Free count is `3616−22=3594=0x0e0a`. The tail pointer appears at page offset20 as `02 01 00 00`, while child `0x100` inside E0 appears as `00 00 01 00`: the same conceptual page-number class has different byte order in these two physical contexts. [^S-JACK-INDEX-PIN]

| Page range | Bytes or rule |
| --- | --- |
| 0000–001A | `03 01 0a 0e 20 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 02 01 00 00 04 00 01` |
| 001B | `00` |
| 001C | `20` |
| 001D | `40` |
| 001E–01DF | Zero bytes |
| 01E0–01F5 | `7f 80 00 00 0a 00 12 34 07 00 00 01 00 14 00 12 35 09 00 00 01 01` |
| 01F6–0FFF | Zero bytes |

The compressed second entry starts with `14`, not its nonnull flag; the missing flag is in the page prefix. Probe `(10,0x1234,7)` descends to `0x100`; `(10,0x1234,8)` descends to `0x101`. Probe `(20,0x1235,9)` descends to `0x101`; the next row locator for value20 goes to the tail. A value99 insertion probe also goes to the tail. These examples show why complete high-key comparison matters even when a page is sorted by one numeric column.

The independently constructed child pages each contain the two listed rows, use a four-byte key prefix, have ends9 and14, have mask byte 1=`42`, and have14 stored bytes/free count3602. Their leaf links are `0100↔0101↔0102`. These files check the parent's stored maxima against actual constructed child entries. They contain no ordinary row data or TableDef pages and therefore are not a database that ACE can open.

### 3.4 Empty root and bounded decoding

The pinned new-root representation is a leaf with prefix0, level 0, no peers/tail, zero mask, zero stored entries and free count3616 (`20 0e`). Its owner remains the TableDef page. When the source removes the last entry from the root, it retains the root page and changes it back to an empty leaf. Empty nonroot pages are unlinked rather than retained as ordinary live search children. These are reference behaviors for R-FMT-2, not a proof of native root allocation/deallocation policy. [^S-JACK-INDEX-PIN][^S-JACK-IDXCACHE-PIN]

The exact constructed empty fixture header is `04 01 20 0e 20 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00`; every subsequent byte is zero.

Original bounded decoder proposal:

```
decode_index_page(take image_pin, physical_descriptor, selected_profile, budget):
    image = image_pin.bytes
    require image.length == 4096
    kind = load_u8(image, 0)
    require kind is leaf or interior
    prefix_length = load_u16_le(image, 24)
    count, first_end, last_end = count_and_validate_all_set_bits(image[27:480])
        // first pass rejects every position outside 0 < end <= 3616
    reservation = reserve checked directory/scratch capacity from budget
    ends = enumerate_set_bits_lsb_first(image[27:480], exactly count)
    require ends are strictly increasing

    if ends.empty:
        require prefix_length == 0
        validate_empty_shape(image, physical_descriptor, selected_profile)
        return EmptyDirectory(take image_pin, take reservation)

    require prefix_length <= ends[0]
    prefix = image[480 : 480 + prefix_length]
    previous_end = 0
    result = page_local_entry_directory()
    for each (ordinal, end) in ends:
        if ordinal == 0:
            view = one_span(image[480 : 480 + end])
        else:
            view = two_spans(prefix,
                            image[480 + previous_end : 480 + end])
        trailer = 4 if leaf else 8
        require view.length >= trailer
        key_length = view.length - trailer
        validate_key_shape(view[0:key_length], physical_descriptor)
        locator = read_u24_be_and_u8(view, key_length)
        child = read_u32_be(view, key_length + 4) if interior
        append checked descriptor(view, key_length, locator, child)
        previous_end = end

    verify strict order using key bytes, then locator; exclude child
    record expected_free = 3616 - ends.last
    compare expected_free with stored free count under selected profile
    return complete entry directory taking image_pin and reservation
```

This decoder stores boundary offsets and small descriptors. It can compare the prefix and suffix spans without allocating one contiguous buffer per entry. It extracts trailers across span boundaries: the full row location can itself be partially or wholly in the prefix. The code that reads a child cannot assume that its physical four bytes are adjacent to its key in the underlying page. For the usual writer prefix limit they are in the suffix, but the logical-entry accessor remains the appropriate boundary.

Reject invalid slice arithmetic and impossible references before following links. Preserve the original page image for diagnostics, and do not “repair” a mismatched free count, unknown stamp or unexpected level merely by recomputing the preferred value. A permissive read path can be broader than a mutation path, but a page whose write invariants are unresolved does not become writable because its keys were readable. On the first read of a physical tree, validate root ownership and the owner's usage map; traversal validates child ownership, peer reciprocity when visited, and absence of cycles. Full-tree validation can check every child maximum and every allocated index page without making every point seek a full-tree walk.

The displayed witnesses were checked by an original independent arithmetic reader: it enumerated mask bits, reconstructed full entries, compared manual payload/free-space expectations, checked child maxima and sibling order, and tested seven routing probes. This checks the examples, not native compatibility.

The decoder takes one immutable image pin and owns its tentative directory until all checks succeed. On failure it releases that pin and its reservation, publishing neither a partial cache view nor a successful prefix of the entries. I/O, budget exhaustion, cancellation, unknown profiles and malformed input remain distinct errors. No native write occurs. A trailer-only bound of four/eight bytes is insufficient: a supported nonempty key descriptor normally contributes at least one marker per field. Validate its admitted key shape, including the separate shortened-key form; do not require reconstruction of every original value from a lossy key.

Count mask bits before allocating. A loose bound is 3,616 endpoints, hence 7,232 bytes for a u16 endpoint directory, plus the retained 4,096-byte image and any descriptors. Actual key grammar gives a tighter count. Two spans per key avoid expanding a shared prefix into every entry. Bitmap enumeration is O(453+n); checking order can cost O(n×Lmax), and binary search O(log n×Lmax), where Lmax bounds reconstructed entry length. Charge those validation comparisons rather than assuming the entire decode is always O(page size).

Read tolerance, serializer capability and fresh-page policy differ. The pinned reader can safely reconstruct some singleton prefixes reaching a row slot or child; its serializer only handles prefixes through the three row-page bytes, and its fresh chooser uses only key bytes. A safely readable singleton is not automatically writable with its recorded prefix. Keep unmodified bytes; qualify changed-page repacking under R-FMT-2. A TableDef owner alone also cannot distinguish two physical indexes on that table: check the selected index's owner map and traversal ancestry.

## 4. Native field keys and descriptor binding

Use three distinct lengths: a field contribution, a composite user key, and a complete ordered entry. For the pinned source, a normal leaf entry is:

`FinishComposite(Field₀ ∥ Field₁ ∥ … ∥ Fieldₙ₋₁) ∥ PageBE24 ∥ SlotU8`.

There is no additional delimiter between fields and no delimiter before the four-byte row locator. Each non-null contribution starts with its direction marker; its type-specific payload supplies the rest. A null contribution is one byte. The maximum column count in the reference is ten. The row locator sorts duplicate physical entries, but it is not part of user-key equality, the key shortening operation, or a SQL equality predicate. A decoder for a page can compare the complete reconstructed key bytes without reversing them into values; reconstructing original text from an index key is impossible in general because distinct strings can share weights. Keep the original row value available for semantic comparison and for returning data. [^S-JACK-INDEX-PIN][^S-JACK-IDXCODES-PIN]

The exact pinned text selector recognizes only these descriptor pairs:

| Native sort identifier | Version interpreted by source | Selected reference family |
|---|---:|---|
| `1033` (`0x0409`) | `-1` (stored signed byte `FF` where applicable) | General 97 |
| `1033` | `0` | General Legacy |
| `1033` | `1` | General |

`ColumnImpl.readSortOrder` reads the identifier as the descriptor's two-byte integer and, when the format stores a four-byte sort descriptor, the signed version byte at offset `+3`. It does not interpret the byte at `+2`. The raw descriptor must be preserved. Its fallback for identifier zero uses the file format's default sort order; that is a reference compatibility choice, not proof that arbitrary zero or unknown descriptors are valid native aliases. `General97` is a sort family name, not permission to open an excluded Jet 3 file and not grounds for rejecting a Jet 4/ACE file carrying that descriptor. [^S-JACK-COL][^S-JACK-INDEX-PIN]

The pin has no general routing class for other language or tailored orders. Unsupported orders either become read-only in Jackcess or, behind `isWriteBrokenIndex`, are written using General Legacy with an expectation that Access repairs them later. CalcDb does not adopt that fallback. All scope-required native orders remain required: inventory every advertised and corpus-observed descriptor, attach an exact profile to each, and close the missing table or tailoring cases through R-SORT-3. An unknown profile is an incomplete implementation case; it is not a redefinition of scope. A known unrelated edit can preserve a verified unchanged existing key, but creating or changing a key requires its exact qualified profile.

CalcDb's profile key includes the raw sort identity/version, relevant field flags and file-family rules. The selected target cohort is an explicit product compatibility policy and evidence dimension; the oracle build is not a hidden dispatch key that changes bytes for the same file-visible descriptor. Unexplained build conflicts remain unresolved until that policy is decided. It should not be keyed only by a locale name or the machine's current culture. Text family selection and NUMERIC ordering mode are separate: the latter is selected by the file format in the reference. Store profile data with its acquisition provenance and an identity hash; use table sharing internally only when it preserves every profile's distinct behavior.

**D-SORT-1 — Provisional.** Bind an exact native encoder by the file-visible sort/type descriptor, column directions and admitted target conditions. Retain full semantic values where native keys lose information. R-SORT-3 confirms the byte rules and which operations can use encoded equality/order directly. Generic host collation, text normalization and a known-broken fallback followed by Compact & Repair remain unsuitable because ordinary writes must already interoperate.

### 4.1 Direction markers and scalar payloads

The following table describes bytes *after* coercion to an admissible stored field value. It does not define public DAO/ADO coercion, Null acceptance or error timing. It applies to ordinary indexable fields. Microsoft documents that Calculated, OLE Object and Attachment fields cannot be indexed; II-04's calculated-field wrapper is therefore not an ordinary scalar key envelope. Do not call a virtual row writer and then transform whatever wrapped bytes it happens to return. The native rejection and exact error remain probe results. [^ms-indexed][^S-JACK-INDEX-PIN][^S-JACK-COL][^S-JACK-CALC]

| Field state | Ascending | Descending |
|---|---:|---:|
| Null | `00`, with no payload | `FF`, with no payload |
| Non-null | `7F`, then payload | `80`, then payload |

For the payload table below, `~` complements every payload bit, `BEw` writes a width-w big-endian quantity, `M` is a sixteen-byte big-endian unsigned NUMERIC magnitude at the column's declared scale, and `A` is the ascending payload. The direction marker is handled separately.

| Native scalar family | Ascending payload | Descending payload | Payload size |
|---|---|---|---:|
| Byte, unsigned | raw byte | complement raw byte | 1 |
| Integer, Long, Large Number | signed two's-complement `BEw`, most significant bit flipped | `~A` | 2, 4, 8 |
| Currency/Money | signed `BE8(value × 10000)`, most significant bit flipped | `~A` | 8 |
| Single, Double, ordinary Date/Time | IEEE bits in big-endian order: complement all bits if sign bit is set; otherwise flip the sign bit | `~A` | 4, 8, 8 |
| Boolean | True `00`; False `FF` | True `FF`; False `00` | 1 |
| NUMERIC, nonnegative | `FF ∥ M` | ACE-family mode: `00 ∥ ~M`; Jet 4 legacy mode: `FF ∥ ~M` | 17 |
| NUMERIC, negative | `00 ∥ ~M` | ACE-family mode: `FF ∥ M`; Jet 4 legacy mode: `00 ∥ M` | 17 |

The integer transform is equivalently a bias by half the signed range followed by an unsigned big-endian store. It supplies directly comparable bytes without arithmetic overflow if implemented as a sign-bit operation. AutoNumber fields use the corresponding native scalar type; they do not introduce an extra key type. The Boolean descriptor in the reference never classifies its value as a null key and routes a null input through Boolean conversion. Record that as a source special case requiring native admission/error checks, not as a universal coercion for nullable expressions. [^S-JACK-IDXCODES-PIN][^S-JACK-INDEX-PIN]

The NUMERIC distinction is particularly important. The ordinary row representation in II-04 consists of a sign and four little-endian words, with words arranged most significant first. The key magnitude is a sixteen-byte big-endian integer. In the reference, Jet 4 selects the legacy transformation and ACE 2007 onward selects the corrected one. The legacy descending representation still places negative keys ahead of positive keys because its sign byte is not reversed with direction. Preserve this as a testable file-family hypothesis, not an opportunity to repair an old file's ordering silently. The source mentions a historical Microsoft bug, but no inaccessible KB text is used here as independent evidence. [^S-JACK-FMT][^S-JACK-COL][^S-JACK-INDEX-PIN]

### 4.2 Checked scalar examples

These are independent calculations from the described representation; no reference Java encoder ran. An original arithmetic calculator produced 78 fixtures and ran selected invariant checks for integer ordering, field lengths, sampled table equalities and shortening boundaries. These are calculated witnesses, not 78 independent native expected-output comparisons.

| Value/type | Ascending complete field | Descending complete field |
|---|---|---|
| Long `−2` | `7F 7F FF FF FE` | `80 80 00 00 01` |
| Long `0` | `7F 80 00 00 00` | `80 7F FF FF FF` |
| Long `42` | `7F 80 00 00 2A` | `80 7F FF FF D5` |
| Long `256` | `7F 80 00 01 00` | `80 7F FF FE FF` |
| Byte `255` | `7F FF` | `80 00` |
| True | `7F 00` | `80 FF` |
| Single `1.5` | `7F BF C0 00 00` | complement its four payload bytes after `80` |
| Money `−12.3456` | `7F 7F FF FF FF FF FE 1D C0` | complement its eight payload bytes after `80` |

Long 42 at page `0x123456`, slot `7`, is the nine-byte full leaf entry `7F 80 00 00 2A 12 34 56 07`. A neighboring duplicate at another slot shares the same first five key bytes. This fixture is suitable for the page-prefix and split examples without introducing text uncertainty.

For NUMERIC `123.45` at scale two, `M = 00` repeated fourteen times followed by `30 39`. Ascending positive is `7F FF ∥ M`; ascending negative is `7F 00 ∥ ~M`. Descending positive begins `80 00` under the ACE-family hypothesis and `80 FF` under the Jet 4 hypothesis. The exact sixteen-byte magnitude is unchanged by that selection except for the indicated complement.

### 4.3 Floating-point and DATE limits

The bit transform itself distinguishes `−0` and `+0`: their ascending complete Double fields are `7F 7F FF FF FF FF FF FF FF` and `7F 80 00 00 00 00 00 00 00`. A direct raw-bit transform also orders NaN signs and payloads. None of this establishes whether ACE accepts those bit patterns through each public interface, preserves them, equates them for a unique index, or normalizes them on writing. Keep raw scalar storage separate from qualified key canonicalization and from SQL equality. Probe signed zero, subnormals, infinities and several quiet/signaling NaN bit patterns through supported creation paths; classify rejected inputs rather than inventing native acceptance.

The ordinary Date/Time encoder uses this same floating-point transformation in the reference, despite the non-numeric interpretation of negative Automation DATE fractions established in II-04. For example, source-derived ascending payload order places raw `−1.75` (`7F 40 03 FF FF FF FF FF FF`) before raw `−1.25` (`7F 40 0B FF FF FF FF FF FF`). Their time-of-day interpretation on the same negative day runs the other way. Raw `−0.25` and `+0.25` also represent the same Automation time interpretation but have different source-derived keys. This is a decisive R-SORT-3 case: compare actual accepted stored bits, index bytes, DAO Seek, unique-index insertion and SQL ordering, rather than replacing native key order with a chronological normalization on assumption.

### 4.4 Binary, GUID and Extended Date/Time

The pinned generic binary key uses chunks of eight data bytes plus one count byte. An intermediate chunk ends with `09`, meaning more input follows. The last chunk is padded with zeroes to eight bytes and ends with the number of valid data bytes, `01` through `08`. For descending order complement data and padding bytes in every chunk; preserve intermediate `09`; complement the final count byte as well. The reference emits no chunk for an empty binary input, leaving only the field marker. That boundary needs native checking; it must not be confused with a null field. [^S-JACK-INDEX-PIN]

`BINARY {41 42 43}` therefore yields ascending `7F 41 42 43 00 00 00 00 00 03` and descending `80 BE BD BC FF FF FF FF FF FC`. For lengths 8 and 9, test the transition from one last count `08` to an intermediate `09` plus another padded chunk. A universal whole-field complement would be wrong because an intermediate `09` stays `09`.

GUID uses the same chunk framing after converting to the sixteen bytes in canonical textual component order, not the native row's mixed-endian memory representation. For `{00112233-4455-6677-8899-AABBCCDDEEFF}`, the ascending field is:

`7F 00 11 22 33 44 55 66 77 09 88 99 AA BB CC DD EE FF 08`.

The descending field is:

`80 FF EE DD CC BB AA 99 88 09 77 66 55 44 33 22 11 00 F7`.

This is a source fact about the pinned GUID writer and key framing; R-SORT-3 must vary every GUID component independently and check ACE ordering. Neither a COM GUID byte comparison nor a different database's UUID ordering is a substitute. [^S-JACK-COL][^S-JACK-INDEX-PIN]

Extended Date/Time has a concrete reference encoder. Start with the forty-two-byte native payload from II-04: nineteen ASCII day digits, `:`, twelve ASCII whole-second digits, seven fractional digits, and `:7` followed by NUL. Output five groups of eight payload bytes, each followed by `09`; then output the last two payload bytes and a seven-byte trailer `00 00 00 00 00 00 02`. With the field marker this is 55 bytes. For descending order complement all forty-two payload bytes and all seven trailer bytes, while preserving the five separators `09`. Those separators are chunk structure, not tab characters in the stored date value. The fixture for `0001-01-02 01:02:03.1234567` reuses II-04's exact 42-byte row payload and checks this 55-byte size. Native index admission, limits and error behavior still require the pinned ACE build; an implemented reference descriptor does not establish acceptance by every ACE file profile. [^S-JACK-COL][^S-JACK-INDEX-PIN]

## 5. Text weights and exact field framing

For General Legacy and General, the reference first retains at most 255 UTF-16 code units and then removes trailing U+0020 spaces from that retained prefix. It does not remove every Unicode whitespace character, normalize the row string, or stop at embedded NUL. The table determines whether each remaining code unit contributes primary bytes, secondary bytes, a modifier, a later flag or positional punctuation/control information. The 255 limit precedes trailing-space removal; a surrogate pair at that boundary can therefore be split by this reference preprocessing. These are important length and boundary probes. [^S-JACK-LEGACY-PIN]

The table file grammar encodes facts, not a generic Unicode collation standard:

| Resource class | Contribution in the pinned encoder |
|---|---|
| `S` | Primary bytes, possibly an expansion of several primary weights |
| `I` | Primary bytes plus secondary bytes |
| `P` | Add a byte modifier to the preceding secondary value; if none exists, create a tracked prefix |
| `U` | Positional unprintable/punctuation information, without primary bytes |
| `Z` | Primary bytes, optional secondary bytes, plus a two-bit later flag |
| `X` | No weight |
| `G` | A significant-character class used by the General 97 nibble encoder |

Odd-length hex strings in a resource record have an implicit leading zero nibble. Thus General's `E02` means `0E 02`, not `E0 02`. In the base tables, record zero corresponds to U+0000. The extended tables start at U+0100 and omit all 2,048 surrogate code units; their 63,232 records cover the remaining BMP range through U+FFFF. This omission is structural, so a naïve line-number lookup beyond U+DFFF is wrong. [^S-JACK-LEGACY-PIN][^S-JACK-RES-GENLEG][^S-JACK-RES-GENLEG-EXT][^S-JACK-RES-GEN][^S-JACK-RES-GEN-EXT][^S-JACK-RES-GEN97][^S-JACK-RES-GEN97-MAP]

The basic framing is primary bytes, `01`, optional secondary and later material, then `00`. Ordinary earlier primary characters contribute secondary placeholder `02` when a later accented character requires that level. Trailing secondary bytes in `00..02` are removed by the source. Combining modifiers operate on the secondary stream rather than passing through a Unicode normalization function. Later material has its own boundaries: the pinned `Z` path packs non-trailing two-bit flags into bytes whose high two bits are `10`, then emits a suffix whose repeat count depends on the number of original flagged characters. The source's name for these flags is not a specification of a complete general-purpose tertiary table. There is no separate case-weight/tertiary resource in this pin. Additional levels visible in the newer live xrefs must not be silently attributed to it.

For exact `Z` behavior in the pinned code, flags are `2` or `3`; trailing `3` flags are removed from the packed stream, but still contribute to the suffix length. Three flags fit in each packed byte. For `n` original flags, let `r = ceil(n/7)`; the suffix is `FF 02 ∥ (80 repeated r) ∥ FF ∥ (80 repeated r)`. If positional unprintables follow, append `FF` before their section. The encoder adds two `01` bytes before later material and one further `01` before the unprintable section. With the initial end-primary marker, a simple unprintable-only case has four consecutive `01` bytes between primary output and its position. This is a concrete source hypothesis to check at `n=1,3,7,8,14,15`, not a license to approximate scripts with a flat primary string.

For descending General/General Legacy, take the complete ascending payload including its final `00`, complement those payload bytes, and append a new uncomplemented `00`. The non-null prefix changes to `80`; it is not part of that payload operation. This differs from binary framing and from General 97.

### 5.1 Small table facts and complete examples

| Code unit(s) | General Legacy resource fact | General resource fact |
|---|---|---|
| `A`, `a` | primary `4A` | primary `0E 02` |
| `E`, `e` | primary `51` | primary `0E 21` |
| `é` U+00E9 | primary `51`, secondary `0E` | primary `0E 21`, secondary `0E` |
| combining acute U+0301 | secondary modifier `0C` | secondary modifier `0C` |
| `Æ` U+00C6 | expansion `4A 51` | expansion `0E 02 0E 21` |
| U+0000 | ignored | ignored |
| U+0020 internally | primary `07` | primary `07 02` |
| U+00AD | positional unprintable code `83` | ignored |

The `A` and `a` sample establishes case equality for that pair in these profiles; it does not prove every case or locale rule. For the selected acute example, a bare `e` followed by U+0301 obtains placeholder `02` and then modifier `0C`, producing secondary `0E`. Thus the source treats that pair like precomposed `é` without changing the original row value. Other combining orders and expansions require their own probes. Embedded U+0000 contributes no weight in these tables, so `A`, `A\u0000` and `A ` share the illustrated key, although their stored strings remain distinct. U+0001 instead contributes unprintable code `03` and a position. [^S-JACK-RES-GENLEG][^S-JACK-RES-GENLEG-EXT][^S-JACK-RES-GEN][^S-JACK-RES-GEN-EXT]

| Text value/profile | Ascending complete field | Descending complete field |
|---|---|---|
| `A`, General Legacy | `7F 4A 01 00` | `80 B5 FE FF 00` |
| `Aé`, General Legacy | `7F 4A 51 01 02 0E 00` | `80 B5 AE FE FD F1 FF 00` |
| `A`, General | `7F 0E 02 01 00` | `80 F1 FD FE FF 00` |
| `Aé`, General | `7F 0E 02 0E 21 01 02 0E 00` | `80 F1 FD F1 DE FE FD F1 FF 00` |

**Figure F-33 — a compound key with mixed directions and Null.** Constructed encoding witness; not ACE-confirmed or render-verified.

In the compound index `(Long ASC, Text General DESC, nullable Long ASC)`, row `(42,"Aé",Null)` has sixteen key bytes:

`7F 80 00 00 2A | 80 F1 FD F1 DE FE FD F1 FF 00 | 00`.

| Full-entry offsets | Contribution | Exact bytes | Width |
| --- | --- | --- | --- |
| 0–4 | Long42, ASC | `7F 80 00 00 2A` | 5 |
| 5–14 | General text Aé, DESC | `80 F1 FD F1 DE FE FD F1 FF 00` | 10 |
| 15 | Nullable Long, Null ASC | `00` | 1 |
| 16–19 | Row-root page0x123456, slot7 | `12 34 56 07` | 4 |

The vertical separators in the expression are explanatory and are not stored. With page `0x123456`, slot `7`, append `12 34 56 07` for a twenty-byte complete leaf entry. There is no whole-key reversal for mixed direction. The encoder can keep field-end offsets in its private scratch state, but those offsets do not become native column-length headers.

### 5.2 An expansion/control discrepancy to test

The pinned legacy file's opening comment says an unprintable position counts primary units. The actual loop increments its `charOffset` once per character handler that emits primary bytes, even when the handler expands to multiple units, and the helper can subsequently adjust that offset through secondary-stream length. Those statements are not equivalent for an expansion. The actual source path gives different unprintable positions for `Æ\u0001` and `AE\u0001`: the former reaches position formula `0x8000 | (7+4×1) = 800B`, the latter `800F`, despite equal primary output. In General the `Æ` primary expansion is four bytes, so it is an especially clear experiment. R-SORT-3 must capture the engine's complete keys for the expansion, the spelled-out equivalent, accent variants and punctuation at both ends. Do not resolve conflicting source comments by choosing the tidier interpretation.

### 5.3 Surrogates and General 97

General Legacy assigns no weight to either surrogate half in the pin. General calculates weights from individual UTF-16 units rather than first converting pairs to a Unicode scalar. Its high-surrogate branches are:

| High-surrogate interval | Primary weight, unsigned big-endian 16-bit | Secondary byte |
|---|---|---:|
| U+D800..U+D83F | code unit minus 10238 | `3F` |
| U+D840..U+D87F | code unit plus 9666 | `3E` |
| U+D880..U+DB7F | ignored | none |
| U+DB80..U+DBFF | code unit plus 9090 | `3E` |

For a low surrogate c, use c−U+DC00 only to select the interval. Subtract the selected constant from c itself: 9992 for offsets 0..7, 9990 for 8..261, 9988 for 262..515, 9986 for 516..769, and 9984 for 770..1023; emit the result as a big-endian 16-bit weight and append secondary `3F`. For the pair `D83D DE00`, the source-derived General key is `7F B0 3F B6 FC 01 3F 3F 00`. That is not a statement that all supplementary characters or isolated surrogates are accepted by ACE's public write APIs. The probe includes each branch boundary, paired and lone units, reversed halves, and the 255-unit truncation boundary. [^S-JACK-GENERAL-PIN]

General 97 is a separate encoder, not General Legacy with a different primary table. It uses 256 base records and a sparse mapping of selected U+0152..U+2122 characters back to that table; an unmapped extended unit is ignored. Secondary information is packed into nibbles, with zero boundary nibbles and `2` placeholders for the source's significant-character class. There is no General-family `01` end-primary marker. Descending complements the resulting payload and does not add the General-family extra terminator. Actual inspected values give `A` primary `60` in significant class `G`, `é` primary `66` and extra nibble `4`; consequently `Aé` is `7F 60 66 02 40` ascending and `80 9F 99 FD BF` descending. Keep this family available wherever the admitted file's descriptor calls for it. [^S-JACK-GENERAL97-PIN][^S-JACK-RES-GEN97][^S-JACK-RES-GEN97-MAP]

## 6. Long-key shortening and bounded construction

The pinned `IndexData` shortens the **complete composite key before the locator is attached**. Up to 510 bytes it changes nothing. For a longer key `K`, it retains bytes `K[0..507]` and replaces everything after them with a two-byte digest. The digest consumes bytes `K[508..length−2]`: the very last byte is omitted. This is not a per-field length cap and can cut inside a primary weight or a later column. [^S-JACK-INDEX-PIN]

The exact arithmetic can be specified without porting the source's lookup table. Let a 16-bit register `R` start at zero. For each consumed byte `b`, perform eight polynomial shift steps on `R`: shift left once, retain sixteen bits and XOR `0x8005` if the previous high bit was set; after those eight steps XOR `(b << 8)`. Append `R` **low byte first** after the retained prefix. This is the byte-swapped orientation of the source's digest register. Its source table is built from the `0x8005` MSB-first polynomial and byte-swapped transitions. Calling an arbitrary library's routine named CRC-16/ARC is insufficient: feed position, initial value, reflected orientation, excluded last byte and final byte order must all match.

Independent bit-by-bit fixtures:

| Full key | Full length | Source-derived stored key |
|---|---:|---|
| General `A` repeated 253 times, ASC | 509 | unchanged |
| Same field followed by an ASC Null field | 510 | unchanged |
| General `A` repeated 254 times, ASC | 511 | first 508 bytes, then `0F 81` |
| General `A` repeated 255 times, ASC | 513 | first 508 bytes, then `27 A5` |
| Synthetic 508 zero bytes then `AA BB CC` | 511 | 508 zero bytes then `FC B8`; `CC` is excluded |

A sharp compound edge follows from the actual source loop. `(General A×255 ASC, Long 1 ASC)` and `(General A×255 ASC, Long 2 ASC)` differ only in the composite key's last byte. The source excludes that byte and produces identical shortened keys: the same first 508 bytes followed by `91 58`. This is evidence of the **pinned reference's behavior**, not proof that ACE makes the same distinction disappear. It is an immediate native deciding probe.

These lossy keys cannot establish semantic equality or order beyond the retained prefix. Even if native ACE confirms the shortening mechanics, CalcDb must not infer that equal digests imply equal rows, or that digest order is the required full text order. Equality operations retain residual row comparison wherever the qualified native behavior calls for it; the planner can use the index to generate candidates but can claim that it supplies complete semantic order only when the requested values and profile have that proved property. DAO traversal of a selected native index still has to reproduce its observable ordering. Unique constraints require their own captured collision policy: the reference checks equal encoded user-key bytes without a residual comparison, but CalcDb must neither invent stronger uniqueness nor inherit a false-positive rejection from a reference limitation.

R-SORT-3 therefore includes 509/510/511-byte boundaries; first-508-byte equality with reversed semantic tails; controlled digest collisions; the omitted-final-Long-byte pair; a cut through a multibyte weight; and a cut through an earlier field in a mixed-direction compound key. Capture native bytes, duplicate acceptance, DAO Seek equality/ranges, index walking and SQL ORDER BY results both with and without index participation. Establish whether native output agrees with the pin before any writer depends on this shortening rule.

### 6.1 Original CalcDb key construction

Use a typed scalar view over the already validated row or a coerced pending value. Fixed scalars stay in integer/raw-bit/decimal form; text is a borrowed UTF-16 span; no temporary public VARIANT or BSTR is needed inside the encoder. The descriptor chooses the codec once per prepared index, not once per row. The following is original design pseudocode, intentionally exposing ownership and failure boundaries rather than copying reference control flow:

```text
make_key(index_plan, borrowed_values, scratch) -> Result<KeyView>
    require values cover each indexed physical column
    require index_plan.profile is exact for its native descriptor
    checkpoint = scratch.mark()
    sink = scratch.begin_key(index_plan.maximum_pre_finish_output)

    for field_plan in index_plan.fields_in_index_order:
        value = borrowed_values[field_plan.physical_column]
        admitted = admit_index_value(field_plan, value) ?
        if admitted.is_null:
            sink.put(field_plan.null_marker) ?
        else:
            sink.put(field_plan.nonnull_marker) ?
            field_plan.emit_payload(admitted.borrow(), sink,
                                    scratch.text_auxiliary) ?
        scratch.note_field_end(sink.logical_length)

    result = index_plan.profile.finish_composite(sink) ?
    return scratch.freeze_view(result)  // invalidated by next scratch use

on error:
    scratch.rewind(checkpoint)
    return error                       // no page image has changed
```

The page writer consumes the immutable view or copies it into its mutation batch before scratch reuse. Every length addition is checked. A text encoder's temporary bound derives from 255 input units, the selected profile's maximum expansion and its auxiliary streams, not from the final shortened size. The inspected resource metadata alone shows General extended primary records as wide as eight bytes and secondary records as wide as three; allocating only 510 bytes before finishing is not safe. A reusable per-cursor or per-batch arena avoids allocations on common DAO row loops. It contains primary output plus secondary/later streams and at most ten field offsets; bounded growth happens before page mutation.

If the native shortening rule is confirmed, its final sink can retain the first 508 bytes and a small pending suffix/register rather than materializing the entire composite key. Keep the last pending emitted byte out of the digest until another byte arrives, since the pinned rule excludes the final byte. Still retain enough bytes to return unmodified keys of length 509 and 510. This streaming sink is a CalcDb implementation choice; the externally visible rule is determined by the profile. Text level construction may still need a bounded auxiliary buffer or a second pass over its borrowed span.

Costs are explicit: integer fields write 1+width bytes and require no heap allocation; NUMERIC writes 18; GUID 19; Extended Date/Time 55; a null writes one. Text work is linear in admitted UTF-16 units plus emitted weight bytes, and its table access pattern depends on the touched BMP blocks. A table representation may use a direct BMP directory into deduplicated immutable record blocks to avoid tens of thousands of individually allocated handlers; size, cache misses and deduplication savings are measurements, not assumptions. The same encoder serves incremental mutation, bulk build and seek preparation, with independent native corpus checks so a shared implementation error cannot validate itself. An unparsed indexed field cannot be omitted from the compound key, even though the pinned reference has such a RawData fallback. CalcDb either retains an already verified unchanged key for an unrelated edit or reports the missing codec before any relevant key mutation.

## 7. Traversal, ranges and DAO Seek

Three comparison domains remain separate:

| Domain | Comparison and purpose |
|---|---|
| User key | Native encoded index-key bytes, under the admitted descriptor/long-key rules. Used for index duplicate groups and the source uniqueness check. |
| Full entry | User key first, then the unsigned row-page and row-slot locator. Used for physical tree order, exact deletion and high fences. |
| Stored page fragment | Prefix plus entry suffixes and, on a node, child pointer. Used for byte packing only. Neither the common prefix nor the child pointer changes comparison semantics. |

The reference comparator first compares user-key byte arrays and only then `RowIdImpl`. It does not compare a node's child page as a tie breaker. A freshly reconstructed full-entry span must retain the key/locator boundary; a generic `memcmp` of arbitrary concatenations is justified only where the admitted encoding proves the same order. [^S-JACK-INDEX-PIN]

### 7.1 Typed internal bounds

Use an internal bound type rather than serializing fictitious locators:

| Bound | Meaning |
|---|---|
| `BeforeAll` / `AfterAll` | Before/after every real entry. |
| `KeyLow(K)` / `KeyHigh(K)` | Before/after every locator whose complete user key equals K. |
| `Full(E, inclusive)` | At or strictly after a particular complete entry. |
| `FieldPrefixLow(Q)` / `FieldPrefixHigh(Q)` | Before/after every complete key whose first specified indexed fields equal Q, where that range is supported by the admitted native key grammar. |

An equal-key scan is `[KeyLow(K), KeyHigh(K))`. A compound-field prefix ends at an actual indexed-field boundary. It is not an arbitrary UTF-16 prefix, a text byte prefix, or “append `FF` to a key.” Descending fields are already represented by the descriptor's native encoding. A prefix involving truncation/digests or another nontrivial native transformation needs an explicitly proved range lowering and any native candidate checks; a generic byte-prefix trick is insufficient.

The pinned source exposes MIN/MAX search sentinels and FIRST/LAST row-id sentinels, but the CalcDb bound types are original internal representations. DAO's omitted trailing Seek arguments and the meaning of `<`, `<=`, `>` and `>=` are lowered separately from public semantics. In particular, the official method documentation's omitted-argument Null rule must not be replaced by this internal prefix-range operation. Public directional/duplicate-positioning conflicts go to **R-CURSOR-1/R-DAO-1**.

### 7.2 Bounded descent and retained cursor

`IndexView` binds one physical index, its compiled descriptor, root identity and visibility/source context. `ReadIndexPage` returns an immutable pin plus a checked offset directory; entries are borrowed prefix/suffix views over that pin. A caller cannot retain such a span after releasing its pin. Parent page numbers are traversal state: the inspected cache explicitly says its parent pointers are not persisted. Do not invent an on-page parent field. [^S-JACK-IDXCACHE-PIN]

```text
LowerBound(index_view, bound, budget):
    page_id = index_view.root_page
    path = budget-owned empty vector
    seen = budget-owned bounded page-id set
    expected_child_context = root context

    loop:
        charge one descent
        require seen.insert(page_id) reports a new page
        pin, page = ReadIndexPage(index_view, page_id, budget)
        validate kind, physical-tree ownership, expected_child_context,
                 offsets, entry order and relevant references

        if page is leaf:
            slot = first entry E for which CompareEntryToBound(E, bound) >= 0
            return CursorPosition(move(pin), slot, move(path), index_view.id)

        slot = first explicit high fence H with CompareEntryToBound(H, bound) >= 0
        if slot exists:
            edge = explicit child at slot
        else if page has tail:
            edge = tail child
        else:
            require at least one explicit child
            edge = last explicit child

        validate edge address and no cycle
        expected_child_context = parent/edge kind and level expectations
        append owned parent-image/edge identity to path
        page_id = edge.page_id
        release pin unless caller's bounded mutation path retains it
```

On error, descent releases its tentative pins/path/visited set and returns a typed failure; it performs no native mutation or cursor-state publication. Successful return transfers the leaf pin and path to its result owner. The function can return `slot == leaf.entry_count`: it found an insertion boundary at the end of that leaf. `FirstInRange` advances over a next leaf when necessary, then compares the candidate against the upper bound. Such continuation must check the same view, expected peer relationship, ordering and finite traversal budget. A malformed cycle or unreadable child is an error, not a successful `NoMatch`.

Do not use an arbitrary native “maximum tree depth” without evidence. A bounded resource limit and a visited-page check prevent unbounded work; resource exhaustion is not reported as proof of file corruption. The pinned source tolerates an interior level byte of zero as absent metadata and derives the level from a child. An unconditional “node level zero is corrupt” rule is therefore too strong until **R-FMT-2** establishes it for the selected profile. [^S-JACK-IDXCACHE-PIN]

```text
NextInRange(cursor, upper, budget):
    require cursor descriptor and source identities remain usable
    if cursor's public population policy requires refresh:
        bound = policy.next_candidate_bound(cursor.owned_anchor, cursor.state)
        replacement = LowerBound(refreshed_admitted_view, bound, budget)
        validate replacement before retiring the old position/pin
        publish replacement under the adapter's evidenced failure policy
        cursor = move(replacement)
        // already positioned at a candidate; do not increment again
    else:
        advance slot within current immutable leaf
    while slot is past current leaf:
        if next link is zero: return End
        acquire/check next leaf under the same admitted visibility
        validate peer linkage and increasing full-entry order
        replace leaf pin; set slot = 0; charge page/cycle budget
        if empty leaf is admitted by this read profile: continue

    if CompareEntryToBound(entry, upper) >= 0: return End
    return borrowed EntryView plus stable logical-root locator
```

The refresh policy returns the next candidate boundary, not a promise that the old current entry still exists. In the common unchanged-order case it is strictly after the retained full-entry anchor. If that anchor was deleted, lower_bound already selects the first greater entry; incrementing again would skip it. Changes in a row's key or population follow the separately evidenced cursor policy. Restoring a candidate and advancing a retained current position are distinct transitions.

Reverse traversal mirrors this with previous links and an upper-bound predecessor. It is not automatically the public DAO reverse-Seek algorithm. The public adapter selects population, refresh, deleted-current-row, bookmark and duplicate positioning rules from R-CURSOR-1. A stable internal anchor contains the full key, logical root locator and the requisite file/table/index incarnation and view identities; it is not a published DAO bookmark representation. If a root row overflows onto another page, the anchor continues to name the root. A slot number alone is invalid after page/slot reuse.

The source cursor's modification counter and current/previous entry anchors provide a useful mechanism observation: it detects local index changes and re-finds bounds and positions. That counter is not an external-ACE invalidation protocol and cannot replace II-03's source/visibility model. [^S-JACK-INDEX-PIN]

For actual height H, at most Np entries on a page, and at most K compared bytes per entry, descent costs O(H) page acquisitions plus O(H log Np × K) comparisons after offset directories are ready. A newly decoded page additionally scans its B-byte envelope/mask and checks ordering, whose byte cost is O(Np×K) in the conservative model. A retained leaf cursor advances in O(1) position work between boundaries; copying an owned key anchor costs O(K), and entering another leaf may cost one acquisition plus O(B plus ordering work) validation unless its checked directory is cached. This does not include fetching the table row or following its LV/overflow graph. Keep the offset directory and common prefix once per immutable image; do not allocate an object and independent key buffer for every comparison.

### 7.3 DAO lowering and a documentary conflict

DAO chooses an existing logical index for a table-type Recordset. That changes its access order without physically sorting the base table. The physical tree may also serve another logical definition, while foreign-key behavior and public properties belong to those definitions. Consequently a successful tree lookup returns an internal row-root reference; the ISAM/object policy decides position, `NoMatch`, subsequent movement and observable errors. The index codec does not manufacture a DAO bookmark from its four locator bytes.[^ms-index]

The internal bound API distinguishes `ExactUserKey(K, locator−∞)`, `ExactUserKey(K, locator+∞)`, and a typed leading-column range. The infinities are tagged comparison states, never fake page numbers or persisted bytes. A range over leading columns also needs proof that the chosen encoding preserves that range; truncation and collation equivalence can require a wider candidate set with residual checks. A BSTR byte prefix is not that proof.

For **DAO `Seek`**, omitted arguments require their own lowering. The Microsoft method reference says omitted trailing keys default to Null and recommends `>=` when trailing fields are omitted. A separate Microsoft tutorial limits such omissions to `>` or `<`. The method reference also describes reverse searching for `<`/`<=`, including an arbitrary starting entry among duplicates at the end, whereas the tutorial describes searching from the beginning. These are documentary conflicts, not permission to select whichever answer is easier. Provisionally implement the method reference's directional model and explicit omitted-Null arguments, then let the initial oracle's R-CURSOR-1/R-DAO-1 cases settle each mismatch before claiming that behavior compatible.[^ms-seek][^ms-seek-guide]

The provisional lowering does not imply that every comparison operator on a mixed-direction compound index is a single raw-byte `lower_bound`. Capture the native argument coercion, relation tested, chosen duplicate and scan direction separately. A missing first or middle argument, an explicit Null, an omitted trailing argument and a type-mismatched Variant are different inputs. On failure retain the evidenced `NoMatch` and invalid-current-position policy; do not promise that the prior bookmark remains current merely because the tree search used a temporary cursor.

## 8. Constraints and a prepared row/index change

`IndexImpl` is a logical definition whose descriptor contains an index-data number. Several such definitions can refer to the same `IndexData`; its primary-key state includes the fact that any applicable logical definition is a primary key. A row operation therefore builds **one tree delta per physical index**, while retaining every relevant logical constraint and descriptor identity. Iterating logical names and inserting repeatedly into the shared tree would create duplicate entries and wrong counts. Foreign-key checks remain logical relationship checks even when their backing storage is shared. [^S-JACK-INDEX-PIN][^S-JACK-IDXIMPL-PIN][^S-JACK-TABLE]

The pinned flags are Unique `01`, IgnoreNulls `02` and Required `08`. These are not interchangeable with DAO property values or column direction flags. The inspected add path behaves as follows:

| Situation | Reference behavior; native matrix still required |
|---|---|
| All indexed fields Null and IgnoreNulls set | No entry is added. |
| At least one field Null and index is primary or Required | Reject, except that the earlier all-null IgnoreNulls return takes precedence in that source path. |
| Unique/primary, not all-null, another entry has equal user-key bytes | Reject even though row locators differ. |
| Unique but all-null key and no earlier prohibition | Duplicates are permitted by this source's check. |
| Partially null compound key | Not omitted merely because some component is Null; normal Required and uniqueness checks can apply. |
| Two source strings with equal native key bytes | They are duplicate index values for this source check; binary BSTR inequality does not rescue a Unique insert. |

Null classification here is descriptor-specific. The source Boolean descriptor never classifies a value as an index Null; even a host-null input passes through Boolean conversion. Do not implement the matrix by counting only generic host-null values, or silently transfer this Boolean coercion to every DAO boundary. [^S-JACK-INDEX-PIN]

Do not promote that particular IgnoreNulls-versus-Required precedence into a native guarantee. Establish which property combinations can be created or reopened and how ACE treats them; a contradictory metadata combination cannot be resolved by guessing. Compare the complete errors and postfailure cursor/transaction state under R-SORT-3/R-FMT-2/R-DAO-1. The pinned long-key encoder can also shorten keys to a digest-bearing form; its observed collision cases mean that equality of source-produced bytes alone is not sufficient evidence of complete native semantic equality or native Unique policy. R-SORT-3 must qualify the full long-key rule and any row-value verification before CalcDb adopts it. [^S-JACK-INDEX-PIN]

The four leaf locator bytes identify the row's logical header/root: u24BE page then u8 slot. The overflow body locator is a different four-byte representation, u8 slot then u24LE page. The pinned growing-update path prepares index changes with the original `rowId`, then places new final row bytes elsewhere and changes the header/root overflow pointer. A key-preserving move of final row data therefore need not change every leaf locator. [^S-JACK-TABLE]

### 8.1 The stored “unique entry count” is not current cardinality

The field named `uniqueEntryCount` is read from the physical index definition and written back to the TableDef. Its source comment says it counts unique entries added and is never decremented, as observed in Access. The implementation increments it for a nonduplicate add without an old-entry replacement; deletion does not decrement it, and updates can preserve it even when a key changes. This is evidence against treating it as “number of current distinct keys,” not complete proof of every native counter transition. [^S-JACK-INDEX-PIN][^S-JACK-TABLE]

Maintain separate concepts: current entry/cardinality facts needed by an algorithm, the persisted native counter, table row count, and the in-memory mutation/invalidation generation. A split changes no logical row count. A physical tree shared by logical names has one native count transition. The preferred provisional counter policy preserves the native field and applies only the qualified transition rule; it does not recompute it during ordinary delete, root cleanup or page compaction. R-FMT-2 must test duplicate versus new-key insert, deletion of one/last duplicate, reinsertion, changed-key update, rollback, create-index and native compact/rebuild. Microsoft explicitly documents that DistinctCount may lag rollback and unique-key deletion; it is not a promise of current exact cardinality. The public property's Refresh/reopen behavior remains a separate native probe.[^ms-distinct]

Microsoft documents a unique multicolumn index as constraining the whole combination, and describes `IgnoreNulls` and `Required` separately. Its SQL syntax exposes `PRIMARY`, `DISALLOW NULL` and `IGNORE NULL`; broad wording about Null fields does not resolve all-null versus partly-null combinations. The pinned implementation supplies specific candidate rules, but the valid-property and error-precedence matrix must be measured. A field's Required/default/validation semantics are also not reducible to an index flag.[^ms-unique][^ms-null][^ms-create]

The accepted field/index matrix must be explicit. Microsoft's current Indexed-property reference excludes OLE Object, Calculated and Attachment fields. Having a row codec or a calculated result of an otherwise indexable scalar type does not establish indexability. Ordinary supported scalar fields, compatible older files and later complex metadata need their separately evidenced creation, read and mutation rules. An unmodelled combination remains a visible missing case; a writer must not invent an index over a calculated wrapper.[^ms-indexed]

### 8.2 Original prepared insert/update

All coercions, value production and user-visible effects have already run in the established semantic order. Preparing a new tree layout must not invoke them again after a dependency mismatch. Accepted row values and their root locator enter this layer; exact key encoders return bounded scratch views or a precise failure. Before another encoder call can reuse that scratch, copy or transfer each retained key into the mutation batch's owned arena. This is one bounded ownership transfer, not a requirement for a separate heap object per field. Before-images and newly owned images never alias mutable backing.

```text
PrepareInsert(row_plan, descriptor_set, view, budget):
    bind accepted values and planned logical-root locator
    collect each affected physical index exactly once
    capture descriptor, row, tree-image and allocation dependencies
    allocation_plan = row_plan.shared_allocation_plan
    overlay = private unpublished workspace using that reservation namespace

    for each physical index in the qualified observable validation order:
        evaluate its admitted Null membership and required-value rules
        if row has no index membership: continue
        Kview = EncodeNativeKey(index descriptor, accepted values, scratch, budget)
        K = overlay.own_key(Kview, budget)  // before any scratch reuse
        E = FullEntry(K, row_plan.root_locator)
        position = LowerBound(view plus overlay, Full(E, inclusive), budget)
        require no preexisting identical full entry except an explicit restore case
        if uniqueness applies:
            inspect the equal-user-key candidate range across leaf boundaries
            exclude only this operation's exact old entry, when updating it
            reject a conflicting row before any native publication
        insert E into the owned logical leaf-entry sequence
        enqueue leaf, changed maximum and applicable native-count transition

    FinalizeIndexShapes(overlay, allocation_plan, budget)
    complete index/map allocation closure in the same allocation_plan
    resolve all symbolic handles and re-encode affected images if needed
    compose row, tree, ownership and catalog effects by physical page and phase
    verify exact keys, references, capacity, preservation and expected dependencies
    transfer owned images/reservations into the enclosing MutationPlan
```

`PrepareUpdate` first derives old/new membership and full entries from one retained old row and the accepted replacement values. Own the old key before encoding the new one, and own both before their scratch can be reused. No-op key/locator pairs need no leaf edit. A key change removes the old entry and inserts the new entry in the same unpublished overlay, so uniqueness sees the correct hypothetical replacement state without exposing a momentary deletion. An overflow-body relocation with the same root and key does not become an index delete/reinsert. Remove/add operations that touch one physical page must share the correctly composed phase images, not independently clone the same stale page.

The source's “an identical full entry was already present” path logs and skips an insertion. CalcDb should not silently generalize that into success for a new application row. Treat it as a consistency/error case unless the enclosing operation explicitly requires an idempotent internal restore. The same applies to the source's fallback full scan by RowId when deletion lacks complete key values: normal CalcDb edits already retain the indexed fields they need. A bounded diagnostic scan can explain a malformed index, but it must not quietly mutate some row found by an incomplete key. [^S-JACK-INDEX-PIN]

At the publication boundary, the coordinator revalidates image/descriptor/allocation dependencies and uniqueness under the selected exclusion mode. Only a physical placement/image change with still-valid semantic inputs, key profiles and table/index incarnations permits bounded effect-free replanning. A changed collation, descriptor meaning, replaced index or input dependency returns to the coordinator's separately evidenced conflict/restart policy; it cannot silently reinterpret accepted values or replay UDFs/coercions. Reusing a public name does not preserve an index incarnation. Failure before publication releases the overlay, reservations and pins and leaves authoritative pages unchanged. Native write failures afterward belong to II-03's exact progress/uncertainty record and the transaction owner's evidenced behavior.

## 9. Recompression, split selection and root growth

**D-INDEX-1 — Provisional.** Keep native page forms, explicit inclusive maximum fences and a stable physical root; split from byte occupancy after prefix recomputation; retain nonempty underfull pages and unlink empty ones. This limits mutation footprint and has a concrete reference basis. It holds only where R-FMT-2 finds native acceptance, R-CURSOR-1 finds compatible order/identity and R-CRASH-1/2 qualifies the prepared physical phases. Eager redistribution/merging and root contraction add pages and movement without an established early benefit; add them only if native validity/behavior requires them or measured byte/read savings justify an equally compatible variant. This decision changes II-03/04's mutation inputs, II-07's saved positions and the II-08 persistence plan, not their ownership or crash contracts.

For n ordinary stored entries of full lengths L0…Ln−1, a legal shared prefix of p bytes gives payload occupancy:

`packed_bytes = sum(Li) − (n−1) × p`.

The prefix is stored once as part of the first complete entry; later entries omit it. For the Jet 4/ACE source geometry, payload begins at 480, leaving **3616 bytes** on a 4096-byte page. Every exclusive cumulative entry end must also have a representable boundary-mask bit. A header tail contributes no ordinary entry payload. For an interior page, each explicit full entry includes the four-byte child pointer; the common prefix cannot be chosen by blindly including those child bytes. [^S-JACK-INDEX-PIN]

The codec can preserve an existing legal prefix that extends into the three row-page locator bytes. The pinned cache's fresh-prefix chooser, however, examines only the user-key bytes of the first/last real entries; it neither includes tail entries nor newly extends compression into locator bytes. These are different facts. The preferred provisional new-page choice is deterministic key-only prefix compression, with native-produced locator-prefix pages accepted and preserved where the edit permits; R-FMT-2 decides whether fresh-page policy needs to match a stronger native rule. Do not normalize a page solely because its prefix differs from that chooser.

### 9.1 A nine-byte insert can add 1210 packed bytes

**Figure F-34 — prefix collapse and a byte-balanced split.** Constructed occupancy and fence witness; not an observed ACE split or render-verified figure.

Construct a leaf holding ascending Long values 0 through 601, one entry per value. Each full entry is five key bytes plus four locator bytes. For value v use logical-root locator `(page 0x002000+v, slot 5)`; these are legal-width constructed locators, not a captured ACE file. The keys all share prefix `7F 80 00` (three bytes):

| State | n | Prefix p | Packed bytes | Result |
|---|---:|---:|---:|---|
| Original values 0…601 | 602 | 3 | `602×9−601×3 = 3615` | Fits, one payload byte free. |
| Insert −1, key `7F 7F FF FF FF` | 603 | 1 | `603×9−602×1 = 4825` | Does not fit. |
| Split left: −1, 0…257 | 259 | 1 | `259×9−258×1 = 2073` | Fits. |
| Split right: 258…601 | 344 | 3 | `344×9−343×3 = 2067` | Fits. |

The only common key byte after inserting −1 is `7F`. A test that adds nine to the previous packed length is wrong. The previous final boundary was 3615, encoded at mask byte 451, bit 7; the unsplit new boundary 4825 has no page representation. The chosen split's final payload boundaries are 2073 and 2067. Free-byte counters for these constructed leaves are 1543 (`07 06` little-endian) and 1549 (`0D 06`).

A half-count cut of 302 versus 301 entries would occupy 2417 versus 1809 bytes. The 259/344 split minimizes the maximum packed occupancy, with the imbalance tie breaker choosing it over 258/345. Counts need not be close when one side compresses better. This arithmetic was independently checked by the accompanying original Python witness; it is not a benchmark or native experiment.

Suppose the existing leaf page `0x0100` retains the right side and new page `0x0120` holds the left side. Its new high fence is value 257, key `7F 80 00 01 01`, locator `00 21 01 05`. The complete new node entry is:

| Field | Exact bytes |
| --- | --- |
| Long 257 key | `7F 80 00 01 01` |
| Logical row-root locator | `00 21 01 05` |
| New left child | `00 00 01 20` |

The right child's original maximum 601 and original page identity remain unchanged in this example. A parent therefore gains the left fence; it does not replace the high fence with the first key of the right leaf. If the original right child was represented by a header tail, it remains that tail while the new left child acquires an explicit high-fence entry.

### 9.2 Original shape finalizer

```text
FinalizeIndexShapes(overlay, allocation_plan, budget):
    worklist = pages whose entries, children, maxima or peer links changed
    while worklist is not empty:
        charge bounded work; take deepest pending page
        normalize empty/one-child/tail form before measuring it
        derive its ordered real entries and optional tail from native child rules
        recompute an admitted common prefix and every exclusive boundary

        if page fits:
            encode owned complete page image
        else:
            evaluate legal contiguous partitions by actual packed byte size
            prefer two nonempty pages minimizing max occupancy, then imbalance
            reject any intrinsically unencodable entry
            if a batch needs more pages, produce a bounded admitted partition
            keep the old page as the right partition
            reserve stable new left page identities in allocation_plan
            set all affected peer edges and parent high fences
            enqueue affected parents and boundary peers

        propagate changed subtree maximum, even if this page's bytes did not change
        requeue any page whose later child/peer/shape change invalidates its image

    return tentative shapes/images and any unresolved allocation references
    // caller freezes only after this worklist and common allocation closure settle
```

Tail demotion can add an explicit fence and alter both prefix and occupancy; it must precede the final capacity check. A previously encoded page changed again by ancestor/peer normalization is measured and encoded again before the plan freezes. Both shape work and subsequent map self-allocation extend one II-04 reservation namespace. Assigned page IDs remain reserved exactly once; unresolved handles are resolved before final byte encoding and freezing. Final memberships, capacity and every child/tail reference are checked after that closure. No second allocator is allowed to reuse a page reserved by the tree finalizer. This is an original byte-balanced policy. The pinned reference explicitly says its split is a simple half-entry split and moves the leading half into a new preceding page. Its functional behavior is not a reason to copy the count-based split or its write order. Evaluate candidate prefix lengths and cumulative entry lengths without making one full key allocation per candidate; bounded prefix/suffix summaries can make all cut evaluations linear in the candidate entries and compared bytes. If the profile does not justify deriving a prefix from endpoints alone, calculate it over all eligible entry spans.

One insertion normally affects a root-to-leaf path and adjacent peers, but the planner must not encode a false “exactly one new page per level” allocation assumption into its safety proof. Batched changes, large fences and recompression can enlarge the closure. Bound total pages/bytes/work explicitly; each emitted page must fit before publication. Let M be total bytes inspected/rebuilt across affected pages, S the new/changed page count and H the actual touched depth. The physical planning cost is O(M plus candidate-key comparison work), with O(S×B plus retained source/entry metadata) output ownership. The admission/reservation budget bounds the exceptional expansion.

### 9.3 Root and peer mechanics

Preferred provisional root growth preserves the native root page number: place its old contents into newly reserved children and make that existing page an interior root. The pinned source nests old root contents in a new child, then splits that child. CalcDb may construct the same final shape privately without imitating those in-memory steps or their file writes. Root growth must include ownership-map changes, new sibling edges, high fences and the root's type/level image. [^S-JACK-IDXCACHE-PIN]

Leaf next/previous links span the complete leaf sequence. The inspected cache's interior peer links do **not** cross parent boundaries: splitting a parent separates the child-node peer chain at that boundary. Leaf links remain continuous across it. “Link every page at one level into a global list” would therefore be an unjustified native format rule. Include the changed peer boundary pages in the mutation closure. Parent identity in memory can change without a nonexistent on-disk parent-pointer write. [^S-JACK-IDXCACHE-PIN]

Tail policy is also specific: the source promotes the last explicit child to header-tail form only when there are multiple children and that child is a leaf; with exactly one remaining tail child it demotes it to an ordinary explicit entry. An existing native shape with different tail usage must be read/preserved according to an evidenced profile, not rejected simply because the reference's fresh writer would choose another form. [^S-JACK-IDXCACHE-PIN]

## 10. Maximum propagation and deletion

Use §3.3's three-child fixture. `P` has explicit high fences Long 10 at `(0x001234,7)` → `0x0100`, Long 20 at `(0x001235,9)` → `0x0101`, and tail `0x0102`. The child entry sets are `{5,10}`, `{15,20}` and `{25,30}`; their exact key/locator construction is specified in §3.3.

Delete 20. Child `0x0101` now has maximum 15 at `(0x001235,3)`. Replace P's corresponding explicit entry:

```text
old: 7F 80 00 00 14  00 12 35 09  00 00 01 01
new: 7F 80 00 00 0F  00 12 35 03  00 00 01 01
```

P's overall maximum is still the tail's 30, so its ancestor fence need not change. The child pointer remains unchanged while both key and row-slot parts of the fence change.

**Figure F-35 — a changed subtree maximum through an unchanged page.** Constructed dependency example; emitted prose/byte table, not ACE-confirmed or render-verified.

Now delete 30 from tail leaf `0x0102`. That leaf retains 25 at `(0x001236,1)`. P's two explicit entries and header tail pointer do not change, so P can have **identical serialized bytes** before and after this second deletion. Its subtree maximum nevertheless changes from 30 to 25. If P itself is represented by an explicit high fence in its parent, that ancestor entry must change from `7F 80 00 00 1E 00 12 36 02` to `7F 80 00 00 19 00 12 36 01`, keeping the same child page pointer for P. Stopping propagation at an unchanged serialized parent page loses this update. [^S-JACK-IDXCACHE-PIN]

| Object | Before | After deleting tail maximum30 | Required action |
| --- | --- | --- | --- |
| Tail leaf0x0102 | Maximum `30@(0x1236,2)` | Maximum `25@(0x1236,1)` | Repack its entries. |
| P, the direct parent | Two explicit fences10/15; tail pointer0x0102 | The same serialized fields | Update its in-memory subtree-maximum summary. |
| Ancestor with an explicit fence for P | Key/locator for30 | Key/locator for25 | Replace that fence and recheck compression/capacity. |

For variable-sized keys, deletion can replace a short maximum with a lexicographically smaller but longer predecessor and can alter an ancestor's common prefix. Consequently even a delete can make an ancestor require repacking or splitting. Use the same byte-capacity finalizer after insert, update and delete; “deletes only shrink pages” is not a valid general argument about ancestor images.

### 10.1 Preferred deletion policy and algorithm

**Retain nonempty underfull pages; unlink truly empty non-root pages; propagate empty-node removal; keep the root page and turn it into an empty leaf when the tree empties. Do not introduce general borrowing, merging or single-child root contraction in the first native writer.** This is the preferred provisional direction for R-FMT-2, R-CURSOR-1 and R-CRASH-1/2, not an assumption of a universal half-full B-tree invariant.

That direction is consistent with the inspected reference's empty-page cleanup and lack of a general merge path. It reduces the number of unrelated page identities and peer links changed by an ordinary delete. Native evidence changes it if retained shapes violate native validity/repair behavior or if the required native operation has additional observable consequences. Legitimate different page placement or fill ratios alone are not failure, provided the book's independent native reopening and “nothing to fix” comparison accepts the structure. A supported compact/rebuild operation can repack live entries as its separately tested behavior.

```text
PrepareDelete(root_row, retained_old_values, view, enclosing_change, budget):
    allocation_plan = enclosing_change.shared_allocation_plan
    overlay = private unpublished workspace in that namespace
    for each applicable physical index once:
        derive old membership and exact full entry using the root locator
        if old membership is absent: continue
        locate exact full entry under the retained view plus overlay
        require it matches the expected logical row and descriptor
        remove it from the private leaf sequence
        if leaf remains nonempty:
            retain page identity; recompute packing; propagate new maximum
        else if leaf is root:
            prepare empty leaf image at the same root page
        else:
            remove its explicit parent edge or header-tail edge
            patch previous.next and next.previous within the correct peer scope
            enqueue parent cleanup; plan retirement only after incoming edges vanish

    recursively remove empty non-root nodes
    convert a one-child tail to an explicit child under the native profile
    retain other single-child/nonempty underfull nodes provisionally
    FinalizeIndexShapes(overlay, allocation_plan, budget)
    complete the shared closure and resolve/re-encode all affected references
    attach native count policy, row deletion and map retirement effects
    return one owned enclosing mutation plan
```

Removing a header tail can expose the last ordinary child as a new tail where the admitted profile requires it; if only one child remains, represent it explicitly. A page cannot be freed while an explicit parent edge, tail edge or peer link still names it in the relevant final state. Old immutable images may also need retained backing for existing readers, rollback and pending loads. Their retention is distinct from live native ownership. II-04's owner/global map and reuse rules apply; one zero entry count in a cache object does not itself authorize physical reuse. [^S-JACK-IDXCACHE-PIN]

The source's `write()` calls empty-page deletion before writing remaining modified pages, and `deleteDataPage` calls the page allocator's deallocation method. This is precisely the sort of reference order **not** to import as an ACE crash protocol. CalcDb compiles unlink/ownership/reuse transitions into the qualified phase graph. On a crash, the next ACE/Access opener must see a state within the specified ACE parity, whether or not CalcDb ever reopens first.

## 11. Bounded bulk index construction

Bulk work reuses the same exact field encoders, user-key grouping, full-entry order, root locators, count policy and native page codec. Sorting by a host locale, final overflow-body page number or BSTR pointer would produce a different tree. Run sorting and merging are workspace operations; spilled runs are not a recovery log and are not required for an ACE-first opener.

```text
PrepareBulkBuild(retained_rows, physical_index_descriptor, view, budget):
    frontier = begin the operation's qualified evaluation/constraint policy
    for each logical row permitted by frontier:
        retain its logical-root locator and accepted indexed values
        apply admitted Null membership and required-value rules
        E = emit exact bounded (user_key, root_locator, native_ordinal) record
        frontier.check_before_advancing(E, budget)
            // includes accepted-prefix duplicate checking when native order requires it
        own/copy accepted E into the bounded run before key scratch reuse
        spill an owned run when needed

    settle all frontier-owned validation/effects in the qualified order
    merge accepted runs by full-entry order
    verify equal-key groups under the same admitted Unique/Null/collision rules
    preserve native failure/provenance selection for deferred native checks
    partition ordered entries using exact recompressed byte capacities
    build linked leaves and their maximum full-entry summaries
    build parent levels using explicit high fences and admitted tail policy
    preserve/prepare the designated root identity and native metadata
    reserve complete page/map/catalog closure and retained before-image backing
    return owned unpublished images and the qualified native publication program
```

Bulk sorting must not postpone a required early constraint failure past later value decoding, conversion or user effects. For a native row-order policy, use a bounded accepted-prefix lookup over memory and owned indexed runs: check each accepted entry before advancing the semantic frontier. This lookup uses the same exact-key/collision rules and adds measured I/O; it is not a second authoritative database. If captures establish a deferred sort-then-check policy, record candidate failures with their native ordinals and apply that exact selection, while preserving any earlier observable effects. Pure parallel preparation is permitted only where reordering cannot alter accepted results, effects or errors. A duplicate at row2 must not be hidden by a later row3 codec failure merely because the bulk path sorts at the end.

For N included entries and K total encoded bytes, comparison sort has O(N log N) comparisons, with byte comparison work determined by keys; materialization and final page emission are O(K plus page/map output). External merge costs depend on the memory/run budget and number of merge passes, which must be reported rather than hidden in “linear build.” Pinned source population currently loops over rows and prepares ordinary adds; the sorted-run builder above is an original CalcDb design, not a claimed ACE or Jackcess bulk algorithm.

A create-index operation may publish the new descriptor only after its native page/ownership state is appropriate; a rebuild of an already reachable index has a different old-root/reference transition. Do not infer that either is an atomic root-pointer swap. An existing root-page number or descriptor may need preserved identity, and old image ownership remains explicit. The exact phases, errors, count initialization, transaction effects and interrupted-build states go to R-FMT-2/R-CAT-1/R-TXN-1/R-CRASH-1/2. A streaming builder may prepare earlier pages privately, but no early native publication is justified by the availability of sorted input. Before returning, transfer every run, page image, reservation and retained source obligation to the enclosing operation owner or release it. Budget/cancellation/I/O failure settles owned runs and reports its exact stage; no untracked worker or borrowed row span survives the call. Native publication failures use II-03 receipts rather than restarting the build.

## 12. Native page phases for index changes

For a split of a non-root leaf, the physical closure commonly includes new index page(s), the original leaf, its previous/next peers as affected, parent/ancestor images, index-owned map pages, global allocation pages, and any TableDef native-count image. For a row key change, it also includes the old/new row/LV/overflow graph from II-04. Several roles may share one physical page. A node split can additionally alter a child-node peer boundary even where the children's data entries stay unchanged.

The preferred provisional direction under **D-WRITE-1** is to prepare complete new targets, establish the evidenced allocation/ownership state before making them reachable, publish the required child/fence/peer/row changes, and retire old references/storage according to the qualified native program. This describes dependencies to test, not a guessed universal linear write sequence. Split publication can require multiple phase images of an original page; a final-image-only dirty set cannot express them. An incoming parent link, a sibling link and a row/key update can have different crash meanings and must appear in the write-point matrix.

No native commit marker, repair flag, “safe full page write,” fsync order or replacement WAL is invented here. R-CRASH-1/2 supplies commit-survival and `dbForceOSFlush` behavior and any native needs-repair state. If complete ACE tracing is impossible, the book's bounded exclusive writer direction still applies: enumerate CalcDb cuts, bracket native kills, compare independent next-openers and leave unsupported transition claims explicit. Optional private undo does not justify a page order that is unsafe for ACE-first reopening.

### 12.1 The same index page needs two different output images

**Figure F-36 — provisional indexed Long 42→43 update.** Constructed phase-image table under D-WRITE-1; not an observed ACE write sequence or a proof of crash safety. All referenced rows/pages are assumed already owned and admitted; this example allocates no new pages.

Let leaf root page 0x90, owned by TableDef 0x20, contain two ascending Long entries:42 at row root `(17,0)` and50 at `(18,2)`. The full entries are `7F 80 00 00 2A 00 00 11 00` and `7F 80 00 00 32 00 00 12 02`. With a four-byte key prefix its payload is fourteen bytes, ending at 9 and14; bitmap byte 1 is42hex, and the little-endian free count is `12 0E`. Page17's one-Long row body is `01 00 2A 00 00 00 01`, using II-04's stored-column count and final nonnull mask.

| Prepared phase | Physical page / role | Exact changed representation | Dependency in the provisional program |
| --- | --- | --- | --- |
| Before | 0x90, original leaf | Payload `7F8000002A00001100 3200001202`; prefix4; ends9,14 | Retain immutable before-image. |
| Remove old index reference | 0x90, intermediate leaf | Singleton payload `7F8000003200001202`; prefix0; end9; bitmap byte 1=`02`; free=`17 0E` | Complete before publishing row43 in this candidate. |
| Change row | 17, ordinary data | Row `01 00 2B 00 00 00 01`; root locator remains `(17,0)` | No table row-count increment; preserve unrelated bytes. |
| Install new index reference | 0x90, final leaf | Payload `7F8000002B00001100 3200001202`; prefix4; ends9,14; bitmap byte 1=`42`; free=`12 0E` | Complete after row change in this candidate. |
| Required native bookkeeping / resolution | Actual affected pages and transaction state | Only evidenced count/stamp/marking changes; no invented on-disk commit word | Native ordinary/forced-flush survival evidence selects acknowledgement. |

The fourteen-byte final payload differs from the original at one key byte, but that fact does not authorize a one-byte native update or collapsing the two page 0x90 images. The proposed order intentionally exposes a different intermediate search structure. R-CRASH-1 must determine whether its interrupted states match the native baseline, or change the ordering/marking/physical spans. A source stamp, bitmap and free count belong to each complete image; their physical transfer granularity remains II-03's qualified choice.

No parent fence changes in this example because the root is a leaf. For a nonroot leaf, replacing a maximum may add ancestor changes from §10; splitting adds the new page, peers and owner/global-map closure from §9. A normal completed output must pass the independent ACE/Access/T-COMPACT-1 branches. An interrupted image is assessed against the corresponding ACE crash class, including the effect of dbForceOSFlush, without depending on a private undo file.

## 13. Cost model, preservation and independent validation

The hot representation is one compiled descriptor, one immutable page-image directory and bounded per-cursor or per-operation scratch. Cache the directory by physical index/profile and II-03 image identity. A recycled RAM address, changed page image or schema incarnation cannot reuse it. Do not add an independently unbounded index page cache beside the shared pager, or import the reference's fixed cache size as a workload target.

| Operation | Local work to measure | Storage and ownership cost |
| --- | --- | --- |
| Resident numeric Seek | Encode1+width key bytes, compare full-entry bounds and descend resident directories; no text materialization | Current leaf pin plus bounded path/anchor; cold misses add actual tree-page reads. |
| MoveNext within a leaf | Advance offset ordinal; decode locator; apply required public cursor policy | No key copy unless an owned anchor must survive movement; row fetch is separate. |
| Cross a leaf boundary | Acquire next image, validate context/order, switch pins | Brief two-pin overlap; full decode only for an uncached directory. |
| Indexed field update | Old/new exact keys, one delta per affected physical tree, uniqueness range checks | Complete affected images, before/history retention and potentially multiple images of one page. |
| Prefix-sensitive split | Recomputed prefix, boundary lengths, partition summaries, parent/peer closure | New native page(s), owner/global map patches, retained root identity and ancestor images. |
| Bulk build | Bounded exact-entry runs and merge, streamed page packing, constraint provenance | Peak run buffers, spill bytes/pass count, unpublished pages and native publication closure. |

For a cold descent of height h, count up to h demanded tree-page reads, plus any required descriptor/map admission, and then table/overflow/LV reads. Repeated DAO calls should amortize descriptor binding and page-directory construction. Track comparisons and bytes examined, heap allocations and retained capacity, key/row/page copies, cache misses, native read/write bytes, page-phase count, synchronization and actual COM crossings separately. Text table lookup locality and the cost of an owned BSTR return belong to different measurements. None of these counts is an invented benchmark result.

Returning a text field from its index weight bytes is generally impossible: case, trailing spaces and ignored units can be lost, and long keys can be shortened. A covering plan needs proof for every requested value and residual predicate. Even a COUNT plan must account for omitted all-null entries rather than assuming every physical index enumerates every table row. A scalar integer key can be reversible under a specific descriptor, but that alone does not prove the full query's native semantics or visibility.

Preserve all unmodified native bytes and references, including stamps, skipped physical/logical metadata, unknown flag bits and protected trailing residue. Repacking a covered entry stream does not authorize zeroing the rest of an existing page. New-page initialization and existing-page normalization are different operations. Inspect owner-map reachability independently of header owner equality; detect cross-tree references, duplicate child reachability, cycles and mismatched child maxima. A whole-tree audit verifies leaf order and sibling reciprocity, appropriate interior-peer scope, root/level shapes, masks/prefixes/free space and row-root reachability. It does not run on every successful point Seek.

Retain the simple scalar encoder as a comparison baseline for later batch/SIMD/table-layout specializations. Check complete output bytes, error order, memory ownership and serial-equivalent results before counting any speed change. The cleared report parts inform bounded reusable buffers, exact baseline comparison and reproducible differential evidence; their timings, SQLite page rules and runtime architecture do not become CalcDb requirements.[^method]

### 13.1 Exact source data and resource acquisition

The inspected resource files are facts used to form small discriminating examples; no whole table is reproduced in the chapter. Each has its own exact hash and acquisition URI. All five code-table resources contain only grammar records with no file-level licence header; the sparse mapping file likewise has only mappings. The repository's pinned `LICENSE.txt` is Apache-2.0; its `NOTICE.txt` discusses expression-engine financial functions and does not establish the origin or separate rights status of these sort-weight tables. The POM advertises Apache-2.0 at project level. Record that context without turning it into a blanket resource clearance. Before redistributing or incorporating a table, establish exact-material provenance/licence; an independently derived table from permitted native black-box captures is an alternative data-acquisition route, not a claim that the current resource files have been cleared. No reference code, tests or tables have been ported into CalcDb in this response. [^S-JACK-RESOURCE-LICENSE-CONTEXT][^S-JACK-RESOURCE-NOTICE-CONTEXT][^S-JACK-RESOURCE-POM-CONTEXT]

## 14. Acceptance and development slices

The following tests are designed acceptance cases. Only arithmetic/example checks were executed here, including some calculated fixtures not printed in full; the native cases remain planned. Each native case retains the common A-02 environment, immutable inputs and independent next-opener branches.

| Test | Case | Required evidence / failure exposed |
| --- | --- | --- |
| T-IDX-01 | Page/mask/endian witnesses | Decode the complete leaf/interior/empty examples; independently verify end bits 9/10/11 and13/22, full-payload boundary3616, little-endian headers and big-endian entry trailers. |
| T-IDX-02 | Prefix and entry bounds | Test prefixes through locator bytes and longer singleton prefixes; distinguish safe read, admitted key grammar and writable shape; reject impossible spans before allocation. |
| T-IDX-03 | Scalar directions | Check every scalar family, two NUMERIC modes, signed zero/negative DATE, binary chunk09 boundaries, component-distinct GUID and55-byte extended date keys against native entries. |
| T-IDX-04 | Text profiles and code units | Verify actual General/Legacy/97 weight examples, descriptor preservation, other required orders, accents/expansions/controls/packed flags and all surrogate boundaries. |
| T-IDX-05 | Long-key finish and collision | Test509/510/511/513-byte forms and the long-text/Long 1-versus2 pair; capture native keys, uniqueness, Seek and SQL order/residual behavior independently. |
| T-IDX-06 | Key construction failure | Unsupported indexed field/profile, malformed source value, bounded text expansion and allocation failure leave no partial key or page mutation; RawData cannot silently drop a field. |
| T-IDX-07 | Bulk build and operation errors | Early duplicate then later key error/effect; Required failure then later duplicate; duplicates whose key order differs from evaluation order. Preserve the native frontier, bounded runs/spill, metadata/rollback and normal interoperability. |
| T-IDX-08 | Preservation and repair sensitivity | Untouched index/TableDef spans and opaque references survive; independently calibrated T-COMPACT-1 distinguishes ordinary compaction from repair or remains inconclusive. |
| T-IDX-09 | Exact fence and next locator | Bound equal to a child maximum goes left; same key with larger locator goes right; child pointer excluded from key comparison. |
| T-IDX-10 | Duplicate group across leaves | KeyLow/KeyHigh and both directions cover native-equal keys; delete the current entry at a leaf boundary before refresh and retain the first surviving successor/predecessor without an extra advance. |
| T-IDX-11 | DAO omitted argument | Compare Null-default Seek, explicit Null, internal field-prefix operation and all operators on mixed-direction compounds; public positioning/errors from R-CURSOR-1/R-DAO-1. |
| T-IDX-12 | Root/body identity | Grow an indexed row repeatedly into overflow bodies; unchanged logical keys retain root locators; index and table enumeration return one logical row. |
| T-IDX-13 | Shared physical tree | Two logical definitions sharing data update once; distinct logical constraints still run; native count, ownership and root stay coherent. |
| T-IDX-14 | Null/flag matrix | Unique/Required/primary/IgnoreNulls, all-null versus partial-null, duplicate/case-equivalent keys, valid/invalid property combinations and first failure state. |
| T-IDX-15 | Packed split witness | 602 Long entries fit in 3615 bytes; adding −1 changes prefix and requires a split; verify exact boundaries/fences, not an entry-count threshold. |
| T-IDX-16 | Ancestor maximum propagation | Delete explicit-child maximum, tail-child maximum, and whole tail; verify ancestors even when an intermediate page's bytes are unchanged. |
| T-IDX-17 | Delete that grows a fence | Variable-size predecessor changes parent size/prefix; the delete path may require ancestor split. |
| T-IDX-18 | Root growth / empty tree | Force three levels; retain root identity through growth/deletion; demote a sole tail, remeasure/repack its added fence, and test evidenced one-child/shrink behavior. |
| T-IDX-19 | Leaf versus node peers | Split parent across node children; leaf links continue globally, node links respect evidenced parent scope; reverse traversal agrees. |
| T-IDX-20 | Underfull policy | Delete down to one live entry per affected page; ACE/Access independent reopening and calibrated Compact & Repair find no repair-required defect. Results decide retention/merge rules. |
| T-IDX-21 | Native count transitions | Insert new/duplicate key, delete one/all duplicates, reinsert, update key, rollback, create index and compact; compare raw native field and public DistinctCount. |
| T-IDX-22 | Failure and budget | Reuse one scratch across several indexes without altering owned old/new keys; fail loads/allocation before publication; distinguish physical replanning from a replaced descriptor/incarnation. |
| T-IDX-23 | Split/delete/build crash cuts | At every qualified page/span write and persistence frontier, compare ACE/Access next-openers and Compact & Repair copies, native needs-repair marking, file/lock artifacts and forced-flush survival. |
| T-IDX-24 | Malformed graph and level policy | Cycles, bad spans/owners/extents and duplicate full entries fail boundedly; stored zero interior levels receive the selected profile policy, without blanket corruption or false NoMatch. |

R-FMT-2 decides layout, root/tail/peer/count and deletion shapes; R-SORT-3 decides exact keys and equality; R-CURSOR-1/R-DAO-1 decides public movement/identity; R-CRASH-1/2 decides publication and survival; R-CAT-1/R-TXN-1 adds index creation/rebuild catalog and transaction consequences. No single passing round trip substitutes for those independent comparisons.

The implementation order within W-04/W-05 is now concrete: first inspect and traverse existing native pages; then qualify scalar/text keys and bounds; then prepare one-leaf insert/delete and row-root changes; then add prefix-sensitive splits, empty-node removal and stable-root growth; then bounded bulk construction. Error, ownership and malformed-input handling arrive with each slice. R-SORT-3 must precede successful key-writing claims; complete ACE crash tracing is not a blanket prerequisite for constructing the bounded exclusive writer. Qualify only supported operation/environment crash classes and keep unresolved cases visible, as I-08 requires.

The full experiment cards and branch dependencies are in A-02; they now include the exact key-length counterexample, mixed Null flags, counter transitions, no-tail nodes and parent-max propagation. The next chapter is **II-06 — Catalog, properties and QueryDefs**: native catalog bootstrap and records, property blocks, object/reference preservation, saved-query serialization and invalidation.

## Sources

[^ms-index]: S-MS-DAO-INDEX — Microsoft, [Recordset.Index property (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-index-property-dao), updated 2022-03-02; syntax/remarks read 2026-11-13. Logical access order and selected existing index; no target execution.
[^ms-seek]: S-MS-DAO-SEEK — Microsoft, [Recordset.Seek method (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/recordset-seek-method-dao), updated 2022-03-21; syntax/parameter/remarks read 2026-11-13. Omitted keys, directional search and failure state; explicitly compared with the conflicting tutorial.
[^ms-seek-guide]: S-MS-DAO-SEEK-GUIDE — Microsoft, [Find a record in a table-type DAO Recordset](https://learn.microsoft.com/en-us/office/vba/access/concepts/data-access-objects/find-a-record-in-a-table-type-dao-recordset), updated 2022-01-22; relevant omission/direction passages read 2026-11-13. Documentary conflict retained for native probes.
[^ms-unique]: S-MS-INDEX-UNIQUE — Microsoft, [Index.Unique property (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/index-unique-property-dao), updated 2021-09-14; remarks read 2026-11-13. Whole-combination uniqueness; detailed Null/collation policy remains a probe.
[^ms-null]: S-MS-INDEX-IGNORENULLS — Microsoft, [Index.IgnoreNulls property (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/index-ignorenulls-property-dao), updated 2022-03-18; remarks and Required/IgnoreNulls matrix read 2026-11-13. Does not settle every compound policy.
[^ms-create]: S-MS-CREATE-INDEX — Microsoft, [CREATE INDEX statement](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/create-index-statement-microsoft-access-sql), updated 2022-03-18; syntax/remarks read 2026-11-13. Directions, unique combinations and Null options, not physical encoding.
[^ms-distinct]: S-MS-INDEX-DISTINCT — Microsoft, [Index.DistinctCount property (DAO)](https://learn.microsoft.com/en-us/office/client-developer/access/desktop-database-reference/index-distinctcount-property-dao), updated 2021-09-14; remarks read 2026-11-13. Explicit stale-count/rollback/delete caveats; no exact native counter-update algorithm inferred.
[^ms-indexed]: S-MS-FIELD-INDEXED — Microsoft, [Indexed property](https://learn.microsoft.com/en-us/office/vba/access/concepts/miscellaneous/indexed-property), updated 2026-05-21; settings/remarks read 2026-11-13. Current documented field exclusions; admission/error checks on the initial native build remain planned.

[^S-JACK-CALC]: S-JACK-CALC — [CalculatedColumnUtil.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/CalculatedColumnUtil.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-COL]: S-JACK-COL — [ColumnImpl.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/ColumnImpl.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-FMT]: S-JACK-FMT — [JetFormat.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/JetFormat.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-GENERAL-PIN]: S-JACK-GENERAL-PIN — [GeneralIndexCodes.java](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/GeneralIndexCodes.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-GENERAL97-PIN]: S-JACK-GENERAL97-PIN — [General97IndexCodes.java](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/General97IndexCodes.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-IDXCACHE-PIN]: S-JACK-IDXCACHE-PIN — [IndexPageCache.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/IndexPageCache.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-IDXCODES-PIN]: S-JACK-IDXCODES-PIN — [IndexCodes.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/IndexCodes.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-IDXIMPL-PIN]: S-JACK-IDXIMPL-PIN — [IndexImpl.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/IndexImpl.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-INDEX-PIN]: S-JACK-INDEX-PIN — [IndexData.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/IndexData.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-LEGACY-PIN]: S-JACK-LEGACY-PIN — [GeneralLegacyIndexCodes.java](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/GeneralLegacyIndexCodes.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-MAP]: S-JACK-MAP — [UsageMap.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/UsageMap.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GEN]: S-JACK-RES-GEN — [index_codes_gen.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_codes_gen.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GEN-EXT]: S-JACK-RES-GEN-EXT — [index_codes_ext_gen.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_codes_ext_gen.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GEN97]: S-JACK-RES-GEN97 — [index_codes_gen_97.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_codes_gen_97.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GEN97-MAP]: S-JACK-RES-GEN97-MAP — [index_mappings_ext_gen_97.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_mappings_ext_gen_97.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GENLEG]: S-JACK-RES-GENLEG — [index_codes_genleg.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_codes_genleg.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RES-GENLEG-EXT]: S-JACK-RES-GENLEG-EXT — [index_codes_ext_genleg.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/resources/com/healthmarketscience/jackcess/index_codes_ext_genleg.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RESOURCE-LICENSE-CONTEXT]: S-JACK-RESOURCE-LICENSE-CONTEXT — [LICENSE.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/LICENSE.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RESOURCE-NOTICE-CONTEXT]: S-JACK-RESOURCE-NOTICE-CONTEXT — [NOTICE.txt](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/NOTICE.txt), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-RESOURCE-POM-CONTEXT]: S-JACK-RESOURCE-POM-CONTEXT — [pom.xml](https://raw.githubusercontent.com/jahlborn/jackcess/be0a2139b087a227d14cafd4ea231306a5a3a862/pom.xml), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.
[^S-JACK-TABLE]: S-JACK-TABLE — [TableImpl.java](https://github.com/jahlborn/jackcess/blob/be0a2139b087a227d14cafd4ea231306a5a3a862/src/main/java/com/healthmarketscience/jackcess/impl/TableImpl.java), exact Jackcess commit `be0a2139b087a227d14cafd4ea231306a5a3a862`, acquired/inspected 2026-11-13. Selected factual use; exact hash, read extent and separate code/resource licence status in A-01. No code/table port or target-ACE execution.

[^method]: L-FRANK-3; L-ASYNC-2 — Supplied `frankensqlite-report.stage1-part3.md` §8 and `asupersync-report.stage1-part2.md` §§7–8, previously read in full and revisited for the named concepts. Bounded buffers, exact baseline comparison and reproducible differential evidence only; no external repositories inspected.

[^authority]: N; C; U; A; B — Authoritative one-pager scope, compatible charter, user corrections and Stage2 continuation, governing assignment and current record. No new scope or native observation inferred.
